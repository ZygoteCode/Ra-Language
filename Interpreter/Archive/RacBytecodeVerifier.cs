using System;
using System.Collections.Generic;
using RaLanguage.Interpreter.IR;

namespace RaLanguage.Interpreter.Archive
{
    // Pre-execution structural verifier for a deserialised RaFunction.
    //
    // Goals:
    //   * every operand slot index < Function.LocalCount
    //     (accounting for the OP_WIDE prefix that promotes b/c from u8
    //      to u16 for the next instruction)
    //   * every const16 < Function.Consts.Length
    //   * every name16  < Function.Names.Length
    //   * every upval16 < Function.Upvalues.Length
    //   * every frame-slot16 < Function.SlotCount
    //   * every funcConst16 (OP_CLOSURE) < Function.Children.Length
    //   * every jump target lands inside [0, Code.Length) — including
    //     the conditional and short-circuit variants
    //   * every AST-ref byte/u16 fits its typed pool length
    //     (CastRefs / MemberAccessRefs / MemberAssignRefs /
    //     ListAssignRefs / EnumAccessRefs / TypeofRefs / NameofRefs /
    //     DerefRefs / SuperRefs / FuncDefRefs / DefineRefs / AstRefs)
    //   * every EH table entry: 0 <= StartPc < EndPc <= Code.Length,
    //     CatchPc/FinallyPc each in [0, Code.Length) when non-negative,
    //     CatchSlot < SlotCount; no two entries partially overlap
    //   * recursive validation of nested function bodies (Children[])
    //
    // The verifier runs before any VM dispatch — a fail-fast guard
    // that converts what would have been a NullReferenceException or
    // an IndexOutOfRangeException deep inside VmExecutor into a clean
    // load-time diagnostic with PC + opcode name + offending operand.
    public static class RacBytecodeVerifier
    {
        public static RacVerifyResult Verify(RaFunction fn, bool recurseChildren = true)
        {
            var diags = new List<RacVerifyDiagnostic>();
            VerifyFunction(fn, diags, recurseChildren, path: fn.Name);
            return new RacVerifyResult(diags);
        }

        private static void VerifyFunction(RaFunction fn, List<RacVerifyDiagnostic> diags,
            bool recurseChildren, string path)
        {
            if (fn == null)
            {
                diags.Add(new RacVerifyDiagnostic(path, -1, "<root>", "RaFunction is null"));
                return;
            }
            var code = fn.Code ?? Array.Empty<uint>();
            int codeLen = code.Length;
            int local = fn.LocalCount;
            int slot = fn.SlotCount;
            int constLen = fn.Consts?.Length ?? 0;
            int nameLen = fn.Names?.Length ?? 0;
            int upvalLen = fn.Upvalues?.Length ?? 0;
            int childrenLen = fn.Children?.Length ?? 0;
            int ehLen = fn.EhTable?.Length ?? 0;
            int astRefsLen = fn.AstRefs?.Length ?? 0;
            int castRefsLen = fn.CastRefs?.Length ?? 0;
            int memberAccessLen = fn.MemberAccessRefs?.Length ?? 0;
            int memberAssignLen = fn.MemberAssignRefs?.Length ?? 0;
            int listAssignLen = fn.ListAssignRefs?.Length ?? 0;
            int enumAccessLen = fn.EnumAccessRefs?.Length ?? 0;
            int typeofLen = fn.TypeofRefs?.Length ?? 0;
            int nameofLen = fn.NameofRefs?.Length ?? 0;
            int derefLen = fn.DerefRefs?.Length ?? 0;
            int superLen = fn.SuperRefs?.Length ?? 0;
            int funcDefLen = fn.FuncDefRefs?.Length ?? 0;
            int defineLen = fn.DefineRefs?.Length ?? 0;
            int declSlotLen = fn.DeclSlotByAstRef?.Length ?? 0;

            if (codeLen == 0)
            {
                diags.Add(new RacVerifyDiagnostic(path, 0, "<empty>", "function has no instructions"));
                goto EhPhase;
            }

            // Wide-prefix state. The dispatch loop promotes pending → active
            // on every instruction; we mirror exactly so the verifier sees
            // the same effective B/C as the runtime.
            int wideHiB = -1, wideHiC = -1;
            int pendingWideHiB = -1, pendingWideHiC = -1;

            for (int pc = 0; pc < codeLen; pc++)
            {
                uint instr = code[pc];
                wideHiB = pendingWideHiB;
                wideHiC = pendingWideHiC;
                pendingWideHiB = -1;
                pendingWideHiC = -1;
                var op = Encoding.DecodeOp(instr);
                string opName = op.ToString();

                if (op == Opcode.Wide)
                {
                    pendingWideHiB = (int)((instr >> 16) & 0xFF);
                    pendingWideHiC = (int)((instr >> 24) & 0xFF);
                    continue;
                }

                byte a = Encoding.A(instr);
                byte bLo = Encoding.B(instr);
                byte cLo = Encoding.C(instr);
                int effB = wideHiB >= 0 ? ((wideHiB << 8) | bLo) : bLo;
                int effC = wideHiC >= 0 ? ((wideHiC << 8) | cLo) : cLo;
                ushort imm16 = Encoding.Imm16(instr);
                short simm16 = Encoding.SImm16(instr);

                switch (op)
                {
                    // ----- Constants / loads -----
                    case Opcode.LoadConst:
                        CheckSlot(a, local, pc, opName, "a", diags, path);
                        CheckIndex(imm16, constLen, pc, opName, "const", diags, path);
                        break;
                    case Opcode.LoadNull:
                    case Opcode.LoadTrue:
                    case Opcode.LoadFalse:
                    case Opcode.GetSelf:
                    case Opcode.Pass:
                    case Opcode.Delete:
                    case Opcode.Drop:
                    case Opcode.Throw:
                    case Opcode.Ret:
                    case Opcode.Halt:
                    case Opcode.Emit:
                    case Opcode.RetNull:
                    case Opcode.PushScope:
                    case Opcode.PopScope:
                    case Opcode.ClearScope:
                    case Opcode.FinallyEnd:
                        if (op != Opcode.RetNull && op != Opcode.PushScope && op != Opcode.PopScope
                            && op != Opcode.ClearScope && op != Opcode.FinallyEnd && op != Opcode.Pass)
                            CheckSlot(a, local, pc, opName, "a", diags, path);
                        break;
                    case Opcode.LoadIntS:
                    case Opcode.LoadIntS64:
                        CheckSlot(a, local, pc, opName, "a", diags, path);
                        // signed imm16 — payload value, not an index, no bound check
                        break;

                    // ----- Variables / bindings -----
                    case Opcode.LoadGlobal:
                    case Opcode.StoreGlobal:
                    case Opcode.SetLocalDirect:
                    case Opcode.AssignBinding:
                        CheckSlot(a, local, pc, opName, "a", diags, path);
                        CheckIndex(imm16, nameLen, pc, opName, "name", diags, path);
                        break;
                    case Opcode.LoadBuiltin:
                        // builtinId16 is a lookup into the global builtin
                        // registry — not bounded by anything inside fn.
                        CheckSlot(a, local, pc, opName, "a", diags, path);
                        break;
                    case Opcode.LoadUpval:
                    case Opcode.StoreUpval:
                        CheckSlot(a, local, pc, opName, "a", diags, path);
                        CheckIndex(imm16, upvalLen, pc, opName, "upvalue", diags, path);
                        break;
                    case Opcode.LoadLocalS:
                    case Opcode.StoreLocalS:
                        CheckSlot(a, local, pc, opName, "a", diags, path);
                        CheckIndex(imm16, slot, pc, opName, "frame-slot", diags, path);
                        break;
                    case Opcode.AddIntoSlot:
                    case Opcode.SubIntoSlot:
                        CheckSlot(a, local, pc, opName, "a (rhs)", diags, path);
                        CheckIndex(imm16, slot, pc, opName, "frame-slot", diags, path);
                        break;
                    case Opcode.AddIntoSlotImm:
                    case Opcode.SubIntoSlotImm:
                        CheckIndex(a, slot, pc, opName, "frame-slot", diags, path);
                        // imm16 = signed numeric value; no index bound
                        break;
                    case Opcode.AddIntoSlotI:
                    case Opcode.SubIntoSlotI:
                        CheckSlot(a, local, pc, opName, "a (self-slot)", diags, path);
                        CheckSlot(imm16, local, pc, opName, "rhsLongSlot", diags, path);
                        break;
                    case Opcode.Declare:
                        CheckSlot(a, local, pc, opName, "a", diags, path);
                        // b = kind, c = typeConst — both are payload, no bound
                        break;
                    case Opcode.DeclareLocal:
                        CheckSlot(a, local, pc, opName, "a (src)", diags, path);
                        CheckIndex(imm16, astRefsLen, pc, opName, "AstRefs", diags, path);
                        // The declared slot identifier is held in
                        // DeclSlotByAstRef[imm16]; we cross-check that
                        // the slot is in range.
                        if (imm16 < declSlotLen && fn.DeclSlotByAstRef != null)
                        {
                            int declSlot = fn.DeclSlotByAstRef[imm16];
                            if (declSlot < 0 || declSlot >= slot)
                                diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                                    $"DeclSlotByAstRef[{imm16}] = {declSlot} is outside [0, SlotCount={slot})"));
                        }
                        break;

                    // ----- Memory model -----
                    case Opcode.Move:
                    case Opcode.MoveLet:
                    case Opcode.Alias:
                    case Opcode.Deref:
                    case Opcode.DerefStore:
                    case Opcode.UnboxI:
                    case Opcode.BoxI:
                    case Opcode.UnboxF:
                    case Opcode.BoxF:
                    case Opcode.NotB:
                    case Opcode.NegI:
                    case Opcode.NegF:
                    case Opcode.Neg:
                    case Opcode.Not:
                    case Opcode.BNot:
                    case Opcode.ListLen:
                    case Opcode.ForEachIterable:
                    case Opcode.Await:
                        CheckSlot(a, local, pc, opName, "a", diags, path);
                        CheckSlot(effB, local, pc, opName, "b", diags, path);
                        break;
                    case Opcode.Borrow:
                        CheckSlot(a, local, pc, opName, "a", diags, path);
                        CheckSlot(effB, local, pc, opName, "b", diags, path);
                        // c = mut bool, no range check
                        break;

                    // ----- Arithmetic / comparisons (all 3-address) -----
                    case Opcode.Add:
                    case Opcode.Sub:
                    case Opcode.Mul:
                    case Opcode.Div:
                    case Opcode.Mod:
                    case Opcode.Pow:
                    case Opcode.Shl:
                    case Opcode.Shr:
                    case Opcode.BAnd:
                    case Opcode.BOr:
                    case Opcode.BXor:
                    case Opcode.AddNN:
                    case Opcode.SubNN:
                    case Opcode.MulNN:
                    case Opcode.AndBB:
                    case Opcode.OrBB:
                    case Opcode.Eq:
                    case Opcode.Ne:
                    case Opcode.SEq:
                    case Opcode.SNe:
                    case Opcode.Lt:
                    case Opcode.Le:
                    case Opcode.Gt:
                    case Opcode.Ge:
                    case Opcode.NullCoal:
                    case Opcode.StrConcat:
                    case Opcode.Ushr:
                    case Opcode.Rol:
                    case Opcode.Ror:
                    case Opcode.AddII:
                    case Opcode.SubII:
                    case Opcode.MulII:
                    case Opcode.DivII:
                    case Opcode.ModII:
                    case Opcode.LtII:
                    case Opcode.LeII:
                    case Opcode.GtII:
                    case Opcode.GeII:
                    case Opcode.EqII:
                    case Opcode.NeII:
                    case Opcode.AddFF:
                    case Opcode.SubFF:
                    case Opcode.MulFF:
                    case Opcode.DivFF:
                    case Opcode.LtFF:
                    case Opcode.LeFF:
                    case Opcode.GtFF:
                    case Opcode.GeFF:
                    case Opcode.ShlII:
                    case Opcode.ShrII:
                    case Opcode.BAndII:
                    case Opcode.BOrII:
                    case Opcode.BXorII:
                    case Opcode.PowII:
                    case Opcode.PowFF:
                    case Opcode.UshrII:
                    case Opcode.RolII:
                    case Opcode.RorII:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (lhs)", diags, path);
                        CheckSlot(effC, local, pc, opName, "c (rhs)", diags, path);
                        break;

                    // ----- Short-circuit + null-coalescing jumps -----
                    case Opcode.AndJz:
                    case Opcode.OrJnz:
                    case Opcode.NCJz:
                    case Opcode.JmpIf:
                    case Opcode.JmpIfNot:
                        CheckSlot(a, local, pc, opName, "a (cond)", diags, path);
                        CheckJumpTarget(pc, simm16, codeLen, opName, diags, path);
                        break;
                    case Opcode.Jmp:
                        CheckJumpTarget(pc, simm16, codeLen, opName, diags, path);
                        break;

                    // ----- Strings / interpolation -----
                    case Opcode.Interp:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "partsBase", diags, path);
                        // Validate the run of consecutive parts slots
                        // fits in LocalCount.
                        if (effB + cLo > local)
                            diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                                $"partsBase + partsCount ({effB} + {cLo}) exceeds LocalCount ({local})"));
                        break;
                    case Opcode.Fmt:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (expr)", diags, path);
                        CheckIndex(cLo, constLen, pc, opName, "fmt-const", diags, path);
                        break;

                    // ----- Containers -----
                    case Opcode.NewList:
                    case Opcode.NewSet:
                    case Opcode.NewTuple:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "base", diags, path);
                        if (effB + cLo > local)
                            diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                                $"base + count ({effB} + {cLo}) exceeds LocalCount ({local})"));
                        break;
                    case Opcode.NewMap:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "base", diags, path);
                        if (effB + cLo * 2 > local)
                            diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                                $"map base + pairCount*2 ({effB} + {cLo}*2) exceeds LocalCount ({local})"));
                        break;
                    case Opcode.ListGet:
                    case Opcode.MapGet:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (target)", diags, path);
                        CheckSlot(effC, local, pc, opName, "c (idx/key)", diags, path);
                        break;
                    case Opcode.ListSet:
                    case Opcode.MapSet:
                        CheckSlot(a, local, pc, opName, "a (target)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (idx/key)", diags, path);
                        CheckSlot(effC, local, pc, opName, "c (src)", diags, path);
                        break;
                    case Opcode.ListPush:
                        CheckSlot(a, local, pc, opName, "a (list)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (src)", diags, path);
                        break;
                    case Opcode.Range:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "base", diags, path);
                        if (effB + 2 >= local)
                            diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                                $"range base + 2 ({effB} + 2) exceeds LocalCount ({local})"));
                        break;

                    // ----- Member / index (AST-ref pools) -----
                    case Opcode.GetMember:
                    case Opcode.GetEvent:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (recv)", diags, path);
                        CheckIndex(cLo, memberAccessLen, pc, opName, "MemberAccessRefs", diags, path);
                        break;
                    case Opcode.SetMember:
                        CheckSlot(a, local, pc, opName, "a (owner)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (val)", diags, path);
                        CheckIndex(cLo, memberAssignLen, pc, opName, "MemberAssignRefs", diags, path);
                        break;
                    case Opcode.SetIndex:
                        CheckSlot(a, local, pc, opName, "a (tgt)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (idx)", diags, path);
                        CheckIndex(cLo, listAssignLen, pc, opName, "ListAssignRefs", diags, path);
                        break;
                    case Opcode.EnumAccess:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (src)", diags, path);
                        CheckIndex(cLo, enumAccessLen, pc, opName, "EnumAccessRefs", diags, path);
                        break;
                    case Opcode.Typeof:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (src)", diags, path);
                        CheckIndex(cLo, typeofLen, pc, opName, "TypeofRefs", diags, path);
                        break;
                    case Opcode.Nameof:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckIndex(imm16, nameofLen, pc, opName, "NameofRefs", diags, path);
                        break;
                    case Opcode.Cast:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (src)", diags, path);
                        CheckIndex(cLo, castRefsLen, pc, opName, "CastRefs", diags, path);
                        break;
                    case Opcode.GetSuper:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckIndex(imm16, superLen, pc, opName, "SuperRefs", diags, path);
                        break;
                    case Opcode.DefineFunction:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckIndex(imm16, funcDefLen, pc, opName, "FuncDefRefs", diags, path);
                        break;
                    case Opcode.NativeDefine:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckIndex(imm16, defineLen, pc, opName, "DefineRefs", diags, path);
                        break;
                    case Opcode.Is:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (src)", diags, path);
                        CheckIndex(cLo, constLen, pc, opName, "typeConst", diags, path);
                        break;

                    // ----- Closures + calls -----
                    case Opcode.Closure:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckIndex(imm16, childrenLen, pc, opName, "Children (funcConst)", diags, path);
                        break;
                    case Opcode.Call:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (fn)", diags, path);
                        // Args live in slots [b+1, b+1+c). Verify the
                        // tail fits within LocalCount.
                        if (effB + 1 + cLo > local)
                            diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                                $"args tail b+1+c ({effB}+1+{cLo}) exceeds LocalCount ({local})"));
                        break;
                    case Opcode.TailCall:
                        CheckSlot(a, local, pc, opName, "a (fn)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (argBase)", diags, path);
                        if (effB + cLo > local)
                            diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                                $"args tail b+c ({effB}+{cLo}) exceeds LocalCount ({local})"));
                        break;
                    case Opcode.CallKw:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (fn)", diags, path);
                        CheckIndex(cLo, constLen, pc, opName, "payload-const", diags, path);
                        break;
                    case Opcode.CallMethod:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (recv)", diags, path);
                        CheckIndex(cLo, nameLen, pc, opName, "method-name", diags, path);
                        break;
                    case Opcode.CallSuper:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        // b/c semantics depend on overload — keep
                        // conservative slot range check.
                        CheckSlot(effB, local, pc, opName, "b", diags, path);
                        // c may be an argCount or a const idx depending
                        // on the future shape; skip strict check here
                        // until lowering is committed.
                        break;
                    case Opcode.NewInstance:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        // b carries classConst:u8 — index into Consts.
                        CheckIndex(bLo, constLen, pc, opName, "class-const", diags, path);
                        // c is argBase slot.
                        CheckSlot(cLo, local, pc, opName, "c (argBase)", diags, path);
                        break;
                    case Opcode.Spawn:
                        CheckSlot(a, local, pc, opName, "a (dst)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (fn)", diags, path);
                        if (effB + 1 + cLo > local)
                            diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                                $"args tail b+1+c ({effB}+1+{cLo}) exceeds LocalCount ({local})"));
                        break;

                    // ----- Exceptions -----
                    case Opcode.EnterTry:
                    case Opcode.LeaveTry:
                        CheckIndex(imm16, ehLen, pc, opName, "EhTable", diags, path);
                        break;

                    // ----- Reserved / not-yet-emitted -----
                    // (MatchBegin shares 0x90 with NativeDefine — match
                    // arms compile through NativeDefine + MatchNodeVisitor.
                    // The MatchBegin / MatchArm / MatchEnd opcodes are
                    // declared in the IR but never reached at this
                    // value because OP_NATIVE_DEFINE wins the switch.)
                    case Opcode.JmpFar:
                    case Opcode.MatchArm:
                    case Opcode.MatchEnd:
                    case Opcode.EmitEvent:
                    case Opcode.ForInit:
                    case Opcode.ForTest:
                    case Opcode.ForNext:
                    case Opcode.ForEachInit:
                    case Opcode.ForEachNext:
                    case Opcode.AsmInvoke:
                    case Opcode.RunPre:
                    case Opcode.RunPost:
                    case Opcode.ForAwait:
                        // Reserved by the IR but not emitted by the
                        // current IrCompiler. If a future codegen
                        // starts emitting them, extend this case with
                        // the matching operand semantics. Until then,
                        // accept silently — an archive that legitimately
                        // contains them came from a newer compiler and
                        // would already have failed an earlier format
                        // check via the manifest's
                        // RaRuntimeRequired field.
                        break;

                    case Opcode.JmpIfStream:
                        // [op][a:slot][imm16: signed forward offset].
                        CheckSlot(a, local, pc, opName, "a (cond)", diags, path);
                        CheckJumpTarget(pc, simm16, codeLen, opName, diags, path);
                        break;
                    case Opcode.ForEachStreamPull:
                        // [op][a:item][b:stream][c:continue]. Three slot
                        // operands; no jump.
                        CheckSlot(a, local, pc, opName, "a (item)", diags, path);
                        CheckSlot(effB, local, pc, opName, "b (stream)", diags, path);
                        CheckSlot(effC, local, pc, opName, "c (continue)", diags, path);
                        break;

                    default:
                        diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                            $"unknown opcode 0x{(byte)op:X2}"));
                        break;
                }
            }

            // EH table validation. Run after the per-PC walk so
            // diagnostics interleave naturally.
            EhPhase:
            if (fn.EhTable != null)
            {
                for (int i = 0; i < fn.EhTable.Length; i++)
                {
                    var eh = fn.EhTable[i];
                    string opName = $"EhTable[{i}]";
                    if (eh.StartPc < 0 || eh.StartPc > codeLen)
                        diags.Add(new RacVerifyDiagnostic(path, eh.StartPc, opName,
                            $"StartPc {eh.StartPc} out of range [0, {codeLen}]"));
                    if (eh.EndPc < 0 || eh.EndPc > codeLen)
                        diags.Add(new RacVerifyDiagnostic(path, eh.EndPc, opName,
                            $"EndPc {eh.EndPc} out of range [0, {codeLen}]"));
                    if (eh.StartPc >= eh.EndPc)
                        diags.Add(new RacVerifyDiagnostic(path, eh.StartPc, opName,
                            $"StartPc {eh.StartPc} >= EndPc {eh.EndPc} (empty / inverted range)"));
                    if (eh.CatchPc >= 0)
                    {
                        if (eh.CatchPc >= codeLen)
                            diags.Add(new RacVerifyDiagnostic(path, eh.CatchPc, opName,
                                $"CatchPc {eh.CatchPc} >= Code.Length {codeLen}"));
                    }
                    if (eh.FinallyPc >= 0)
                    {
                        if (eh.FinallyPc >= codeLen)
                            diags.Add(new RacVerifyDiagnostic(path, eh.FinallyPc, opName,
                                $"FinallyPc {eh.FinallyPc} >= Code.Length {codeLen}"));
                    }
                    if (slot > 0 && eh.CatchSlot >= slot)
                        diags.Add(new RacVerifyDiagnostic(path, eh.StartPc, opName,
                            $"CatchSlot {eh.CatchSlot} >= SlotCount {slot}"));
                    if (eh.ScopeDepth < 0)
                        diags.Add(new RacVerifyDiagnostic(path, eh.StartPc, opName,
                            $"ScopeDepth {eh.ScopeDepth} negative"));
                }
                // Forbid partial overlap. Nesting (one range strictly
                // contained in another) is fine; partial overlap
                // breaks unwind semantics.
                for (int i = 0; i < fn.EhTable.Length; i++)
                {
                    var a2 = fn.EhTable[i];
                    for (int j = i + 1; j < fn.EhTable.Length; j++)
                    {
                        var b2 = fn.EhTable[j];
                        bool overlap = a2.StartPc < b2.EndPc && b2.StartPc < a2.EndPc;
                        if (!overlap) continue;
                        bool aInsideB = b2.StartPc <= a2.StartPc && a2.EndPc <= b2.EndPc;
                        bool bInsideA = a2.StartPc <= b2.StartPc && b2.EndPc <= a2.EndPc;
                        if (!aInsideB && !bInsideA)
                            diags.Add(new RacVerifyDiagnostic(path, a2.StartPc, "EhTable",
                                $"entry {i} [{a2.StartPc},{a2.EndPc}) partially overlaps entry {j} [{b2.StartPc},{b2.EndPc})"));
                    }
                }
            }

            // Upvalues: Index is reasonable (u16 bound). When IsLocal=true the
            // index points at the parent frame's local slot; we cannot bound
            // that from here without the parent context, but the load/store
            // ops above already validate references from THIS function.
            if (fn.Upvalues != null)
            {
                for (int i = 0; i < fn.Upvalues.Length; i++)
                {
                    var uv = fn.Upvalues[i];
                    if (uv.Index > ushort.MaxValue)
                        diags.Add(new RacVerifyDiagnostic(path, -1, $"Upvalues[{i}]",
                            $"Index {uv.Index} exceeds u16 range"));
                }
            }

            // SlotNames length must match SlotCount.
            if (fn.SlotNames != null && fn.SlotNames.Length != slot)
                diags.Add(new RacVerifyDiagnostic(path, -1, "<header>",
                    $"SlotNames length {fn.SlotNames.Length} != SlotCount {slot}"));

            // PcSpans parallel-array length check.
            if (fn.PcSpansPc != null && fn.PcSpansSpan != null
                && fn.PcSpansPc.Length != fn.PcSpansSpan.Length)
                diags.Add(new RacVerifyDiagnostic(path, -1, "<header>",
                    $"PcSpansPc length {fn.PcSpansPc.Length} != PcSpansSpan length {fn.PcSpansSpan.Length}"));

            // Recurse into nested function bodies.
            if (recurseChildren && fn.Children != null)
            {
                for (int i = 0; i < fn.Children.Length; i++)
                {
                    var child = fn.Children[i];
                    if (child == null) continue;
                    string childPath = string.IsNullOrEmpty(path)
                        ? $"<children[{i}]>"
                        : $"{path}/{(string.IsNullOrEmpty(child.Name) ? "<anon>" : child.Name)}";
                    VerifyFunction(child, diags, recurseChildren: true, childPath);
                }
            }
        }

        private static void CheckSlot(int idx, int localCount, int pc, string opName,
            string field, List<RacVerifyDiagnostic> diags, string path)
        {
            if (idx < 0 || idx >= localCount)
                diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                    $"{field} slot {idx} out of range [0, LocalCount={localCount})"));
        }

        private static void CheckIndex(int idx, int poolLen, int pc, string opName,
            string poolName, List<RacVerifyDiagnostic> diags, string path)
        {
            if (idx < 0 || idx >= poolLen)
                diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                    $"{poolName} index {idx} out of range [0, {poolLen})"));
        }

        private static void CheckJumpTarget(int pc, int offs, int codeLen, string opName,
            List<RacVerifyDiagnostic> diags, string path)
        {
            // The dispatcher reads `instr` then increments pc, then
            // applies the jump offset. Mirror that here: target = (pc + 1) + offs.
            int target = pc + 1 + offs;
            if (target < 0 || target >= codeLen)
                diags.Add(new RacVerifyDiagnostic(path, pc, opName,
                    $"jump target {target} (= pc+1+offs = {pc + 1}+{offs}) out of range [0, {codeLen})"));
        }
    }

    public sealed class RacVerifyDiagnostic
    {
        public string FunctionPath { get; }
        public int Pc { get; }
        public string OpcodeName { get; }
        public string Message { get; }

        public RacVerifyDiagnostic(string path, int pc, string opName, string message)
        {
            FunctionPath = path;
            Pc = pc;
            OpcodeName = opName;
            Message = message;
        }

        public override string ToString()
        {
            string pcStr = Pc < 0 ? "<header>" : $"pc={Pc:D4}";
            return $"  {FunctionPath} {pcStr} {OpcodeName}: {Message}";
        }
    }

    public sealed class RacVerifyResult
    {
        public IReadOnlyList<RacVerifyDiagnostic> Diagnostics { get; }
        public bool Ok => Diagnostics.Count == 0;
        public int Count => Diagnostics.Count;

        public RacVerifyResult(IReadOnlyList<RacVerifyDiagnostic> diagnostics)
        {
            Diagnostics = diagnostics;
        }

        public string FormatReport()
        {
            if (Ok) return "rac-verify: OK";
            var sb = new System.Text.StringBuilder();
            sb.Append("rac-verify: FAILED (").Append(Diagnostics.Count).Append(" diagnostic")
                .Append(Diagnostics.Count == 1 ? "" : "s").Append(")\n");
            foreach (var d in Diagnostics) sb.Append(d).Append('\n');
            return sb.ToString();
        }
    }
}

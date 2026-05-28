using System.Collections.Generic;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // Physical loop-invariant code motion. Reorders `RaFunction.Code` to
    // move hoistable opcodes out of loop bodies into the loops' preheaders.
    //
    // Hoist targets (extended via multi-pass dataflow):
    //   * Constant loads (LoadConst / LoadNull / LoadTrue / LoadFalse /
    //     LoadIntS / LoadIntS64). Zero operand reads → trivially invariant.
    //   * Pure arithmetic (Add/Sub/Mul + NN/II variants, Neg/NegI/NegF,
    //     Shl/Shr/BAnd/BOr/BXor + II variants, BNot). Pure, no side
    //     effects, no error edges.
    //   * Pure copies (Move, Alias, MoveLet).
    //   * Type bridges (UnboxI, BoxI, UnboxF, BoxF).
    //
    // Hoistable iff: pure op AND every operand's def is OUTSIDE the loop
    // body OR is itself a hoistable candidate. Worklist iterates until
    // closure.
    //
    // Excluded:
    //   * Comparisons (Lt/Le/Gt/Ge/Eq/Ne + II variants). Result may feed
    //     the loop-exit JmpIfNot; hoisting would deadlock or break.
    //   * Div/Mod/Pow — error edges (division-by-zero, overflow).
    //   * Not (logical) — bool semantics edge cases.
    //
    // Safety:
    //   1. Only hoist when the loop has a clear preheader block (≥ 1
    //      predecessor of the header NOT in `loop.Body`).
    //   2. Only hoist when the slot the candidate writes has no OTHER
    //      writer inside the loop body. Re-uses of the slot by in-loop
    //      writers would observe stale values otherwise.
    //   3. Refuse the entire pass on jmp_imm16 overflow (signed 16-bit
    //      offset clamp after PC remap).
    //
    // The pass mutates `fn.Code` and rewrites every PC-dependent field
    // alongside: EhTable region PCs, PcSpansPc[], per-PC IC tables
    // (LoadGlobalIc / EnumAccessIc / CastIc / MemberAccessIc /
    // CallMethodIc). DeclSlotByAstRef is keyed by AstRefs index, not PC,
    // and stays valid without remap.
    public static class LicmHoist
    {
        public static int Apply(RaFunction fn, IrAnalysisBundle bundle)
        {
            if (fn.Code.Length == 0) return 0;
            var loops = bundle.Loops;
            if (loops.Loops.Count == 0) return 0;
            var cfg = bundle.Cfg;
            var ssa = bundle.Ssa;

            // Map every in-loop PC → (header block id, NaturalLoop).
            // Used by the worklist to iterate body PCs in a known scope.
            var pcToLoop = new Dictionary<int, (int Header, LoopAnalysis.NaturalLoop Loop)>();
            foreach (var loopKv in loops.Loops)
            {
                foreach (int blockId in loopKv.Value.Body)
                {
                    var bb = cfg.Blocks[blockId];
                    for (int pc = bb.StartPc; pc < bb.EndPcExclusive; pc++)
                        pcToLoop[pc] = (loopKv.Key, loopKv.Value);
                }
            }
            if (pcToLoop.Count == 0) return 0;

            // Worklist: candidate PCs. LICM does NOT trust
            // `LoopAnalysis.HoistableOps` directly — its local
            // `SsaForm_OperandReads` mirrors only the legacy boxed
            // opcodes and silently treats typed II / FF / BB ops as
            // operandless (returning "trivially invariant"). For an
            // AddII iter += step inside a loop, that bug would mark
            // the iter advance as hoistable. We re-validate every
            // candidate via the local `AreOperandsInvariantOrHoisted`
            // which uses the canonical `OperandReadsForLicm`.
            //
            // Seed = constant-load PCs from LoopAnalysis (always
            // operandless → always correctly marked). Other ops go
            // through the worklist with proper validation.
            var candidates = new Dictionary<int, int>(); // pc → headerBlock
            foreach (var kv in loops.HoistableOps)
            {
                uint instr = fn.Code[kv.Key];
                var op = Encoding.DecodeOp(instr);
                if (IsConstantLoadOp(op)) candidates[kv.Key] = kv.Value;
            }

            // Multi-pass worklist: extend candidates with hoist-eligible
            // pure ops whose operand defs all live outside the loop OR
            // in an already-hoisted candidate. The operand check
            // requires SSA UseVersion lookup AND a matching DefVersion;
            // an opcode whose use isn't tracked (missing UseVersion) is
            // treated as non-invariant — conservative against
            // loop-carried slots whose phi was elided by SSA
            // construction.
            //
            // LoadLocalS has a special path: it reads from
            // frame.SlotLocals[imm16] (a SymbolEntry), not from a
            // register. Operand invariance is decided by
            // `fn.MutatedNames`: the binding name resolved via
            // `fn.SlotNames[imm16]` must NOT appear in MutatedNames.
            // When that's true, the SE.Value is provably stable for
            // the duration of the function call.
            bool changed = true;
            int safetyIters = 64; // bound — typical convergence in 2-4 passes
            while (changed && safetyIters-- > 0)
            {
                changed = false;
                foreach (var kv in pcToLoop)
                {
                    int pc = kv.Key;
                    if (candidates.ContainsKey(pc)) continue;
                    uint instr = fn.Code[pc];
                    var op = Encoding.DecodeOp(instr);
                    if (!IsHoistEligibleOp(op)) continue;
                    if (!AreOperandsInvariantOrHoisted(pc, instr, op, kv.Value.Loop, ssa, cfg, candidates, fn))
                        continue;
                    candidates[pc] = kv.Value.Header;
                    changed = true;
                }
            }
            if (candidates.Count == 0) return 0;

            // Validate: slot-reuse + preheader gate per candidate.
            var insertions = new Dictionary<int, int>();
            foreach (var ckv in candidates)
            {
                int oldPc = ckv.Key;
                int headerBlock = ckv.Value;
                if (!loops.Loops.TryGetValue(headerBlock, out var loop)) continue;
                uint instr = fn.Code[oldPc];
                byte slot = Encoding.A(instr);
                if (HasOtherWriterInLoop(oldPc, slot, loop, ssa, cfg)) continue;
                var hb = cfg.Blocks[headerBlock];
                bool hasPreheader = false;
                foreach (var pred in hb.Predecessors)
                {
                    if (!loop.Body.Contains(pred)) { hasPreheader = true; break; }
                }
                if (!hasPreheader) continue;
                insertions[oldPc] = hb.StartPc;
            }
            if (insertions.Count == 0) return 0;

            // Group hoisted PCs by insertion-before PC, sort to preserve
            // relative order (data flow between hoisted ops).
            var insertionPlan = new Dictionary<int, List<int>>();
            foreach (var kv in insertions)
            {
                if (!insertionPlan.TryGetValue(kv.Value, out var list))
                {
                    list = new List<int>();
                    insertionPlan[kv.Value] = list;
                }
                list.Add(kv.Key);
            }
            foreach (var kv in insertionPlan) kv.Value.Sort();

            var hoistedSet = new HashSet<int>(insertions.Keys);

            int oldLen = fn.Code.Length;
            var newCode = new uint[oldLen];
            var newToOldPc = new int[oldLen];
            var pcMap = new int[oldLen + 1];
            int newPos = 0;
            for (int oldPc = 0; oldPc < oldLen; oldPc++)
            {
                if (insertionPlan.TryGetValue(oldPc, out var toInsert))
                {
                    foreach (var hoistedOldPc in toInsert)
                    {
                        pcMap[hoistedOldPc] = newPos;
                        newCode[newPos] = fn.Code[hoistedOldPc];
                        newToOldPc[newPos] = hoistedOldPc;
                        newPos++;
                    }
                }
                if (hoistedSet.Contains(oldPc)) continue;
                pcMap[oldPc] = newPos;
                newCode[newPos] = fn.Code[oldPc];
                newToOldPc[newPos] = oldPc;
                newPos++;
            }
            pcMap[oldLen] = newPos;

            if (newPos != oldLen) return 0; // safety net

            // Rewrite PC-relative branch offsets.
            for (int newPc = 0; newPc < oldLen; newPc++)
            {
                uint instr = newCode[newPc];
                var op = Encoding.DecodeOp(instr);
                if (!IsPcRelativeBranch(op)) continue;
                short oldOffset = unchecked((short)Encoding.Imm16(instr));
                int oldSourcePc = newToOldPc[newPc];
                int oldTargetPc = oldSourcePc + 1 + oldOffset;
                if (oldTargetPc < 0 || oldTargetPc > oldLen) return 0; // bail
                int newTargetPc = pcMap[oldTargetPc];
                int newOffset = newTargetPc - newPc - 1;
                if (newOffset < short.MinValue || newOffset > short.MaxValue)
                    return 0; // would overflow imm16 — bail out cleanly
                byte a = Encoding.A(instr);
                newCode[newPc] = Encoding.Pack2(op, a, unchecked((ushort)(short)newOffset));
            }

            // Remap EhTable.
            if (fn.EhTable != null && fn.EhTable.Length > 0)
            {
                for (int i = 0; i < fn.EhTable.Length; i++)
                {
                    var eh = fn.EhTable[i];
                    int newStart = RemapPc(pcMap, eh.StartPc, oldLen);
                    int newEnd = RemapPc(pcMap, eh.EndPc, oldLen);
                    int newCatch = eh.CatchPc < 0 ? -1 : RemapPc(pcMap, eh.CatchPc, oldLen);
                    int newFinally = eh.FinallyPc < 0 ? -1 : RemapPc(pcMap, eh.FinallyPc, oldLen);
                    fn.EhTable[i] = new ExceptionHandler(
                        newStart, newEnd, newCatch, newFinally, eh.CatchSlot, eh.ScopeDepth);
                }
            }

            // Remap PcSpansPc + sort the parallel arrays by new PC
            // (binary search at runtime requires ascending order).
            if (fn.PcSpansPc != null && fn.PcSpansPc.Length > 0)
            {
                int n = fn.PcSpansPc.Length;
                for (int i = 0; i < n; i++)
                {
                    int oldPc = fn.PcSpansPc[i];
                    if (oldPc < 0) continue;
                    fn.PcSpansPc[i] = RemapPc(pcMap, oldPc, oldLen);
                }
                // Parallel sort: pair-key by PC, value-key by SourceSpan.
                var pcs = fn.PcSpansPc;
                var spans = fn.PcSpansSpan!;
                var pairs = new (int Pc, Errors.SourceSpan Span)[n];
                for (int i = 0; i < n; i++) pairs[i] = (pcs[i], spans[i]);
                System.Array.Sort(pairs, (a, b) => a.Pc.CompareTo(b.Pc));
                for (int i = 0; i < n; i++) { pcs[i] = pairs[i].Pc; spans[i] = pairs[i].Span; }
            }

            // Re-index per-PC IC tables. Each holds runtime cache state
            // for a specific opcode at a specific PC; the new PC layout
            // requires moving cached entries to their new positions.
            RemapIcTable(ref fn.LoadGlobalIc, pcMap, oldLen);
            RemapIcTable(ref fn.EnumAccessIc, pcMap, oldLen);
            RemapIcTable(ref fn.CastIc, pcMap, oldLen);
            RemapIcTable(ref fn.MemberAccessIc, pcMap, oldLen);
            RemapIcTable(ref fn.CallMethodIc, pcMap, oldLen);

            fn.Code = newCode;
            return insertions.Count;
        }

        // For a PC in [0, oldLen] (inclusive bound = exclusive end of
        // protected region), look up the new PC. PCs beyond `oldLen`
        // get mapped to `newPos` (== oldLen).
        private static int RemapPc(int[] pcMap, int oldPc, int oldLen)
        {
            if (oldPc < 0) return oldPc;
            if (oldPc >= pcMap.Length) return pcMap[oldLen];
            return pcMap[oldPc];
        }

        private static void RemapIcTable<T>(ref T[]? table, int[] pcMap, int oldLen) where T : struct
        {
            if (table == null || table.Length == 0) return;
            var newTable = new T[table.Length];
            for (int oldPc = 0; oldPc < oldLen && oldPc < table.Length; oldPc++)
            {
                int newPc = pcMap[oldPc];
                if (newPc >= 0 && newPc < newTable.Length)
                    newTable[newPc] = table[oldPc];
            }
            table = newTable;
        }

        // Returns true iff another (non-`pc`) opcode in `loop.Body`
        // defines the same slot. Hoisting would race against the
        // in-loop writer otherwise.
        private static bool HasOtherWriterInLoop(
            int candidatePc, byte slot, LoopAnalysis.NaturalLoop loop,
            SsaForm ssa, ControlFlowGraph cfg)
        {
            foreach (var kv in ssa.DefVersions)
            {
                if (kv.Key.Pc == candidatePc) continue;
                if (kv.Key.Slot != slot) continue;
                int defPc = kv.Key.Pc;
                if (defPc < 0 || defPc >= cfg.PcToBlock.Length) continue;
                int defBlock = cfg.PcToBlock[defPc];
                if (defBlock < 0) continue;
                if (loop.Body.Contains(defBlock)) return true;
            }
            return false;
        }

        private static bool IsConstantLoadOp(Opcode op)
        {
            return op == Opcode.LoadConst
                || op == Opcode.LoadNull
                || op == Opcode.LoadTrue
                || op == Opcode.LoadFalse
                || op == Opcode.LoadIntS
                || op == Opcode.LoadIntS64;
        }

        // Full set of opcodes eligible for the multi-pass LICM hoist. All
        // are pure (no side effects) and have no error edges (no
        // divide-by-zero, no exception throws). Their result feeds
        // downstream consumers via slot reads.
        private static bool IsHoistEligibleOp(Opcode op)
        {
            if (IsConstantLoadOp(op)) return true;
            switch (op)
            {
                // Pure copies.
                case Opcode.Move:
                case Opcode.Alias:
                case Opcode.MoveLet:
                // Type bridges (zero-error: UnboxI/UnboxF deopt to Ref
                // tag on failure; BoxI/BoxF read raw bits).
                case Opcode.UnboxI:
                case Opcode.BoxI:
                case Opcode.UnboxF:
                case Opcode.BoxF:
                // Boxed arith (Add/Sub/Mul). May allocate NumberValue
                // post-cache, but that's a one-shot cost when hoisted.
                case Opcode.Add:
                case Opcode.Sub:
                case Opcode.Mul:
                case Opcode.AddNN:
                case Opcode.SubNN:
                case Opcode.MulNN:
                // Typed II arith. Hoisting preserves Int64 tag on the
                // result slot.
                case Opcode.AddII:
                case Opcode.SubII:
                case Opcode.MulII:
                // Typed FF arith. IEEE-754 semantics, no throws.
                case Opcode.AddFF:
                case Opcode.SubFF:
                case Opcode.MulFF:
                case Opcode.DivFF:
                // Unary negate / bitwise NOT.
                case Opcode.Neg:
                case Opcode.NegI:
                case Opcode.NegF:
                case Opcode.BNot:
                // Bitwise binary ops. Pure, no overflow/throw paths.
                case Opcode.Shl:
                case Opcode.Shr:
                case Opcode.Ushr:
                case Opcode.Rol:
                case Opcode.Ror:
                case Opcode.BAnd:
                case Opcode.BOr:
                case Opcode.BXor:
                case Opcode.ShlII:
                case Opcode.ShrII:
                case Opcode.UshrII:
                case Opcode.RolII:
                case Opcode.RorII:
                case Opcode.BAndII:
                case Opcode.BOrII:
                case Opcode.BXorII:
                // LoadLocalS — gated by MutatedNames check below.
                // Reads frame.SlotLocals[imm16].Value, no register
                // operand reads, so trivially passes the
                // OperandReadsForLicm-driven invariance test. The
                // real invariance check is done at the
                // AreOperandsInvariantOrHoisted entry point via the
                // SlotNames → MutatedNames bridge.
                case Opcode.LoadLocalS:
                    return true;
                default:
                    return false;
            }
        }

        // Returns true iff every operand-read slot at PC `pc` is
        // defined either OUTSIDE `loop.Body` OR by another PC already
        // in the `hoistedCandidates` set. The latter case implements
        // the multi-pass closure: a pure op whose operands flow from
        // already-hoisted ops becomes hoistable in the next iteration.
        //
        // LoadLocalS has a SymbolEntry operand source instead of a
        // register: handled by a name-based gate against
        // `fn.MutatedNames` BEFORE the register-operand walk. A
        // LoadLocalS whose binding name does not appear in
        // MutatedNames reads a stable `SymbolEntry.Value` for the
        // duration of the call → invariant.
        private static bool AreOperandsInvariantOrHoisted(
            int pc, uint instr, Opcode op,
            LoopAnalysis.NaturalLoop loop, SsaForm ssa, ControlFlowGraph cfg,
            Dictionary<int, int> hoistedCandidates, RaFunction fn)
        {
            if (op == Opcode.LoadLocalS)
            {
                // Two-tier safety check:
                //   1. Scan the loop body for direct writes to the same
                //      SymbolEntry binding (imm16 = SlotLocals index).
                //      Writer opcodes: StoreLocalS, AddIntoSlot/Sub +
                //      Imm variants, AddIntoSlotI/SubIntoSlotI.
                //   2. Name-based opcodes (SetLocalDirect, AssignBinding,
                //      DeclareLocal) inside the loop body are
                //      conservatively refused — without resolving the
                //      target name to a binding offset we cannot
                //      disprove aliasing.
                //   3. Call opcodes (Call / CallMethod / TailCall) inside
                //      the loop body could indirectly mutate any binding
                //      via captured closures. Cross-check the function-
                //      wide `MutatedNames` set: if the name appears
                //      (assignment / declaration anywhere in the
                //      function body, including nested function defs the
                //      walker descended into), refuse. Otherwise admit.
                ushort imm = Encoding.Imm16(instr);
                if (imm >= (uint)fn.SlotNames.Length) return false;
                string? name = fn.SlotNames[imm];
                if (string.IsNullOrEmpty(name)) return false;
                return !LoopBodyMutatesBinding(loop, fn, cfg, pc, imm, name);
            }
            foreach (var (slot, isUse) in OperandReadsForLicm(op, instr))
            {
                if (!isUse) continue;
                // Missing UseVersion = no SSA tracking for this read.
                // Could be a slot whose phi was elided OR a typed
                // opcode whose operands SsaForm didn't index. Either
                // way, we can't prove invariance — conservatively
                // refuse hoist.
                if (!ssa.UseVersions.TryGetValue((pc, slot), out int version))
                    return false;
                bool ok = false;
                foreach (var dkv in ssa.DefVersions)
                {
                    if (dkv.Key.Slot != slot || dkv.Value != version) continue;
                    int defPc = dkv.Key.Pc;
                    if (defPc < 0 || defPc >= cfg.PcToBlock.Length) break;
                    int defBlock = cfg.PcToBlock[defPc];
                    if (defBlock < 0) break;
                    if (!loop.Body.Contains(defBlock)) ok = true;
                    else if (hoistedCandidates.ContainsKey(defPc)) ok = true;
                    break;
                }
                if (!ok) return false;
            }
            return true;
        }

        // Operand-read enumeration for the hoist-eligible opcode set.
        // Mirrors `SsaForm.OperandReads` for the subset of opcodes we
        // care about; kept local to avoid coupling on private surface.
        private static IEnumerable<(int Slot, bool IsUse)> OperandReadsForLicm(Opcode op, uint instr)
        {
            switch (op)
            {
                // LoadConst / LoadNull / LoadTrue / LoadFalse / LoadIntS /
                // LoadIntS64 — no operand reads.
                case Opcode.LoadConst:
                case Opcode.LoadNull:
                case Opcode.LoadTrue:
                case Opcode.LoadFalse:
                case Opcode.LoadIntS:
                case Opcode.LoadIntS64:
                // LoadLocalS — Imm16 = SlotLocals offset (not a register
                // slot). Invariance is decided externally via the
                // MutatedNames bridge in AreOperandsInvariantOrHoisted.
                case Opcode.LoadLocalS:
                    yield break;
                // Unary: one operand at B.
                case Opcode.Move:
                case Opcode.Alias:
                case Opcode.MoveLet:
                case Opcode.UnboxI:
                case Opcode.BoxI:
                case Opcode.UnboxF:
                case Opcode.BoxF:
                case Opcode.Neg:
                case Opcode.NegI:
                case Opcode.NegF:
                case Opcode.BNot:
                    yield return (Encoding.B(instr), true);
                    yield break;
                // Binary: operands at B and C.
                case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
                case Opcode.AddNN: case Opcode.SubNN: case Opcode.MulNN:
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.AddFF: case Opcode.SubFF: case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.Shl: case Opcode.Shr:
                case Opcode.Ushr: case Opcode.Rol: case Opcode.Ror:
                case Opcode.BAnd: case Opcode.BOr: case Opcode.BXor:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.UshrII: case Opcode.RolII: case Opcode.RorII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                    yield return (Encoding.B(instr), true);
                    yield return (Encoding.C(instr), true);
                    yield break;
            }
        }

        // Returns true iff the loop body contains an opcode that may
        // change `frame.SlotLocals[binding].Value` between iterations.
        // Direct binding writers (StoreLocalS / AddIntoSlot family) are
        // matched on the imm16 / A operand depending on encoding.
        // Indirect mutation through Call / CallMethod / TailCall is
        // gated on whether `fn.MutatedNames` has the binding's name:
        // when absent, no static AST node assigns this name, so even a
        // callee cannot affect it from within the same scope. Name-based
        // write opcodes (SetLocalDirect / AssignBinding / DeclareLocal)
        // always refuse because their binding offset is opaque at IR.
        private static bool LoopBodyMutatesBinding(
            LoopAnalysis.NaturalLoop loop, RaFunction fn, ControlFlowGraph cfg,
            int candidatePc, ushort binding, string name)
        {
            bool nameInMutatedSet = fn.MutatedNames != null && fn.MutatedNames.Contains(name);
            foreach (int b in loop.Body)
            {
                var bb = cfg.Blocks[b];
                for (int pc = bb.StartPc; pc < bb.EndPcExclusive; pc++)
                {
                    if (pc == candidatePc) continue;
                    uint instr = fn.Code[pc];
                    var op = Encoding.DecodeOp(instr);
                    switch (op)
                    {
                        // imm16-binding direct writers.
                        case Opcode.StoreLocalS:
                        case Opcode.AddIntoSlot:
                        case Opcode.SubIntoSlot:
                            if (Encoding.Imm16(instr) == binding) return true;
                            break;
                        // A-binding direct writers (imm16 = immediate / other slot).
                        case Opcode.AddIntoSlotImm:
                        case Opcode.SubIntoSlotImm:
                        case Opcode.AddIntoSlotI:
                        case Opcode.SubIntoSlotI:
                            if (Encoding.A(instr) == binding) return true;
                            break;
                        // Name-based writers — refuse conservatively.
                        case Opcode.SetLocalDirect:
                        case Opcode.AssignBinding:
                        case Opcode.DeclareLocal:
                            return true;
                        // Indirect mutation via call → bridge through
                        // MutatedNames. If the function never assigns
                        // this name anywhere AND no imported module
                        // could capture / re-export it AND every
                        // statically-resolvable callee proves clean,
                        // callees in the same scope cannot reach it.
                        case Opcode.Call:
                        case Opcode.CallMethod:
                        case Opcode.TailCall:
                            if (nameInMutatedSet) return true;
                            // M88: cross-file gate. With imports active,
                            // a callee could live in a different
                            // module and mutate `name` via closure
                            // capture / shared mutable state — paths
                            // the in-file walker behind
                            // `fn.MutatedNames` cannot see. Consult
                            // the process-wide
                            // `ModuleManager.GlobalMutatedNames`
                            // registry first: when no loaded module
                            // ever assigns this name, no callee in
                            // any module could either, so the hoist
                            // is still safe even with imports active.
                            // When the name DOES appear in the
                            // registry (or the registry is empty
                            // because no module has loaded yet — a
                            // possibility on the importing function's
                            // first compile), refuse conservatively.
                            if (fn.HasImports)
                            {
                                var reg = RaLanguage.Interpreter.Modules.ModuleManager.GlobalMutatedNames;
                                if (reg.Count == 0 || reg.Contains(name)) return true;
                            }
                            // M88: per-call alias check. When the
                            // function value loaded into the Call's
                            // `fnSlot` traces back to a `LoadGlobal`
                            // of a known in-file name, look up that
                            // callee's `MutatedNames` and refuse iff
                            // it contains `name`. Lambdas / dynamic
                            // dispatch / cross-file calls without a
                            // direct `LoadGlobal` def fall through to
                            // the safe-refuse arm below.
                            if (op == Opcode.Call || op == Opcode.TailCall)
                            {
                                byte fnSlot = Encoding.B(instr);
                                string? calleeName = TraceFnSlotName(fn, cfg, pc, fnSlot);
                                if (calleeName != null
                                    && fn.CalleeMutatedNames != null
                                    && fn.CalleeMutatedNames.TryGetValue(calleeName, out var calleeMut))
                                {
                                    if (calleeMut.Contains(name)) return true;
                                    // Resolved + clean — continue scanning.
                                    break;
                                }
                                // CallMethod or unresolved callee:
                                // refuse conservatively.
                                return true;
                            }
                            return true;
                    }
                }
            }
            return false;
        }

        private static bool IsPcRelativeBranch(Opcode op)
        {
            return op == Opcode.Jmp
                || op == Opcode.JmpIf
                || op == Opcode.JmpIfNot
                || op == Opcode.AndJz
                || op == Opcode.OrJnz
                || op == Opcode.NCJz
                || op == Opcode.JmpIfStream;
        }

        // M88: walk backward from the Call's PC inside the same basic
        // block looking for the most-recent write to `fnSlot`. When
        // that write is a `LoadGlobal a=fnSlot, imm16=nameIdx`, return
        // the resolved name; otherwise null. Limited to within-block
        // scan because cross-block tracing would require SSA and is
        // not worth the complexity for the common pattern (`name(...)`
        // → `LoadGlobal` followed immediately by `Call`).
        private static string? TraceFnSlotName(
            RaFunction fn, ControlFlowGraph cfg, int callPc, byte fnSlot)
        {
            int blockId = cfg.PcToBlock[callPc];
            if (blockId < 0) return null;
            var bb = cfg.Blocks[blockId];
            for (int pc = callPc - 1; pc >= bb.StartPc; pc--)
            {
                uint instr = fn.Code[pc];
                var op = Encoding.DecodeOp(instr);
                if (!WritesSlot(op, instr, fnSlot)) continue;
                if (op == Opcode.LoadGlobal)
                {
                    ushort nameIdx = Encoding.Imm16(instr);
                    if (nameIdx < (uint)fn.Names.Length) return fn.Names[nameIdx];
                    return null;
                }
                // Any other writer (Move, Call result, dynamic dispatch,
                // …) — give up.
                return null;
            }
            return null;
        }

        // Lightweight predicate: returns true iff `op` writes its A
        // operand (the slot encoding) AND A == slot. Mirrors the
        // SsaForm-side `DefinedSlot` lookup but inlined here so we
        // don't depend on the full bundle.
        private static bool WritesSlot(Opcode op, uint instr, byte slot)
        {
            switch (op)
            {
                case Opcode.LoadConst:
                case Opcode.LoadNull:
                case Opcode.LoadTrue:
                case Opcode.LoadFalse:
                case Opcode.LoadIntS:
                case Opcode.LoadIntS64:
                case Opcode.LoadGlobal:
                case Opcode.LoadBuiltin:
                case Opcode.LoadUpval:
                case Opcode.LoadLocalS:
                case Opcode.Move:
                case Opcode.Alias:
                case Opcode.MoveLet:
                case Opcode.Borrow:
                case Opcode.Deref:
                case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
                case Opcode.Div: case Opcode.Mod: case Opcode.Pow:
                case Opcode.Shl: case Opcode.Shr:
                case Opcode.Ushr: case Opcode.Rol: case Opcode.Ror:
                case Opcode.BAnd: case Opcode.BOr: case Opcode.BXor:
                case Opcode.AddNN: case Opcode.SubNN: case Opcode.MulNN:
                case Opcode.AddII: case Opcode.SubII: case Opcode.MulII:
                case Opcode.DivII: case Opcode.ModII:
                case Opcode.ShlII: case Opcode.ShrII:
                case Opcode.UshrII: case Opcode.RolII: case Opcode.RorII:
                case Opcode.BAndII: case Opcode.BOrII: case Opcode.BXorII:
                case Opcode.LtII: case Opcode.LeII:
                case Opcode.GtII: case Opcode.GeII:
                case Opcode.EqII: case Opcode.NeII:
                case Opcode.Neg: case Opcode.NegI: case Opcode.NegF:
                case Opcode.Not: case Opcode.BNot:
                case Opcode.Eq: case Opcode.Ne:
                case Opcode.SEq: case Opcode.SNe:
                case Opcode.Lt: case Opcode.Le: case Opcode.Gt: case Opcode.Ge:
                case Opcode.AddFF: case Opcode.SubFF:
                case Opcode.MulFF: case Opcode.DivFF:
                case Opcode.LtFF: case Opcode.LeFF:
                case Opcode.GtFF: case Opcode.GeFF:
                case Opcode.AndBB: case Opcode.OrBB:
                case Opcode.NotB:
                case Opcode.UnboxI: case Opcode.BoxI:
                case Opcode.UnboxF: case Opcode.BoxF:
                case Opcode.PowII: case Opcode.PowFF:
                case Opcode.NullCoal:
                case Opcode.StrConcat: case Opcode.Interp: case Opcode.Fmt:
                case Opcode.NewList: case Opcode.NewMap:
                case Opcode.NewSet: case Opcode.NewTuple:
                case Opcode.ListGet: case Opcode.MapGet:
                case Opcode.Range:
                case Opcode.GetMember: case Opcode.EnumAccess:
                case Opcode.ForEachIterable: case Opcode.ListLen:
                case Opcode.ForEachStreamPull:
                case Opcode.Cast: case Opcode.Is:
                case Opcode.Typeof: case Opcode.Nameof:
                case Opcode.Closure: case Opcode.DefineFunction:
                case Opcode.GetSelf: case Opcode.GetSuper:
                case Opcode.Call: case Opcode.CallKw: case Opcode.CallMethod:
                case Opcode.NewInstance:
                case Opcode.NativeDefine:
                case Opcode.Await: case Opcode.Spawn:
                    return Encoding.A(instr) == slot;
                default:
                    return false;
            }
        }
    }
}

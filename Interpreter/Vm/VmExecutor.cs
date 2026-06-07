using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Vm
{
    // The Ra VM dispatch loop. Sole execution backend.
    //
    // Contract:
    //   - Execute returns ValueTask<RuntimeResult> so OP_AWAIT can `await` the
    //     underlying task without unwinding the C# call stack.
    //   - Errors land as RuntimeResult.Failure (never thrown) so the C# stack
    //     is not exercised on Ra-level exceptions.
    //   - The Context carries the active SymbolTable + module / annotation /
    //     borrow / async plumbing. PushScope/PopScope opcodes pivot ctx
    //     through Context.Copy() / Context.Parent.
    public sealed class VmExecutor
    {
        private readonly IInterpreter _interpreter;

        // Shared `-1` constant for OP_NEG synthesis. Allocated once per
        // process; safe because NumberValue is IsCopy and never mutated.
        private static readonly NumberValue s_negOne = new NumberValue(BigNumber.Parse("-1"));

        public VmExecutor(IInterpreter interpreter)
        {
            _interpreter = interpreter;
        }

        public async ValueTask<RuntimeResult> RunScript(RaFunction script, Context context)
        {
            // M79: pool rent for the top-level script frame. Hot when the
            // interactive menu re-runs main.ra repeatedly — each cycle
            // reuses the same script's pre-sized Slots / SlotLocals.
            var frame = VmFrame.Rent(script);
            var res = await Execute(frame, context).ConfigureAwait(false);
            if (res.Error == null) VmFrame.Return(frame);

            if (res.State == FlowState.Return)
            {
                return new RuntimeResult().Success(res.FuncReturnValue);
            }
            return res;
        }

        // M33: dispatch-depth guard. The dispatch loop is iterative, but
        // Invoke → ExecuteWithNamedArgs → Execute chains recurse through C#
        // for every user-level call. A deeply-recursive Ra script can blow
        // the C# stack and crash with StackOverflowException (which AppDomain
        // cannot catch). Bound the chain to a generous limit (3000) and
        // raise a regular RuntimeError instead so the program can attempt
        // recovery via try/catch.
        [System.ThreadStatic]
        private static int s_callDepth;
        private const int MaxCallDepth = 3000;

        public async ValueTask<RuntimeResult> Execute(VmFrame f, Context ctxArg)
        {
            if (++s_callDepth > MaxCallDepth)
            {
                s_callDepth--;
                var posOv = DummyPos(ctxArg);
                return new RuntimeResult().Failure(new Errors.Types.RuntimeError(
                    posOv, posOv,
                    $"call-depth limit exceeded ({MaxCallDepth}); possible infinite recursion",
                    ctxArg,
                    code: Errors.DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "stack would overflow",
                    help: "rewrite as iteration, raise the stack budget, or break the recursion chain"));
            }
            try
            {
            // M39: invocation counter. Bumped on every entry — sequential
            // single-threaded runtime so no Interlocked needed. Reserved
            // for any future profile-driven decision; the analysis
            // bundle that previously gated on this counter now runs
            // unconditionally at IR finalize (M64 in-place rewrite),
            // so the dispatch loop pays nothing more than the
            // increment.
            //
            // M69: state vars below are re-assigned on every TailCall
            // trampoline through `TAILCALL_RESTART`. The C# stack does
            // not grow across the tail edge — `f`/`ctx`/all hoisted
            // references swap to the callee frame in place, then the
            // dispatch loop continues. The `do { ... } while (false)`
            // wrapper plus `goto` keeps the labels' scope contained
            // without burning a `for` counter.
            uint[] code = null!;
            // M75: LocalsView replaces the legacy `LocalsView locals`
            // parallel array. Indexer-backed wrapper over `f.Slots` keeps
            // every `locals[a]` syntactic call site working while the
            // physical store is the M71 tagged-union ValueSlot[] (the
            // sole per-frame value store).
            LocalsView locals = default;
            RuntimeValue?[] consts = null!;
            string[] names = null!;
            AstNode[] astRefs = null!;
            Runtime.SymbolEntry?[] slots = null!;
            string?[] slotNames = null!;
            int[] declSlotByAstRef = null!;
            var res = new RuntimeResult();

            // ctx is the current execution scope. OP_PUSH_SCOPE replaces it
            // with a fresh child; OP_POP_SCOPE restores via Context.Parent.
            var ctx = ctxArg;

            // Hoist PC into a local int so the inner loop reads/writes a
            // register instead of an object field on every instruction. f.Pc
            // is resynced before suspension (await) and before returning so
            // callers / async resumption see a coherent value.
            int pc;

            // M82 — Wide prefix state. `pendingWideHi*` holds the high
            // bytes set by an `Opcode.Wide` instruction; `wideHi*`
            // holds the same values promoted on the NEXT iteration so
            // the handler can combine them with the low bytes of its
            // own operands. Both reset to -1 (sentinel "no wide") after
            // a single handler consumes them. The two-stage swap keeps
            // the Wide prefix scoped to exactly one following
            // instruction without per-case explicit reset code.
            int wideHiB = -1, wideHiC = -1;
            int pendingWideHiB = -1, pendingWideHiC = -1;

            // The frame `f` we were handed is owned by the EXTERNAL caller
            // (RunScript / IrExpressionEvaluator), which returns it to the pool
            // on the success path. A tail-call trampoline below replaces `f`
            // with a freshly-rented callee frame and pools the outgoing one —
            // but it must NOT pool this entry frame, or the caller's own
            // VmFrame.Return would double-return it (aliasing the same frame
            // into the pool while it is still live). Cleared after the first
            // trampoline hop, so every Execute-rented intermediate frame is
            // still pooled normally. Declared before the restart label so it
            // survives the `goto`.
            bool fIsEntryFrame = true;

            TAILCALL_RESTART:
            wideHiB = wideHiC = -1;
            pendingWideHiB = pendingWideHiC = -1;
            f.Function.InvocationCount++;
            code = f.Function.Code;
            // M75: view over the tagged-union slot store. No separate
            // Locals[] array exists anymore — see VmFrame for the
            // single-source-of-truth invariant.
            locals = new LocalsView(f.Slots);
            consts = f.Function.Consts;
            names = f.Function.Names;
            astRefs = f.Function.AstRefs;
            slots = f.SlotLocals;
            slotNames = f.Function.SlotNames;
            declSlotByAstRef = f.Function.DeclSlotByAstRef;
            pc = f.Pc;

            while (true)
            {
                // No bounds check on pc: every emit path terminates with
                // OP_HALT / OP_RET / OP_RET_NULL, so the loop always exits
                // through a return path before falling off the end.
                uint instr = code[pc++];
                var op = Encoding.DecodeOp(instr);

                // M82 — promote pending Wide state to active and clear
                // pending. The next handler (this iteration's `op`)
                // observes wideHiB/wideHiC; the NEXT iteration after
                // that resets them via the same promotion-and-clear.
                wideHiB = pendingWideHiB;
                wideHiC = pendingWideHiC;
                pendingWideHiB = -1;
                pendingWideHiC = -1;
                if (op == Opcode.Wide)
                {
                    // Stash high bytes from B / C fields. The Wide
                    // instruction itself carries no semantic action;
                    // the FOLLOWING instruction combines these with
                    // its low bytes via the wideHi* state.
                    pendingWideHiB = (int)((instr >> 16) & 0xFF);
                    pendingWideHiC = (int)((instr >> 24) & 0xFF);
                    continue;
                }

                // M71: maintain `Slots` tag coherence across slot
                // reuse. Whenever a non-II opcode writes `locals[A]`,
                // the corresponding ValueSlot's tag must be reset to
                // `Ref` so a later `TryReadAsLong` does not resurrect
                // a stale Int64 / Float64 payload. The II family
                // re-sets `Slots[A].Tag` inside `ExecuteUnboxedII`
                // after this clear, so an unconditional pre-clear
                // for every writer opcode is safe.
                //
                // The bitmap `s_writesLocalsA` is keyed by opcode tag
                // — a single byte-indexed branchless load. Gated on
                // `f.Slots.Length > 0` so functions that the M66
                // rewriter left untouched pay nothing.
                // Pre-clear removed: LocalsView's getter materializes via
                // ToRuntimeValue() when Tag != Ref, and its setter writes
                // Tag = Ref unconditionally. Every boxed handler that
                // reads via `locals[B/C]` and writes via `locals[A] = ...`
                // already enjoys the typed→boxed bridge without an
                // explicit pre-clear. Handlers that bypass the setter
                // (II / FF / BB family) manage their own Tag in their
                // case body. Eliminates a bitmap lookup + branch per
                // dispatched opcode — pure win on every hot path.

                try
                {
                switch (op)
                {
                    // -------- control --------
                    case Opcode.Halt:
                    {
                        byte a = Encoding.A(instr);
                        f.Pc = pc;
                        return res.Success(locals[a]);
                    }

                    case Opcode.Ret:
                    {
                        byte a = Encoding.A(instr);
                        f.Pc = pc;
                        return res.SuccessReturn(locals[a] ?? NullValue.Null);
                    }

                    case Opcode.RetYield:
                    {
                        // L10 — function-level `yield X` (no enclosing match/switch).
                        // Returns from the fn with FlowState.Yield, so the fn
                        // boundary takes the same `.Value` validation path the
                        // visitor's uncaught yield takes (byte-identical to OP_RET
                        // except the flow state → preserves the yield error wording).
                        byte a = Encoding.A(instr);
                        f.Pc = pc;
                        return res.SuccessYield(locals[a] ?? NullValue.Null);
                    }

                    case Opcode.RetNull:
                        f.Pc = pc;
                        return res.SuccessReturn(NullValue.Null);

                    case Opcode.Pass:
                        break;

                    // -------- loads --------
                    case Opcode.LoadConst:
                    {
                        byte a = Encoding.A(instr);
                        ushort idx = Encoding.Imm16(instr);
                        locals[a] = consts[idx];
                        break;
                    }

                    case Opcode.LoadNull:
                        locals[Encoding.A(instr)] = NullValue.Null;
                        break;

                    case Opcode.LoadTrue:
                        locals[Encoding.A(instr)] = BooleanValue.Of(true);
                        break;

                    case Opcode.LoadFalse:
                        locals[Encoding.A(instr)] = BooleanValue.Of(false);
                        break;

                    case Opcode.Move:
                    {
                        byte a = Encoding.A(instr);
                        byte b = Encoding.B(instr);
                        var src = locals[b];
                        locals[a] = src?.Aliased();
                        break;
                    }

                    // L3: `&name` (Borrow) / `&mut name` (BorrowMut). [op][dst][nameIdx:imm16].
                    // Resolve the bound name to its SymbolEntry and run the shared
                    // borrow-place rules (BorrowOps.TryBorrow) — same logic the
                    // visitor fallback uses. No sub-eval ⇒ fully synchronous.
                    case Opcode.Borrow:
                    case Opcode.BorrowMut:
                    {
                        byte a = Encoding.A(instr);
                        ushort nameIdx = Encoding.Imm16(instr);
                        var bname = names[nameIdx];
                        var (bs, be) = ResolveSpan(f, pc - 1, ctx);
                        var (bval, berr) = Runtime.BorrowOps.TryBorrow(
                            ctx, bname, op == Opcode.BorrowMut, null, bs, be);
                        if (berr != null) throw new RaUserError(berr);
                        locals[a] = bval;
                        break;
                    }

                    // L3: `*ref op= value`. [op][dst][refSlot][opTok]; the RHS
                    // value is in the contiguous slot refSlot+1. Resolve the
                    // reference, apply the (compound) operator + write through
                    // via the shared DerefStoreOps.Apply, leave the result in dst.
                    case Opcode.DerefStore:
                    {
                        byte a = Encoding.A(instr);
                        byte b = Encoding.B(instr);
                        byte opByte = Encoding.C(instr);
                        var refVal = locals[b];
                        var newVal = locals[(byte)(b + 1)];
                        if (newVal == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: DerefStore value slot is null"));
                        var (ds, de) = ResolveSpan(f, pc - 1, ctx);
                        var (dres, derr) = Runtime.DerefStoreOps.Apply(
                            refVal, newVal, (Lexer.Tokens.TokenType)opByte, ctx, ds, de);
                        if (derr != null) throw new RaUserError(derr);
                        locals[a] = dres;
                        break;
                    }

                    // M92: the hottest II ops — AddII / SubII / MulII, the
                    // loop-carried accumulator / counter arithmetic (~28% of
                    // dispatches after M90 fusion moved the comparisons into
                    // JmpNot*II) — inlined directly in the main switch. Skips
                    // both the [NoInlining] `ExecuteUnboxedII` call AND its
                    // secondary switch on every dispatch of the #1 opcode.
                    //
                    // Frame-budget discipline: the inline hot path holds only
                    // int64 temporaries (lv/rv/sum), which the JIT colors onto
                    // stack slots already used by the AddIntoSlot* cases — so
                    // the dispatch-loop MoveNext frame does not grow. The rare
                    // overflow→BigNumber box is outlined to `BoxIIOverflow`
                    // ([NoInlining]) so its BigInteger locals never touch this
                    // frame. Verified against test_deep_recursion (depth 2000).
                    case Opcode.AddII:
                    {
                        byte a = Encoding.A(instr);
                        byte b = Encoding.B(instr);
                        byte c = Encoding.C(instr);
                        if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                        { DeoptBinaryII(f, locals, a, b, c, Opcode.Add); break; }
                        long sum = lv + rv;
                        if (((lv ^ sum) & (rv ^ sum)) < 0) { BoxIIOverflow(f, a, lv, rv, Opcode.Add); break; }
                        ref var sa = ref f.Slots[a];
                        sa.Tag = ValueSlotTag.Int64; sa.Bits = sum; sa.Ref = null;
                        break;
                    }
                    case Opcode.SubII:
                    {
                        byte a = Encoding.A(instr);
                        byte b = Encoding.B(instr);
                        byte c = Encoding.C(instr);
                        if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                        { DeoptBinaryII(f, locals, a, b, c, Opcode.Sub); break; }
                        long diff = lv - rv;
                        if (((lv ^ rv) & (lv ^ diff)) < 0) { BoxIIOverflow(f, a, lv, rv, Opcode.Sub); break; }
                        ref var sa = ref f.Slots[a];
                        sa.Tag = ValueSlotTag.Int64; sa.Bits = diff; sa.Ref = null;
                        break;
                    }
                    case Opcode.MulII:
                    {
                        byte a = Encoding.A(instr);
                        byte b = Encoding.B(instr);
                        byte c = Encoding.C(instr);
                        if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                        { DeoptBinaryII(f, locals, a, b, c, Opcode.Mul); break; }
                        long hi = System.Math.BigMul(lv, rv, out long lo);
                        if (hi != (lo >> 63)) { BoxIIOverflow(f, a, lv, rv, Opcode.Mul); break; }
                        ref var sa = ref f.Slots[a];
                        sa.Tag = ValueSlotTag.Int64; sa.Bits = lo; sa.Ref = null;
                        break;
                    }

                    // M66 tagged-union opcodes (remaining). Dispatched here but
                    // the case bodies live in `ExecuteUnboxedII` so the
                    // dispatch loop's C# stack frame stays small — critical for
                    // the recursion depth limit (`test_deep_recursion.ra` depth
                    // 2000). The hot arithmetic trio above is the measured
                    // exception worth inlining.
                    case Opcode.LoadIntS64:
                    case Opcode.UnboxI:
                    case Opcode.BoxI:
                    case Opcode.LtII:
                    case Opcode.LeII:
                    case Opcode.GtII:
                    case Opcode.GeII:
                    case Opcode.EqII:
                    case Opcode.NeII:
                        ExecuteUnboxedII(f, locals, instr);
                        break;

                    // M72 FF family — Float64-tagged dispatch. Same
                    // off-stack discipline as the II family: case
                    // bodies live in `ExecuteUnboxedFF` so the
                    // dispatch-loop frame stays compact.
                    case Opcode.UnboxF:
                    case Opcode.BoxF:
                    case Opcode.AddFF:
                    case Opcode.SubFF:
                    case Opcode.MulFF:
                    case Opcode.DivFF:
                    case Opcode.LtFF:
                    case Opcode.LeFF:
                    case Opcode.GtFF:
                    case Opcode.GeFF:
                        ExecuteUnboxedFF(f, locals, instr);
                        break;

                    // M73 BB family — Bool-tagged dispatch. Same
                    // off-stack discipline as the II / FF
                    // families.
                    case Opcode.AndBB:
                    case Opcode.OrBB:
                    case Opcode.NotB:
                        ExecuteUnboxedBB(f, locals, instr);
                        break;

                    // M68 extended II/FF dispatch — Div / Mod /
                    // bitwise / negate. Off-stack helper to keep
                    // the dispatch loop's C# frame compact.
                    case Opcode.DivII:
                    case Opcode.ModII:
                    case Opcode.ShlII:
                    case Opcode.ShrII:
                    case Opcode.UshrII:
                    case Opcode.RolII:
                    case Opcode.RorII:
                    case Opcode.BAndII:
                    case Opcode.BOrII:
                    case Opcode.BXorII:
                    case Opcode.NegI:
                    case Opcode.NegF:
                    // M80 typed power.
                    case Opcode.PowII:
                    case Opcode.PowFF:
                        ExecuteUnboxedExtII(f, locals, instr);
                        break;

                    case Opcode.LoadGlobal:
                    {
                        // M23.1: per-PC inline cache. Hit if cached
                        // (Table, Gen) match current leaf. SymbolEntry is
                        // mutated in place by TryAssign so parent-table
                        // writes propagate via the cached pointer without
                        // invalidating the cache; leaf shadowing bumps
                        // LocalGeneration → miss → refresh.
                        byte a = Encoding.A(instr);
                        ushort idx = Encoding.Imm16(instr);
                        var name = names[idx];
                        var icArr = f.Function.LoadGlobalIc;
                        int icPc = pc - 1;
                        Runtime.SymbolEntry? entry = null;
                        var leafTable = ctx.SymbolTable!;
                        if (icArr != null && (uint)icPc < (uint)icArr.Length)
                        {
                            ref var slot = ref icArr[icPc];
                            if (slot.Entry != null
                                && ReferenceEquals(slot.Table, leafTable)
                                && slot.Gen == leafTable.LocalGeneration)
                            {
                                entry = slot.Entry;
                            }
                            else
                            {
                                entry = leafTable.GetEntry(name);
                                if (entry != null)
                                {
                                    slot.Table = leafTable;
                                    slot.Gen = leafTable.LocalGeneration;
                                    slot.Entry = entry;
                                }
                            }
                        }
                        else
                        {
                            entry = leafTable.GetEntry(name);
                        }
                        if (entry == null)
                        {
                            // M83 — emit real source position via
                            // PcSpansPc lookup instead of the legacy
                            // DummyPos fallback that surfaced as
                            // `<vm>:1:1` to users.
                            var (s1, e1) = ResolveSpan(f, pc - 1, ctx);
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                s1, e1,
                                $"'{name}' is not defined", ctx,
                                code: Errors.DiagnosticCode.RuntimeUndefinedSymbol,
                                primaryLabel: "no such symbol in scope",
                                help: $"declare '{name}' with 'var', 'let', 'const' or 'final' before using it, or check the spelling"));
                        }
                        if (entry.IsMoved)
                        {
                            var (s2, e2) = ResolveSpan(f, pc - 1, ctx);
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                s2, e2,
                                $"value of '{name}' was already moved", ctx,
                                code: Errors.DiagnosticCode.RuntimeMovedValue,
                                primaryLabel: "used here after move",
                                help: "non-copy 'let' bindings transfer ownership on use; rebind the value or take a copy"));
                        }
                        if (entry.HasMutableBorrow)
                        {
                            var (s3, e3) = ResolveSpan(f, pc - 1, ctx);
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                s3, e3,
                                $"cannot read '{name}': it is exclusively borrowed by a '&mut'", ctx,
                                code: Errors.DiagnosticCode.RuntimeBorrowViolation,
                                primaryLabel: "binding is exclusively borrowed",
                                help: "access the value through the existing '&mut' borrow with '*ref', or wait until the borrow's scope ends"));
                        }

                        var entryValue = entry.Value;
                        if (entryValue.Type == RuntimeValueType.StructInstance
                            || entryValue.Type == RuntimeValueType.ClassInstance
                            || entryValue.Type == RuntimeValueType.Enum
                            || entryValue.Type == RuntimeValueType.EnumType)
                        {
                            locals[a] = entryValue.SetContext(ctx);
                            break;
                        }
                        locals[a] = entryValue.Aliased().SetContext(ctx);
                        break;
                    }

                    // M14 slot-based local read. The slot was populated when
                    // the originating OP_DECLARE_LOCAL ran (also cached when
                    // function parameters are bound). All Ra borrow / move
                    // guards still apply — the cached SymbolEntry carries
                    // those flags. Compared to OP_LOAD_GLOBAL this eliminates
                    // the SymbolTable.GetEntry name walk on every read.
                    case Opcode.LoadLocalS:
                    {
                        byte a = Encoding.A(instr);
                        ushort slot = Encoding.Imm16(instr);
                        var entry = slot < (uint)slots.Length ? slots[slot] : null;
                        if (entry == null)
                        {
                            // Lazy slot population: bindings that the Resolver
                            // pinned to this slot but that were NOT created
                            // through OP_DECLARE_LOCAL (function defs via
                            // OP_DEFINE_FUNCTION, dynamically-injected names,
                            // imports populated mid-flight) still live in
                            // ctx.SymbolTable. Resolve once by name and cache
                            // the entry so subsequent reads are fast.
                            string? lazyName = slot < (uint)slotNames.Length ? slotNames[slot] : null;
                            if (!string.IsNullOrEmpty(lazyName))
                            {
                                entry = ctx.SymbolTable!.GetEntry(lazyName!);
                                if (entry != null && slot < (uint)slots.Length) slots[slot] = entry;
                            }
                        }
                        if (entry == null)
                        {
                            string? n = slot < (uint)slotNames.Length ? slotNames[slot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"'{n ?? "<slot>"}' is not defined", ctx,
                                code: Errors.DiagnosticCode.RuntimeUndefinedSymbol,
                                primaryLabel: "no such symbol in scope",
                                help: $"declare '{n ?? "<slot>"}' with 'var', 'let', 'const' or 'final' before using it, or check the spelling"));
                        }
                        if (entry.IsMoved)
                        {
                            string? n = slot < (uint)slotNames.Length ? slotNames[slot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"value of '{n ?? "<slot>"}' was already moved", ctx,
                                code: Errors.DiagnosticCode.RuntimeMovedValue,
                                primaryLabel: "used here after move",
                                help: "non-copy 'let' bindings transfer ownership on use; rebind the value or take a copy"));
                        }
                        if (entry.HasMutableBorrow)
                        {
                            string? n = slot < (uint)slotNames.Length ? slotNames[slot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"cannot read '{n ?? "<slot>"}': it is exclusively borrowed by a '&mut'", ctx,
                                code: Errors.DiagnosticCode.RuntimeBorrowViolation,
                                primaryLabel: "binding is exclusively borrowed",
                                help: "access the value through the existing '&mut' borrow with '*ref', or wait until the borrow's scope ends"));
                        }
                        var ev = entry.Value;
                        // M26.3: dominant types in tight loops (Number,
                        // Boolean, Null, String) return `this` from Aliased()
                        // because IsCopy=true and Copy()=this. Skip the
                        // virtual call entirely — semantics identical, one
                        // less indirect dispatch per slot read. Reference-
                        // backed types (StructInstance / ClassInstance / Enum
                        // / EnumType) also already skipped Aliased in the
                        // previous path; merged into a single fast branch.
                        var et = ev.Type;
                        if (et == RuntimeValueType.Number
                            || et == RuntimeValueType.Boolean
                            || et == RuntimeValueType.Null
                            || et == RuntimeValueType.String
                            || et == RuntimeValueType.Integer
                            || et == RuntimeValueType.Long
                            || et == RuntimeValueType.StructInstance
                            || et == RuntimeValueType.ClassInstance
                            || et == RuntimeValueType.Enum
                            || et == RuntimeValueType.EnumType)
                        {
                            locals[a] = ev.SetContext(ctx);
                            break;
                        }
                        locals[a] = ev.Aliased().SetContext(ctx);
                        break;
                    }

                    // M14 slot-based local write (plain `=` only; compound
                    // forms still route through OP_STORE_GLOBAL because they
                    // need AssignmentHelper for operator selection / coercion).
                    case Opcode.StoreLocalS:
                    {
                        byte src = Encoding.A(instr);
                        ushort slot = Encoding.Imm16(instr);
                        var entry = slot < (uint)slots.Length ? slots[slot] : null;
                        if (entry == null)
                        {
                            string? lazyName = slot < (uint)slotNames.Length ? slotNames[slot] : null;
                            if (!string.IsNullOrEmpty(lazyName))
                            {
                                entry = ctx.SymbolTable!.GetEntry(lazyName!);
                                if (entry != null && slot < (uint)slots.Length) slots[slot] = entry;
                            }
                        }
                        if (entry == null)
                        {
                            string? n = slot < (uint)slotNames.Length ? slotNames[slot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"'{n ?? "<slot>"}' is not defined", ctx,
                                code: Errors.DiagnosticCode.RuntimeUndefinedSymbol,
                                primaryLabel: "no such symbol in scope",
                                help: "declare the binding before assigning to it"));
                        }
                        if (!entry.IsMutable)
                        {
                            string? n = slot < (uint)slotNames.Length ? slotNames[slot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"cannot assign to '{n ?? "<slot>"}': binding is not mutable", ctx,
                                code: Errors.DiagnosticCode.RuntimeImmutableBinding,
                                primaryLabel: "immutable binding",
                                help: "declare with 'var' or 'let mut' to allow reassignment"));
                        }
                        if (entry.HasMutableBorrow || entry.SharedBorrowCount > 0)
                        {
                            string? n = slot < (uint)slotNames.Length ? slotNames[slot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"cannot assign to '{n ?? "<slot>"}': binding is currently borrowed", ctx,
                                code: Errors.DiagnosticCode.RuntimeBorrowViolation,
                                primaryLabel: "active borrow blocks assignment",
                                help: "wait for the borrow to fall out of scope before reassigning"));
                        }
                        var newValue = locals[src];
                        if (newValue == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: StoreLocalS src is null"));
                        // Borrow rebind: when the slot currently holds an
                        // alive BorrowValue and we are reseating it, release
                        // the prior borrow so the source binding's borrow
                        // counters drop back. AssignmentHelper does this for
                        // the slow path; the slot fast path must mirror.
                        if (entry.Value is Values.Primitives.BorrowValue oldBorrow_sl)
                            oldBorrow_sl.Release();
                        // The store reseats the binding: the previous value
                        // is dropped, and the slot now owns a fresh value.
                        // Always clear IsMoved so subsequent reads see the
                        // new value as live (matters for `let mut` borrow
                        // rebinds such as `let mut r = &a; r = &b;`).
                        entry.Value = newValue;
                        entry.IsMoved = false;
                        break;
                    }

                    // M27.2 fused increment/decrement: `slot = slot ± rhs` in a
                    // single dispatch. Shares the slot lookup + lazy resolve +
                    // borrow / mutability checks with StoreLocalS, then folds in
                    // the Add/Sub Binary fast path (int64 branchless overflow).
                    // Compile-time gating (IrCompiler.TryEmitSelfAdditiveSlot)
                    // restricts the source RHS to side-effect-free shapes, so
                    // reading entry.Value here and writing back below mirrors
                    // the unfused `LoadLocalS → Add → StoreLocalS` sequence with
                    // identical observable semantics.
                    case Opcode.AddIntoSlot:
                    case Opcode.SubIntoSlot:
                    {
                        byte rhsSlot = Encoding.A(instr);
                        ushort selfSlot = Encoding.Imm16(instr);
                        var entry = selfSlot < (uint)slots.Length ? slots[selfSlot] : null;
                        if (entry == null)
                        {
                            string? lazyName = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            if (!string.IsNullOrEmpty(lazyName))
                            {
                                entry = ctx.SymbolTable!.GetEntry(lazyName!);
                                if (entry != null && selfSlot < (uint)slots.Length) slots[selfSlot] = entry;
                            }
                        }
                        if (entry == null)
                        {
                            string? n = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"'{n ?? "<slot>"}' is not defined", ctx,
                                code: Errors.DiagnosticCode.RuntimeUndefinedSymbol,
                                primaryLabel: "no such symbol in scope",
                                help: "declare the binding before assigning to it"));
                        }
                        if (!entry.IsMutable)
                        {
                            string? n = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"cannot assign to '{n ?? "<slot>"}': binding is not mutable", ctx,
                                code: Errors.DiagnosticCode.RuntimeImmutableBinding,
                                primaryLabel: "immutable binding",
                                help: "declare with 'var' or 'let mut' to allow reassignment"));
                        }
                        if (entry.HasMutableBorrow || entry.SharedBorrowCount > 0)
                        {
                            string? n = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"cannot assign to '{n ?? "<slot>"}': binding is currently borrowed", ctx,
                                code: Errors.DiagnosticCode.RuntimeBorrowViolation,
                                primaryLabel: "active borrow blocks assignment",
                                help: "wait for the borrow to fall out of scope before reassigning"));
                        }
                        if (entry.IsMoved)
                        {
                            string? n = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"value of '{n ?? "<slot>"}' was already moved", ctx,
                                code: Errors.DiagnosticCode.RuntimeMovedValue,
                                primaryLabel: "used here after move",
                                help: "non-copy 'let' bindings transfer ownership on use"));
                        }
                        var leftVal = entry.Value;
                        var rightVal = locals[rhsSlot];
                        if (rightVal == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: AddIntoSlot rhs is null"));
                        bool isAdd = (Opcode)(instr & 0xFF) == Opcode.AddIntoSlot;
                        RuntimeValue? produced = null;
                        if (leftVal.Type == RuntimeValueType.Number && rightVal.Type == RuntimeValueType.Number)
                        {
                            var ln = (NumberValue)leftVal;
                            var rn = (NumberValue)rightVal;
                            if (TryGetInt64(ln, out long lv) && TryGetInt64(rn, out long rv))
                            {
                                if (isAdd)
                                {
                                    long sum = lv + rv;
                                    if (((lv ^ sum) & (rv ^ sum)) >= 0)
                                        produced = NumberValue.OfInt64(sum);
                                }
                                else
                                {
                                    long diff = lv - rv;
                                    if (((lv ^ rv) & (lv ^ diff)) >= 0)
                                        produced = NumberValue.OfInt64(diff);
                                }
                            }
                        }
                        if (produced == null)
                        {
                            var r = isAdd ? leftVal.AddedTo(rightVal) : leftVal.SubbedBy(rightVal);
                            if (r.Error != null) throw new RaUserError(r.Error);
                            produced = r.Value!;
                        }
                        // See StoreLocalS comment: reseat clears IsMoved
                        // because the slot now owns a fresh value.
                        entry.Value = produced;
                        entry.IsMoved = false;
                        break;
                    }

                    // O(n) string building. A loop string accumulator's
                    // `s = s + x` self-appends into a per-frame StringBuilder
                    // instead of reallocating the whole string each iteration.
                    case Opcode.StrAccBegin:
                    {
                        byte a = Encoding.A(instr);
                        ushort accIdx = Encoding.Imm16(instr);
                        var sv = locals[a];
                        string seed = sv is StringValue sstr0 ? sstr0.Value
                            : (sv == null ? "" : Utilities.StringConversionUtility.ConvertToString(sv));
                        if (accIdx < (uint)f.StrAcc.Length)
                            f.StrAcc[accIdx] = new System.Text.StringBuilder(seed);
                        break;
                    }
                    case Opcode.StrAccAppend:
                    {
                        byte a = Encoding.A(instr);
                        ushort accIdx = Encoding.Imm16(instr);
                        var v = locals[a];
                        if (accIdx < (uint)f.StrAcc.Length)
                        {
                            var sb = f.StrAcc[accIdx] ?? (f.StrAcc[accIdx] = new System.Text.StringBuilder());
                            // Match StringValue.AddedTo's right-operand coercion
                            // exactly so the built string is identical to the
                            // boxed `s + x` chain.
                            if (v is StringValue vstr) sb.Append(vstr.Value);
                            else if (v != null) sb.Append(Utilities.StringConversionUtility.ConvertToString(v));
                        }
                        break;
                    }
                    case Opcode.StrAccMaterialize:
                    {
                        byte a = Encoding.A(instr);
                        ushort accIdx = Encoding.Imm16(instr);
                        var sb = accIdx < (uint)f.StrAcc.Length ? f.StrAcc[accIdx] : null;
                        locals[a] = new StringValue(sb?.ToString() ?? "").SetContext(ctx);
                        break;
                    }
                    case Opcode.StrAccAppendI:
                    {
                        byte a = Encoding.A(instr);
                        ushort accIdx = Encoding.Imm16(instr);
                        if (accIdx < (uint)f.StrAcc.Length)
                        {
                            var sb = f.StrAcc[accIdx] ?? (f.StrAcc[accIdx] = new System.Text.StringBuilder());
                            ref var slotRef = ref f.Slots[a];
                            if (slotRef.Tag == ValueSlotTag.Int64)
                            {
                                // Decimal form of the int64 == NumberValue's
                                // integer string (InvariantGlobalization), so the
                                // built string matches the boxed `s + i` chain.
                                sb.Append(slotRef.Bits);
                            }
                            else
                            {
                                // Deopt: the iter slot drifted off Int64 (boxed).
                                // Fall back to the same coercion StrAccAppend uses.
                                var boxed = slotRef.ToRuntimeValue();
                                if (boxed is StringValue bvs) sb.Append(bvs.Value);
                                else if (boxed != null) sb.Append(Utilities.StringConversionUtility.ConvertToString(boxed));
                            }
                        }
                        break;
                    }

                    // Typed-RHS fused self-additive slot. Layout matches
                    // AddIntoSlot ([op][rhsSlot:u8][selfSlot:u16]) but rhs
                    // is read DIRECTLY from `f.Slots[rhsSlot].Bits` as an
                    // int64 — no boxed mirror needed. Eliminates the
                    // body's `LoadLocalS` (and the iter-mirror BoxI when
                    // the publish has been elided) for the common
                    // `sum = sum + i` shape inside `for i = lit to lit`.
                    case Opcode.AddIntoSlotI:
                    case Opcode.SubIntoSlotI:
                    {
                        byte rhsLongSlot = Encoding.A(instr);
                        ushort selfSlotI = Encoding.Imm16(instr);
                        var entryI = selfSlotI < (uint)slots.Length ? slots[selfSlotI] : null;
                        if (entryI == null)
                        {
                            string? lazyName = selfSlotI < (uint)slotNames.Length ? slotNames[selfSlotI] : null;
                            if (!string.IsNullOrEmpty(lazyName))
                            {
                                entryI = ctx.SymbolTable!.GetEntry(lazyName!);
                                if (entryI != null && selfSlotI < (uint)slots.Length) slots[selfSlotI] = entryI;
                            }
                        }
                        if (entryI == null)
                        {
                            string? n = selfSlotI < (uint)slotNames.Length ? slotNames[selfSlotI] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"'{n ?? "<slot>"}' is not defined", ctx,
                                code: Errors.DiagnosticCode.RuntimeUndefinedSymbol,
                                primaryLabel: "no such symbol in scope",
                                help: "declare the binding before assigning to it"));
                        }
                        if (!entryI.IsMutable)
                        {
                            string? n = selfSlotI < (uint)slotNames.Length ? slotNames[selfSlotI] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"cannot assign to '{n ?? "<slot>"}': binding is not mutable", ctx,
                                code: Errors.DiagnosticCode.RuntimeImmutableBinding,
                                primaryLabel: "immutable binding",
                                help: "declare with 'var' or 'let mut' to allow reassignment"));
                        }
                        if (entryI.HasMutableBorrow || entryI.SharedBorrowCount > 0)
                        {
                            string? n = selfSlotI < (uint)slotNames.Length ? slotNames[selfSlotI] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"cannot assign to '{n ?? "<slot>"}': binding is currently borrowed", ctx,
                                code: Errors.DiagnosticCode.RuntimeBorrowViolation,
                                primaryLabel: "active borrow blocks assignment",
                                help: "wait for the borrow to fall out of scope before reassigning"));
                        }
                        if (entryI.IsMoved)
                        {
                            string? n = selfSlotI < (uint)slotNames.Length ? slotNames[selfSlotI] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"value of '{n ?? "<slot>"}' was already moved", ctx,
                                code: Errors.DiagnosticCode.RuntimeMovedValue,
                                primaryLabel: "used here after move",
                                help: "non-copy 'let' bindings transfer ownership on use"));
                        }
                        // Typed RHS read: the IR compiler only emits this
                        // opcode when the source slot is provably Int64-
                        // tagged (lazy-long for-loop iter). At runtime we
                        // verify defensively and deopt to the boxed
                        // helper if the tag has drifted.
                        bool isAddI = (Opcode)(instr & 0xFF) == Opcode.AddIntoSlotI;
                        ref var rhsSlotRef = ref f.Slots[rhsLongSlot];
                        long rvI;
                        if (rhsSlotRef.Tag == ValueSlotTag.Int64)
                        {
                            rvI = rhsSlotRef.Bits;
                        }
                        else
                        {
                            // Deopt: slot is boxed. Pull via ToRuntimeValue
                            // and route through the boxed AddIntoSlot path
                            // semantics.
                            var rhsBoxedDeopt = rhsSlotRef.ToRuntimeValue();
                            var leftValDeopt = entryI.Value;
                            var rDeopt = isAddI ? leftValDeopt.AddedTo(rhsBoxedDeopt) : leftValDeopt.SubbedBy(rhsBoxedDeopt);
                            if (rDeopt.Error != null) throw new RaUserError(rDeopt.Error);
                            entryI.Value = rDeopt.Value!;
                            entryI.IsMoved = false;
                            break;
                        }
                        var leftValI = entryI.Value;
                        RuntimeValue? producedI = null;
                        if (leftValI.Type == RuntimeValueType.Number)
                        {
                            var lnI = (NumberValue)leftValI;
                            if (TryGetInt64(lnI, out long lvI))
                            {
                                if (isAddI)
                                {
                                    long sumI = lvI + rvI;
                                    if (((lvI ^ sumI) & (rvI ^ sumI)) >= 0)
                                        producedI = NumberValue.OfInt64(sumI);
                                }
                                else
                                {
                                    long diffI = lvI - rvI;
                                    if (((lvI ^ rvI) & (lvI ^ diffI)) >= 0)
                                        producedI = NumberValue.OfInt64(diffI);
                                }
                            }
                        }
                        if (producedI == null)
                        {
                            // Overflow / scale mismatch: box rhs and route
                            // through the boxed dispatch path. Matches the
                            // AddIntoSlot fallback semantics.
                            var rhsBoxedSlow = NumberValue.OfInt64(rvI);
                            var rSlow = isAddI ? leftValI.AddedTo(rhsBoxedSlow) : leftValI.SubbedBy(rhsBoxedSlow);
                            if (rSlow.Error != null) throw new RaUserError(rSlow.Error);
                            producedI = rSlow.Value!;
                        }
                        entryI.Value = producedI;
                        entryI.IsMoved = false;
                        break;
                    }

                    // M27.5 inlined-immediate fused increment. Layout:
                    // [op][slot:u8][simm16]. The RHS is encoded directly in the
                    // instruction, so this opcode dispatches once with zero
                    // const-pool reads and zero temp-slot writes. Restricted
                    // by the IR compiler to slot ≤ 255 and literal in
                    // [-32768..32767]; larger frames / wider literals fall
                    // back to AddIntoSlot.
                    case Opcode.AddIntoSlotImm:
                    case Opcode.SubIntoSlotImm:
                    {
                        byte selfSlotByte = Encoding.A(instr);
                        short simm = Encoding.SImm16(instr);
                        ushort selfSlot = selfSlotByte;
                        var entry = selfSlot < (uint)slots.Length ? slots[selfSlot] : null;
                        if (entry == null)
                        {
                            string? lazyName = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            if (!string.IsNullOrEmpty(lazyName))
                            {
                                entry = ctx.SymbolTable!.GetEntry(lazyName!);
                                if (entry != null && selfSlot < (uint)slots.Length) slots[selfSlot] = entry;
                            }
                        }
                        if (entry == null)
                        {
                            string? n = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"'{n ?? "<slot>"}' is not defined", ctx,
                                code: Errors.DiagnosticCode.RuntimeUndefinedSymbol,
                                primaryLabel: "no such symbol in scope",
                                help: "declare the binding before assigning to it"));
                        }
                        if (!entry.IsMutable)
                        {
                            string? n = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"cannot assign to '{n ?? "<slot>"}': binding is not mutable", ctx,
                                code: Errors.DiagnosticCode.RuntimeImmutableBinding,
                                primaryLabel: "immutable binding",
                                help: "declare with 'var' or 'let mut' to allow reassignment"));
                        }
                        if (entry.HasMutableBorrow || entry.SharedBorrowCount > 0)
                        {
                            string? n = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"cannot assign to '{n ?? "<slot>"}': binding is currently borrowed", ctx,
                                code: Errors.DiagnosticCode.RuntimeBorrowViolation,
                                primaryLabel: "active borrow blocks assignment",
                                help: "wait for the borrow to fall out of scope before reassigning"));
                        }
                        if (entry.IsMoved)
                        {
                            string? n = selfSlot < (uint)slotNames.Length ? slotNames[selfSlot] : null;
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                DummyPos(ctx), DummyPos(ctx),
                                $"value of '{n ?? "<slot>"}' was already moved", ctx,
                                code: Errors.DiagnosticCode.RuntimeMovedValue,
                                primaryLabel: "used here after move",
                                help: "non-copy 'let' bindings transfer ownership on use"));
                        }
                        bool isAddImm = (Opcode)(instr & 0xFF) == Opcode.AddIntoSlotImm;
                        var leftVal = entry.Value;
                        RuntimeValue? producedImm = null;
                        if (leftVal.Type == RuntimeValueType.Number)
                        {
                            var ln = (NumberValue)leftVal;
                            if (TryGetInt64(ln, out long lv))
                            {
                                long rv = simm;
                                if (isAddImm)
                                {
                                    long sum = lv + rv;
                                    if (((lv ^ sum) & (rv ^ sum)) >= 0)
                                        producedImm = NumberValue.OfInt64(sum);
                                }
                                else
                                {
                                    long diff = lv - rv;
                                    if (((lv ^ rv) & (lv ^ diff)) >= 0)
                                        producedImm = NumberValue.OfInt64(diff);
                                }
                            }
                        }
                        if (producedImm == null)
                        {
                            // Slow path: materialise the literal as a NumberValue
                            // and dispatch the virtual operator. Mirrors the
                            // semantics AddIntoSlot uses when the int64 fast
                            // path doesn't apply (decimal/scale targets, BigNumber
                            // beyond int64, etc.).
                            var rhsVal = NumberValue.OfInt64(simm);
                            var rr = isAddImm ? leftVal.AddedTo(rhsVal) : leftVal.SubbedBy(rhsVal);
                            if (rr.Error != null) throw new RaUserError(rr.Error);
                            producedImm = rr.Value!;
                        }
                        // See StoreLocalS comment: reseat clears IsMoved.
                        entry.Value = producedImm;
                        entry.IsMoved = false;
                        break;
                    }

                    // -------- scope management (M4) --------
                    case Opcode.PushScope:
                    {
                        ctx = ctx.Copy();
                        f.CtxDepth++;
                        break;
                    }
                    case Opcode.PopScope:
                    {
                        var parent = ctx.Parent;
                        if (parent == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: PopScope without matching PushScope"));
                        // M21.1: release any borrows held by entries in the
                        // leaving scope so the source SymbolEntry's borrow
                        // counter is correctly decremented. Mirrors the
                        // SymbolTable.ReleaseLocalBorrows() call that
                        // ScopeNodeVisitor performs on scope exit. Without
                        // this, `{ var r = &mut x; ... }` leaves x flagged
                        // as exclusively borrowed forever.
                        ctx.SymbolTable?.ReleaseLocalBorrows();
                        ctx = parent;
                        f.CtxDepth--;
                        break;
                    }
                    case Opcode.ClearScope:
                    {
                        ctx.SymbolTable?.Clear();
                        break;
                    }
                    case Opcode.SetLocalDirect:
                    {
                        byte src = Encoding.A(instr);
                        ushort idx = Encoding.Imm16(instr);
                        var name = names[idx];
                        var value = locals[src];
                        if (value == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: SetLocalDirect src is null"));
                        ctx.SymbolTable!.SetLocal(name, value);
                        // M14: when this name maps to a frame slot, repoint the
                        // slot to the freshly-created SymbolEntry. For-loop iter
                        // variables hit this path on every outer iteration and
                        // would otherwise leave the slot referencing the prior
                        // iter scope's (now-orphaned) entry.
                        var n2s = f.Function.NameToSlot;
                        if (n2s != null && n2s.TryGetValue(name, out int s2) && s2 < slots.Length)
                        {
                            slots[s2] = ctx.SymbolTable.GetLocalEntry(name);
                        }
                        break;
                    }
                    case Opcode.DeleteLocal:
                    {
                        // L10: `del name`. Mirrors VariableDeleteNodeVisitor.Apply
                        // for a single name (the IR emits one DeleteLocal per name
                        // in a `del a, b`): look the name up, raise the visitor's
                        // exact "does not exist" error if absent, else remove it.
                        // `a` is unused; the name lives in the Names pool at imm16.
                        ushort idx = Encoding.Imm16(instr);
                        var name = names[idx];
                        var existing = ctx.SymbolTable!.Get(name);
                        if (existing == null)
                        {
                            var (ds, de) = ResolveSpan(f, pc - 1, ctx);
                            throw new RaUserError(new Errors.Types.RuntimeError(
                                ds, de,
                                $"'{name}' variable does not exist", ctx));
                        }
                        ctx.SymbolTable.Remove(name);
                        break;
                    }
                    case Opcode.AssignBinding:
                    {
                        byte src = Encoding.A(instr);
                        ushort idx = Encoding.Imm16(instr);
                        var name = names[idx];
                        var value = locals[src];
                        if (value == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: AssignBinding src is null"));
                        // M26.1: slot fast path. The iter-variable name maps
                        // to a frame slot; if the cached SymbolEntry pointer
                        // is alive we mutate it directly and skip the
                        // SymbolTable parent-chain walk that TryAssign would
                        // otherwise do per loop iteration.
                        var n2s_fast = f.Function.NameToSlot;
                        if (n2s_fast != null && n2s_fast.TryGetValue(name, out int sFast)
                            && sFast < slots.Length && slots[sFast] != null)
                        {
                            slots[sFast]!.Value = value;
                            break;
                        }
                        if (!ctx.SymbolTable!.TryAssign(name, value))
                            throw new RaUserError(MakeIcError(ctx, $"VM: '{name}' is not defined"));
                        // M14: TryAssign mutates the SymbolEntry in place. If the
                        // slot already points at that entry, .Value updates are
                        // visible without action here. But if the slot is empty
                        // (first AssignBinding before any Load), pre-populate
                        // it so subsequent OP_LOAD_LOCAL_S in the body sees the
                        // current iteration's entry.
                        var n2s2 = f.Function.NameToSlot;
                        if (n2s2 != null && n2s2.TryGetValue(name, out int s3) && s3 < slots.Length && slots[s3] == null)
                        {
                            slots[s3] = ctx.SymbolTable.GetEntry(name);
                        }
                        break;
                    }

                    // -------- collection literals (M6) --------
                    case Opcode.NewList:
                    {
                        byte dst = Encoding.A(instr);
                        byte baseSlot = Encoding.B(instr);
                        byte count = Encoding.C(instr);
                        var elements = new System.Collections.Generic.List<RuntimeValue>(count);
                        for (int i = 0; i < count; i++)
                        {
                            var v = locals[baseSlot + i];
                            if (v == null) throw new RaUserError(MakeIcError(ctx, $"VM: NewList element {i} is null"));
                            elements.Add(v);
                        }
                        locals[dst] = new ListValue(elements).SetContext(ctx);
                        break;
                    }
                    case Opcode.NewTuple:
                    {
                        byte dst = Encoding.A(instr);
                        byte baseSlot = Encoding.B(instr);
                        byte count = Encoding.C(instr);
                        var elements = new System.Collections.Generic.List<RuntimeValue>(count);
                        for (int i = 0; i < count; i++)
                        {
                            var v = locals[baseSlot + i];
                            if (v == null) throw new RaUserError(MakeIcError(ctx, $"VM: NewTuple element {i} is null"));
                            elements.Add(v);
                        }
                        locals[dst] = new TupleValue(elements).SetContext(ctx);
                        break;
                    }
                    case Opcode.NewSet:
                    {
                        // Mirror SetNodeVisitor: linear-search dedupe via
                        // RuntimeValue.Equals (HashSet.Add would use
                        // GetHashCode, but RuntimeValue overrides Equals
                        // without a matching GetHashCode so the hash-based
                        // dedupe silently misses duplicates).
                        byte dst = Encoding.A(instr);
                        byte baseSlot = Encoding.B(instr);
                        byte count = Encoding.C(instr);
                        var elements = new System.Collections.Generic.HashSet<RuntimeValue>();
                        for (int i = 0; i < count; i++)
                        {
                            var v = locals[baseSlot + i];
                            if (v == null) throw new RaUserError(MakeIcError(ctx, $"VM: NewSet element {i} is null"));
                            bool exists = false;
                            foreach (var existing in elements)
                            {
                                if (v.Equals(existing)) { exists = true; break; }
                            }
                            if (!exists) elements.Add(v);
                        }
                        locals[dst] = new SetValue(elements).SetContext(ctx);
                        break;
                    }
                    case Opcode.NewMap:
                    {
                        byte dst = Encoding.A(instr);
                        byte baseSlot = Encoding.B(instr);
                        byte pairCount = Encoding.C(instr);
                        var map = new MapValue().SetContext(ctx);
                        for (int i = 0; i < pairCount; i++)
                        {
                            var k = locals[baseSlot + 2 * i];
                            var v = locals[baseSlot + 2 * i + 1];
                            if (k == null || v == null)
                                throw new RaUserError(MakeIcError(ctx, $"VM: NewMap pair {i} has null component"));
                            var (_, setErr) = map.ListSet(k, v);
                            if (setErr != null) throw new RaUserError(setErr);
                        }
                        locals[dst] = map;
                        break;
                    }
                    case Opcode.Range:
                    {
                        // Slots [base..base+2] are start, end, step. C flag:
                        // 0 = exclusive (`..`), 1 = inclusive (`..=`).
                        byte dst = Encoding.A(instr);
                        byte baseSlot = Encoding.B(instr);
                        bool inclusive = Encoding.C(instr) != 0;
                        var startV = locals[baseSlot];
                        var endV = locals[baseSlot + 1];
                        var stepV = locals[baseSlot + 2];
                        if (startV == null || endV == null || stepV == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: Range operand is null"));
                        if (startV.Type != RuntimeValueType.Number)
                            throw new RaUserError(MakeIcError(ctx, "Start value should be a number"));
                        if (endV.Type != RuntimeValueType.Number)
                            throw new RaUserError(MakeIcError(ctx, "End value should be a number"));
                        if (stepV.Type != RuntimeValueType.Number)
                            throw new RaUserError(MakeIcError(ctx, "Step value should be a number"));
                        var sv = (NumberValue)startV;
                        var ev = (NumberValue)endV;
                        var stv = (NumberValue)stepV;
                        if (sv.Value > ev.Value)
                            throw new RaUserError(MakeIcError(ctx, "Start value should not be higher than the end value"));
                        var values = new System.Collections.Generic.List<RuntimeValue>();
                        if (inclusive)
                            for (BigNumber i = sv.Value; i <= ev.Value; i += stv.Value)
                                values.Add(new NumberValue(i).SetContext(ctx));
                        else
                            for (BigNumber i = sv.Value; i < ev.Value; i += stv.Value)
                                values.Add(new NumberValue(i).SetContext(ctx));
                        locals[dst] = new ListValue(values).SetContext(ctx);
                        break;
                    }
                    case Opcode.ListGet:
                    {
                        byte dst = Encoding.A(instr);
                        byte tgt = Encoding.B(instr);
                        byte idx = Encoding.C(instr);
                        var target = locals[tgt];
                        var index = locals[idx];
                        if (target == null || index == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: ListGet operand is null"));
                        var (val, err) = target.ListAccess(index);
                        if (err != null) throw new RaUserError(err);
                        locals[dst] = val;
                        break;
                    }
                    case Opcode.ListPush:
                    {
                        // L10 list-literal incremental build (the plain-element
                        // counterpart to ListExtend). `a` holds the ListValue
                        // under construction (just built by NewList); `b` holds
                        // the single value to append. Mirrors ListNodeVisitor's
                        // non-spread branch (`elements.Add(val)`) — no copy, so
                        // the element identity matches the all-native NewList
                        // band path.
                        byte pListSlot = Encoding.A(instr);
                        byte pValSlot = Encoding.B(instr);
                        var pList = locals[pListSlot];
                        var pVal = locals[pValSlot];
                        if (pList == null || pVal == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: ListPush operand is null"));
                        ((ListValue)pList).Elements.Add(pVal);
                        break;
                    }
                    case Opcode.ListExtend:
                    {
                        // L10 list-literal spread (`[a, ...x, b]`). `a` holds the
                        // ListValue under construction (just built by NewList);
                        // `b` holds the iterable being splatted. Mirrors
                        // ListNodeVisitor's spread branch EXACTLY: the source
                        // must be a ListValue (ranges materialize to lists, so
                        // they qualify) — anything else raises the visitor's
                        // identical "Spread target must be an iterable" error.
                        // No per-element copy (the visitor does a bare AddRange).
                        byte listSlot = Encoding.A(instr);
                        byte srcSlot = Encoding.B(instr);
                        var listV = locals[listSlot];
                        var srcV = locals[srcSlot];
                        if (listV == null || srcV == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: ListExtend operand is null"));
                        // The list slot was produced by NewList immediately
                        // above — defensive cast, never user-reachable as a
                        // non-list.
                        var dstList = (ListValue)listV;
                        if (srcV.Type != RuntimeValueType.List)
                            throw new RaUserError(MakeIcError(ctx, "Spread target must be an iterable (e.g. list)"));
                        dstList.Elements.AddRange(((ListValue)srcV).Elements);
                        break;
                    }
                    case Opcode.NullCoal:
                    {
                        byte dst = Encoding.A(instr);
                        byte aSlot = Encoding.B(instr);
                        byte bSlot = Encoding.C(instr);
                        var a = locals[aSlot];
                        if (a != null && a.Type != RuntimeValueType.Null)
                            locals[dst] = a;
                        else
                            locals[dst] = locals[bSlot];
                        break;
                    }
                    case Opcode.Interp:
                    {
                        // String interpolation: build a final StringValue from
                        // the slots `b..b+c-1`. Each slot is already a runtime
                        // value (literal parts come pre-loaded as StringValue
                        // constants; expression parts are computed). Conversion
                        // mirrors StringNodeVisitor's per-part path.
                        //
                        // M79: thread-static StringBuilder pool. The dispatch
                        // loop's `OP_INTERP` runs every f"..." literal at
                        // runtime — string-heavy code paths used to allocate a
                        // fresh StringBuilder per dispatch. The thread-static
                        // instance is rented (`Clear()` keeps the underlying
                        // char[] buffer), used, then returned by leaving it
                        // in the slot for the next dispatch. Safe because the
                        // VM dispatch is single-threaded per call frame and
                        // never recurses through Interp before consuming the
                        // result.
                        byte dst = Encoding.A(instr);
                        byte baseSlot = Encoding.B(instr);
                        byte count = Encoding.C(instr);
                        var sb = RentInterpStringBuilder();
                        for (int i = 0; i < count; i++)
                        {
                            var v = locals[baseSlot + i];
                            if (v == null || v.Type == RuntimeValueType.Null)
                                sb.Append("null");
                            else if (v is StringValue sv)
                                sb.Append(sv.Value);
                            else
                                sb.Append(Utilities.StringConversionUtility.ConvertToString(v));
                        }
                        locals[dst] = new StringValue(sb.ToString()).SetContext(ctx);
                        break;
                    }
                    case Opcode.Fmt:
                        // L4 `${expr:spec}`. Body lives off-stack (NoInlining)
                        // so the locals never enlarge the dispatch-loop frame —
                        // depth-2000 recursion budget (same as FusedCmpBranchDelta).
                        locals[Encoding.A(instr)] = ExecuteFmt(f, locals, instr, ctx, pc);
                        break;
                    case Opcode.With:
                        // L4 `recv with { ... }`. Off-stack body (NoInlining),
                        // same frame-budget discipline as OP_FMT above.
                        locals[Encoding.A(instr)] = ExecuteWith(f, locals, instr, ctx);
                        break;
                    case Opcode.DefineType:
                        // L5 one-shot definition from a flat descriptor. Off-stack
                        // body (NoInlining) — definitions run once, never in the
                        // recursion hot path, but keep the frame discipline.
                        locals[Encoding.A(instr)] = ExecuteDefineType(f, instr, ctx, pc, _interpreter);
                        break;
                    case Opcode.Throw:
                    {
                        byte src = Encoding.A(instr);
                        var v = locals[src];
                        string message = v == null ? "<null>" : v.ToString() ?? "<null>";
                        var thrown = new Errors.Types.RuntimeError(
                            DummyPos(ctx), DummyPos(ctx), message, ctx);
                        // Preserve the raw value for pattern-based catch
                        // clauses (`catch (Pattern) { ... }`). System-raised
                        // VM errors leave ThrownValue null and the catch
                        // falls back to a StringValue rendering.
                        thrown.ThrownValue = v;
                        throw new RaUserError(thrown);
                    }

                    // -------- introspection / refs (M9) --------
                    case Opcode.Typeof:
                    {
                        byte dst = Encoding.A(instr);
                        byte src = Encoding.B(instr);
                        // M82 Wide-aware refIdx — high byte from prior
                        // Wide prefix when present, else 0.
                        int refIdx = wideHiC >= 0
                            ? ((wideHiC << 8) | Encoding.C(instr))
                            : Encoding.C(instr);
                        var refs = f.Function.TypeofRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: Typeof refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        var v = locals[src];
                        if (v == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: Typeof source is null"));
                        var sub = Runtime.TypeofHelper.Apply(node, ctx, v);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        locals[dst] = sub.Value;
                        break;
                    }
                    case Opcode.Nameof:
                    {
                        byte dst = Encoding.A(instr);
                        ushort refIdx = Encoding.Imm16(instr);
                        var refs = f.Function.NameofRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: Nameof refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        var sub = Runtime.NameofHelper.Apply(node, ctx);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        locals[dst] = sub.Value;
                        break;
                    }
                    case Opcode.Deref:
                    {
                        byte dst = Encoding.A(instr);
                        byte src = Encoding.B(instr);
                        // M82 Wide-aware refIdx — high byte from prior
                        // Wide prefix when present, else 0.
                        int refIdx = wideHiC >= 0
                            ? ((wideHiC << 8) | Encoding.C(instr))
                            : Encoding.C(instr);
                        var refs = f.Function.DerefRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: Deref refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        var v = locals[src];
                        if (v == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: Deref source is null"));
                        var sub = Runtime.DereferenceHelper.Apply(node, ctx, v);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        locals[dst] = sub.Value;
                        break;
                    }
                    case Opcode.GetSuper:
                    {
                        byte dst = Encoding.A(instr);
                        ushort refIdx = Encoding.Imm16(instr);
                        var refs = f.Function.SuperRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: GetSuper refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        var sub = Runtime.SuperHelper.Apply(node, ctx);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        locals[dst] = sub.Value;
                        break;
                    }
                    case Opcode.DefineFunction:
                    {
                        byte dst = Encoding.A(instr);
                        ushort refIdx = Encoding.Imm16(instr);
                        var refs = f.Function.FuncDefRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: DefineFunction refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        var sub = Runtime.FunctionDefinitionHelper.Apply(node, ctx, _interpreter);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        locals[dst] = sub.Value;
                        break;
                    }

                    case Opcode.NativeDefine:
                    {
                        byte dst = Encoding.A(instr);
                        ushort refIdx = Encoding.Imm16(instr);
                        var refs = f.Function.DefineRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: NativeDefine refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        RuntimeResult sub;
                        switch (node.NodeType)
                        {
                            case AstNodeType.ExtensionDefinition:
                                sub = Visitors.Extensions.ExtensionDefinitionNodeVisitor.Apply(
                                    (Parser.Nodes.Classes.ExtensionDefinitionNode)node, ctx);
                                break;
                            case AstNodeType.TraitDefinition:
                                sub = Visitors.Traits.TraitDefinitionNodeVisitor.Apply(
                                    (Parser.Nodes.Traits.TraitDefinitionNode)node, ctx, _interpreter);
                                break;
                            case AstNodeType.StructDefinition:
                                sub = Visitors.Structs.StructDefinitionNodeVisitor.Apply(
                                    (Parser.Nodes.Structs.StructDefinitionNode)node, ctx, _interpreter);
                                break;
                            case AstNodeType.RecordDefinition:
                                sub = Visitors.Records.RecordDefinitionNodeVisitor.Apply(
                                    (Parser.Nodes.Records.RecordDefinitionNode)node, ctx, _interpreter);
                                break;
                            case AstNodeType.WithExpression:
                                sub = await Visitors.Operations.WithExpressionNodeVisitor.Apply(
                                    (Parser.Nodes.Operations.WithExpressionNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.InterfaceDefinition:
                                sub = Visitors.Interfaces.InterfaceDefinitionNodeVisitor.Apply(
                                    (Parser.Nodes.Interfaces.InterfaceDefinitionNode)node, ctx, _interpreter);
                                break;
                            case AstNodeType.EnumDefinition:
                                sub = await Visitors.Enums.EnumDefinitionNodeVisitor.Apply(
                                    (Parser.Nodes.Enums.EnumDefinitionNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.UsingNamespace:
                                sub = Visitors.Namespaces.UsingNamespaceNodeVisitor.Apply(
                                    (Parser.Nodes.Namespaces.UsingNamespaceNode)node, ctx);
                                break;
                            case AstNodeType.ClassDefinition:
                                sub = await Visitors.Classes.ClassDefinitionNodeVisitor.Apply(
                                    (Parser.Nodes.Classes.ClassDefinitionNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.AnnotationDefinition:
                                sub = Visitors.Annotations.AnnotationDefinitionNodeVisitor.Apply(
                                    (Parser.Nodes.Annotations.AnnotationDefinitionNode)node, ctx, _interpreter);
                                break;
                            case AstNodeType.NamespaceDeclaration:
                                sub = await Visitors.Namespaces.NamespaceDeclarationNodeVisitor.Apply(
                                    (Parser.Nodes.Namespaces.NamespaceDeclarationNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.ImportAll:
                            case AstNodeType.ImportSelective:
                            case AstNodeType.ImportAlias:
                                sub = Visitors.Imports.ImportNodeVisitor.Apply(node, ctx, _interpreter);
                                break;
                            case AstNodeType.Match:
                                sub = await Visitors.Patterns.MatchNodeVisitor.Apply(
                                    (Parser.Nodes.Patterns.MatchNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.DestructuringDeclaration:
                                sub = await Visitors.Patterns.DestructuringDeclarationNodeVisitor.Apply(
                                    (Parser.Nodes.Patterns.DestructuringDeclarationNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.TryUnwrap:
                                sub = await Visitors.Patterns.TryUnwrapNodeVisitor.Apply(
                                    (Parser.Nodes.Patterns.TryUnwrapNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Await:
                                sub = await Visitors.Async.AwaitNodeVisitor.Apply(
                                    (Parser.Nodes.Async.AwaitNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Spawn:
                                sub = await Visitors.Async.SpawnNodeVisitor.Apply(
                                    (Parser.Nodes.Async.SpawnNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Emit:
                                sub = await Visitors.Async.EmitNodeVisitor.Apply(
                                    (Parser.Nodes.Async.EmitNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.ForAwait:
                                sub = await Visitors.Async.ForAwaitNodeVisitor.Apply(
                                    (Parser.Nodes.Async.ForAwaitNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Pipeline:
                                sub = await Visitors.Operations.PipelineNodeVisitor.Apply(
                                    (Parser.Nodes.Operations.PipelineNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Borrow:
                                sub = await Visitors.Operations.BorrowNodeVisitor.Apply(
                                    (Parser.Nodes.Operations.BorrowNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.DereferenceAssignment:
                                sub = await Visitors.Operations.DereferenceAssignmentNodeVisitor.Apply(
                                    (Parser.Nodes.Operations.DereferenceAssignmentNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Goto:
                                sub = await Visitors.Special.GotoNodeVisitor.Apply(
                                    (Parser.Nodes.Special.GotoNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Label:
                                sub = await Visitors.Special.LabelNodeVisitor.Apply(
                                    (Parser.Nodes.Special.LabelNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.SuperFor:
                                sub = await Visitors.Statements.SuperForNodeVisitor.Apply(
                                    (Parser.Nodes.Statements.SuperForNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.AsmBlock:
                                sub = await Visitors.Asm.AsmBlockNodeVisitor.Apply(
                                    (Parser.Nodes.Asm.AsmBlockNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.RegexLiteral:
                                sub = await Visitors.Primitives.RegexLiteralNodeVisitor.Apply(
                                    (Parser.Nodes.Primitives.RegexLiteralNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.FormattedInterpolation:
                                sub = await Visitors.Primitives.FormattedInterpolationNodeVisitor.Apply(
                                    (Parser.Nodes.Primitives.FormattedInterpolationNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Yield:
                                sub = await Visitors.Iterations.YieldNodeVisitor.Apply(
                                    (Parser.Nodes.Iterations.YieldNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.AnnotationApplication:
                                sub = await Visitors.Annotations.AnnotationApplicationNodeVisitor.Apply(
                                    (Parser.Nodes.Annotations.AnnotationApplicationNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Switch:
                                sub = await Visitors.Statements.SwitchNodeVisitor.Apply(
                                    (Parser.Nodes.Statements.SwitchNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Try:
                                sub = await Visitors.Special.TryNodeVisitor.Apply(
                                    (Parser.Nodes.Special.TryNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Scope:
                                sub = await Visitors.Special.ScopeNodeVisitor.Apply(
                                    (Parser.Nodes.Special.ScopeNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.If:
                                sub = await Visitors.Statements.IfNodeVisitor.Apply(
                                    (Parser.Nodes.Statements.IfNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.VariableDeclaration:
                                sub = await Visitors.Variables.VariableDeclarationNodeVisitor.Apply(
                                    (Parser.Nodes.Variables.VariableDeclarationNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.VariableAssignment:
                                sub = await Visitors.Variables.VariableAssignmentNodeVisitor.Apply(
                                    (Parser.Nodes.Variables.VariableAssignmentNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.BinaryOperation:
                                sub = await Visitors.Operations.BinaryOperationNodeVisitor.Apply(
                                    (Parser.Nodes.Operations.BinaryOperationNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.UnaryOperation:
                                sub = await Visitors.Operations.UnaryOperationNodeVisitor.Apply(
                                    (Parser.Nodes.Operations.UnaryOperationNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.List:
                                sub = await Visitors.Primitives.ListNodeVisitor.Apply(
                                    (Parser.Nodes.Primitives.ListNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Set:
                                sub = await Visitors.Primitives.SetNodeVisitor.Apply(
                                    (Parser.Nodes.Primitives.SetNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Tuple:
                                sub = await Visitors.Primitives.TupleNodeVisitor.Apply(
                                    (Parser.Nodes.Primitives.TupleNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Map:
                                sub = await Visitors.Primitives.MapNodeVisitor.Apply(
                                    (Parser.Nodes.Primitives.MapNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.FunctionCall:
                                sub = await Visitors.Functions.FunctionCallNodeVisitor.Apply(
                                    (Parser.Nodes.Functions.FunctionCallNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Break:
                                sub = await Visitors.Iterations.BreakNodeVisitor.Apply(
                                    (Parser.Nodes.Iterations.BreakNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Continue:
                                sub = await Visitors.Iterations.ContinueNodeVisitor.Apply(
                                    (Parser.Nodes.Iterations.ContinueNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Pass:
                                sub = await Visitors.Operations.PassNodeVisitor.Apply(
                                    (Parser.Nodes.Operations.PassNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Return:
                                sub = await Visitors.Functions.ReturnNodeVisitor.Apply(
                                    (Parser.Nodes.Functions.ReturnNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Throw:
                                sub = await Visitors.Statements.ThrowNodeVisitor.Apply(
                                    (Parser.Nodes.Statements.ThrowNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.Retry:
                                sub = await Visitors.Primitives.RetryNodeVisitor.Apply(
                                    (Parser.Nodes.Statements.RetryNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.VariableDelete:
                                sub = await Visitors.Variables.VariableDeleteNodeVisitor.Apply(
                                    (Parser.Nodes.Variables.VariableDeleteNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.MemberAssignment:
                                sub = await Visitors.Members.MemberAssignmentNodeVisitor.Apply(
                                    (Parser.Nodes.Structs.MemberAssignmentNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.ListAssignment:
                                sub = await Visitors.Variables.ListAssignmentNodeVisitor.Apply(
                                    (Parser.Nodes.Variables.ListAssignmentNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            case AstNodeType.DelegateDefinition:
                                sub = Visitors.Functions.DelegateDefinitionNodeVisitor.Apply(
                                    (Parser.Nodes.Functions.DelegateDefinitionNode)node, ctx);
                                break;
                            case AstNodeType.IsType:
                                sub = await Visitors.Operations.IsTypeNodeVisitor.Apply(
                                    (Parser.Nodes.Operations.IsTypeNode)node, ctx, _interpreter).ConfigureAwait(false);
                                break;
                            default:
                                throw new RaUserError(MakeIcError(ctx,
                                    $"VM: NativeDefine unsupported NodeType {node.NodeType}"));
                        }
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        if (sub.ShouldReturn()) return sub;
                        locals[dst] = sub.Value;
                        break;
                    }

                    // -------- OOP member access (M7) --------
                    case Opcode.GetMember:
                    {
                        // `obj.member`. Encoding [op][dst][srcSlot][refIdx:u8]
                        // where refIdx indexes RaFunction.MemberAccessRefs.
                        byte dst = Encoding.A(instr);
                        byte src = Encoding.B(instr);
                        // M82 Wide-aware refIdx — high byte from prior
                        // Wide prefix when present, else 0.
                        int refIdx = wideHiC >= 0
                            ? ((wideHiC << 8) | Encoding.C(instr))
                            : Encoding.C(instr);
                        var refs = f.Function.MemberAccessRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: GetMember refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        var target = locals[src];
                        if (target == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: GetMember target is null"));
                        // M28.1: per-PC IC. ApplyWithIc reads + writes the
                        // slot for the current PC; hit path skips the
                        // chain-of-type-tag dispatch, miss path runs the
                        // full chain and primes the slot.
                        var memIc = f.Function.MemberAccessIc;
                        int memPc = pc - 1;
                        RuntimeResult sub;
                        if (memIc != null && (uint)memPc < (uint)memIc.Length)
                        {
                            sub = Runtime.MemberAccessHelper.ApplyWithIc(node, ctx, target, ref memIc[memPc]);
                        }
                        else
                        {
                            sub = Runtime.MemberAccessHelper.Apply(node, ctx, target);
                        }
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        locals[dst] = sub.Value;
                        break;
                    }

                    case Opcode.SetMember:
                    {
                        // `obj.member = v`. Encoding [op][ownerSlot][valSlot][refIdx:u8].
                        byte ownerSlot = Encoding.A(instr);
                        byte valSlot = Encoding.B(instr);
                        // M82 Wide-aware refIdx — high byte from prior
                        // Wide prefix when present, else 0.
                        int refIdx = wideHiC >= 0
                            ? ((wideHiC << 8) | Encoding.C(instr))
                            : Encoding.C(instr);
                        var refs = f.Function.MemberAssignRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: SetMember refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        var owner = locals[ownerSlot];
                        var value = locals[valSlot];
                        if (owner == null || value == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: SetMember slot is null"));
                        var sub = Runtime.MemberAssignmentHelper.Apply(node, ctx, owner, value);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        break;
                    }

                    case Opcode.SetIndex:
                    {
                        // `arr[i] = v`. Encoding [op][targetSlot][idxSlot][refIdxAndValSlot].
                        // We pack refIdx (top 4 bits) and valSlot (low 4 bits) into c?
                        // Simpler: use a side ListAssignRefs pool and store
                        // valSlot in the byte. But 4 ops needed: target, idx,
                        // val, refIdx. One u32 only fits 3 byte operands.
                        // Solution: refIdx is c. valSlot is recovered from
                        // contiguous layout: caller emits target/idx/val into
                        // consecutive slots; we use targetSlot+2 as the val
                        // slot. This is consistent and free.
                        byte tgtSlot = Encoding.A(instr);
                        byte idxSlot = Encoding.B(instr);
                        // M82 Wide-aware refIdx — high byte from prior
                        // Wide prefix when present, else 0.
                        int refIdx = wideHiC >= 0
                            ? ((wideHiC << 8) | Encoding.C(instr))
                            : Encoding.C(instr);
                        var refs = f.Function.ListAssignRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: SetIndex refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        var target = locals[tgtSlot];
                        var idx = locals[idxSlot];
                        var val = locals[(byte)(idxSlot + 1)];
                        if (target == null || idx == null || val == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: SetIndex slot is null"));
                        var sub = Runtime.ListAssignmentHelper.Apply(node, ctx, target, idx, val);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        break;
                    }

                    case Opcode.ForEachIterable:
                    {
                        // Canonicalise foreach iteration: input is any
                        // List/Tuple/Set/Map; output is a ListValue whose
                        // elements are the iteration items (Map yields a
                        // TupleValue(key, value) per pair, matching
                        // ForEachNodeVisitor's Map branch).
                        byte dst = Encoding.A(instr);
                        byte src = Encoding.B(instr);
                        var coll = locals[src];
                        if (coll == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: ForEachIterable source is null"));
                        switch (coll.Type)
                        {
                            case RuntimeValueType.List:
                                locals[dst] = coll;
                                break;
                            case RuntimeValueType.Tuple:
                            {
                                var tv = (TupleValue)coll;
                                locals[dst] = new ListValue(new System.Collections.Generic.List<RuntimeValue>(tv.Elements)).SetContext(ctx);
                                break;
                            }
                            case RuntimeValueType.Set:
                            {
                                var sv = (SetValue)coll;
                                locals[dst] = new ListValue(new System.Collections.Generic.List<RuntimeValue>(sv.Elements)).SetContext(ctx);
                                break;
                            }
                            case RuntimeValueType.Map:
                            {
                                var mv = (MapValue)coll;
                                var pairs = new System.Collections.Generic.List<RuntimeValue>(mv.Pairs.Count);
                                foreach (var (k, v) in mv.Pairs)
                                    pairs.Add(new TupleValue(new System.Collections.Generic.List<RuntimeValue> { k, v }));
                                locals[dst] = new ListValue(pairs).SetContext(ctx);
                                break;
                            }
                            // Note: Stream is handled by the dedicated lazy
                            // path emitted by CompileForEach (JmpIfStream +
                            // ForEachStreamPull). If a Stream reaches this
                            // opcode it means the dual-path emission was
                            // bypassed somehow — fall through to the error.
                            default:
                                throw new RaUserError(MakeIcError(ctx, "Must iter onto a collection"));
                        }
                        break;
                    }

                    case Opcode.ListLen:
                    {
                        byte dst = Encoding.A(instr);
                        byte src = Encoding.B(instr);
                        var coll = locals[src];
                        if (coll == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: ListLen source is null"));
                        int n;
                        switch (coll.Type)
                        {
                            case RuntimeValueType.List: n = ((ListValue)coll).Elements.Count; break;
                            case RuntimeValueType.Tuple: n = ((TupleValue)coll).Elements.Count; break;
                            case RuntimeValueType.Set: n = ((SetValue)coll).Elements.Count; break;
                            case RuntimeValueType.Map: n = ((MapValue)coll).Pairs.Count; break;
                            default:
                                throw new RaUserError(MakeIcError(ctx, "ListLen requires a collection"));
                        }
                        locals[dst] = new NumberValue(n).SetContext(ctx);
                        break;
                    }

                    case Opcode.EnumAccess:
                    {
                        // `EnumType.Variant`. [op][dst][srcSlot][refIdx:u8].
                        byte dst = Encoding.A(instr);
                        byte src = Encoding.B(instr);
                        // M82 Wide-aware refIdx — high byte from prior
                        // Wide prefix when present, else 0.
                        int refIdx = wideHiC >= 0
                            ? ((wideHiC << 8) | Encoding.C(instr))
                            : Encoding.C(instr);
                        var refs = f.Function.EnumAccessRefs;
                        if (refIdx >= refs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: EnumAccess refIdx {refIdx} out of range"));
                        var node = refs[refIdx];
                        var enumValue = locals[src];
                        if (enumValue == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: EnumAccess source is null"));
                        // M27.3: per-PC IC. EnumAccessHelper.Apply performs a
                        // type-tag check + two dict lookups (HasMember +
                        // GetMember). Variant tables are immutable after
                        // construction so identity equality on the EnumType
                        // reference is a safe cache key — same type at this PC
                        // always resolves to the same variant value.
                        var icArr = f.Function.EnumAccessIc;
                        int icPc = pc - 1;
                        if (icArr != null && (uint)icPc < (uint)icArr.Length)
                        {
                            ref var slot = ref icArr[icPc];
                            // Primary hit (inline).
                            if (slot.Result != null && ReferenceEquals(slot.EnumType, enumValue))
                            {
                                locals[dst] = slot.Result;
                                break;
                            }
                            // Cold path — PIC scan + prime in helper.
                            var enumResult = EnumAccessIcMissCold(
                                ref slot, node, ctx, enumValue);
                            if (enumResult.Error != null) throw new RaUserError(enumResult.Error);
                            locals[dst] = enumResult.Value;
                            break;
                        }
                        var sub = Runtime.EnumAccessHelper.Apply(node, ctx, enumValue);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        locals[dst] = sub.Value;
                        break;
                    }

                    // -------- type cast (M5) --------
                    case Opcode.Cast:
                    {
                        byte dst = Encoding.A(instr);
                        byte src = Encoding.B(instr);
                        // M82 Wide-aware refIdx — high byte from prior
                        // Wide prefix when present, else 0.
                        int refIdx = wideHiC >= 0
                            ? ((wideHiC << 8) | Encoding.C(instr))
                            : Encoding.C(instr);
                        var castRefs = f.Function.CastRefs;
                        if (refIdx >= castRefs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: Cast refIdx {refIdx} out of range"));
                        var castNode = castRefs[refIdx];
                        var v = locals[src];
                        if (v == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: Cast source slot is null"));
                        // M27.3: per-PC IC. The dominant cast pattern in tight
                        // code paths is `x as T` where x already has type T —
                        // RuntimeValue.CastTo falls through several string
                        // compares before returning Copy(). Cache the "noop"
                        // verdict per (PC, source RuntimeValueType): on a hit
                        // we skip the string-compare cascade entirely.
                        // Polymorphic sites don't churn — the slot is primed
                        // once on the first observation and consulted only
                        // for the early fast path.
                        var castIc = f.Function.CastIc;
                        int castIcPc = pc - 1;
                        if (castIc != null && (uint)castIcPc < (uint)castIc.Length)
                        {
                            ref var slot = ref castIc[castIcPc];
                            // Primary hit — noop-cast fast path.
                            if (slot.Primed && slot.IsNoop && slot.SrcType == v.Type)
                            {
                                locals[dst] = v.Copy().SetContext(ctx).SetPos(castNode.PositionStart, castNode.PositionEnd);
                                break;
                            }
                            // M81 PIC scan inline (cheap — 2 entries).
                            if (slot.Pic != null)
                            {
                                bool hit = false;
                                for (int i = 0; i < slot.Pic.Length; i++)
                                {
                                    ref var pe = ref slot.Pic[i];
                                    if (pe.Primed && pe.SrcType == v.Type)
                                    {
                                        if (pe.IsNoop)
                                        {
                                            locals[dst] = v.Copy().SetContext(ctx).SetPos(castNode.PositionStart, castNode.PositionEnd);
                                            hit = true;
                                        }
                                        break;
                                    }
                                }
                                if (hit) break;
                            }
                            // Cold path — virtual CastTo + PIC update.
                            var castResult = CastIcMissCold(ref slot, v, castNode, ctx);
                            if (castResult.Error != null) throw new RaUserError(castResult.Error);
                            locals[dst] = castResult.Value ?? NullValue.Null.SetContext(ctx).SetPos(castNode.PositionStart, castNode.PositionEnd);
                            break;
                        }
                        var (casted, castErr) = v.CastTo(castNode.TargetType);
                        if (castErr != null) throw new RaUserError(castErr);
                        locals[dst] = casted ?? NullValue.Null.SetContext(ctx).SetPos(castNode.PositionStart, castNode.PositionEnd);
                        break;
                    }

                    // -------- function call (M5) --------
                    case Opcode.Call:
                    {
                        // M5 simple call: `fn(arg0, arg1, ...)` with
                        // positional arguments only (no named, no ref, no
                        // spread, no generic type args). Calling convention:
                        //   locals[B]   = callee
                        //   locals[B+1] = arg0
                        //   ...
                        //   locals[B+C] = arg<C-1>
                        // Result lands in locals[A]. Delegates to
                        // FunctionCallExecutor.Invoke so semantics match the
                        // AST visitor verbatim (annotation interceptors,
                        // contracts, type coercion, generic dispatch, ...).
                        if (ctx.AreCallsBlocked)
                            throw new RaUserError(MakeIcError(ctx,
                                "function calls are not allowed in this context"));

                        byte dst = Encoding.A(instr);
                        byte fnSlot = Encoding.B(instr);
                        byte argCount = Encoding.C(instr);
                        var fn = locals[fnSlot];
                        if (fn == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: callee slot is null"));
                        var argList = RentArgList(argCount);
                        for (int i = 0; i < argCount; i++)
                        {
                            var a = locals[fnSlot + 1 + i];
                            if (a == null)
                                throw new RaUserError(MakeIcError(ctx, $"VM: argument {i} slot is null"));
                            argList.Add(a);
                        }
                        var emptyNamed = Runtime.Calls.FunctionCallExecutor.EmptyNamedArgs;
                        // M28.2: per-PC method-dispatch IC. When the callee is
                        // a BoundClassMethodGroupValue (`obj.foo` resolved to
                        // a group of overload candidates), cache the resolved
                        // overload per (Definition, argCount) and substitute
                        // a single-method BoundClassMethodValue before
                        // dispatch. Skips the LINQ FirstOrDefault scan +
                        // CanBindSignature HashSet alloc inside the group.
                        var callIc = f.Function.CallMethodIc;
                        int callIcPc = pc - 1;
                        if (fn is Values.Classes.BoundClassMethodGroupValue bgrp
                            && callIc != null && (uint)callIcPc < (uint)callIc.Length)
                        {
                            ref var slot = ref callIc[callIcPc];
                            Parser.Nodes.Functions.FunctionDefinitionNode? selectedMethod = null;
                            // Primary hit (inline, hot path).
                            if (slot.Primed
                                && slot.ArgCount == argCount
                                && ReferenceEquals(slot.ReceiverShape, bgrp.Definition))
                            {
                                selectedMethod = (Parser.Nodes.Functions.FunctionDefinitionNode?)slot.ChosenMethod;
                            }
                            else
                            {
                                // Cold path — PIC scan + prime in a
                                // NoInlining helper so the dispatch
                                // loop's C# frame stays small (deep
                                // recursion tripwire).
                                selectedMethod = CallMethodIcMissCold(
                                    ref slot, bgrp, argCount, argList, emptyNamed);
                            }
                            if (selectedMethod != null)
                            {
                                // PERF: reuse the bound-method wrapper when this
                                // site re-binds the SAME receiver + method
                                // (hot method loop). The wrapper holds only
                                // (Definition, SelfInstance, MethodNode) — all
                                // identity-stable here — plus Context/Pos which
                                // we re-stamp. Eliminates the per-call
                                // BoundClassMethodValue allocation.
                                if (slot.CachedBound is Values.Primitives.BoundClassMethodValue cb
                                    && ReferenceEquals(cb.SelfInstance, bgrp.SelfInstance)
                                    && ReferenceEquals(cb.MethodNode, selectedMethod))
                                {
                                    fn = cb.SetContext(ctx).SetPos(bgrp.PositionStart, bgrp.PositionEnd);
                                }
                                else
                                {
                                    var bound = new Values.Primitives.BoundClassMethodValue(
                                        bgrp.Definition, bgrp.SelfInstance, selectedMethod, isStatic: false)
                                        .SetContext(ctx)
                                        .SetPos(bgrp.PositionStart, bgrp.PositionEnd);
                                    slot.CachedBound = bound;
                                    fn = bound;
                                }
                            }
                        }
                        var pos = DummyPos(ctx);
                        var invokeTask = Runtime.Calls.FunctionCallExecutor.Invoke(
                            fn, argList, emptyNamed, null, pos, pos, ctx);
                        RuntimeResult invokeRes;
                        if (invokeTask.IsCompletedSuccessfully)
                        {
                            invokeRes = invokeTask.Result;
                            // Sync completion: the call (binding + body) is fully
                            // done, so the transport list is dead — recycle it.
                            // The async branch deliberately does NOT recycle: a
                            // suspended async callee may still hold `argList`.
                            ReturnArgList(argList);
                        }
                        else
                            invokeRes = await invokeTask.ConfigureAwait(false);
                        if (invokeRes.Error != null) throw new RaUserError(invokeRes.Error);
                        locals[dst] = invokeRes.Value;
                        break;
                    }

                    case Opcode.CallGeneric:
                        // L10 explicit-generic-type-arg call `foo<int>(...)`. Off-
                        // stack body (NoInlining async) — keeps the dispatch frame
                        // small for sync-completion deep recursion (M85).
                        locals[Encoding.A(instr)] =
                            await ExecuteCallGeneric(f, locals, instr, ctx).ConfigureAwait(false);
                        break;

                    // M28.3: fused Call + Ret. Same dispatch logic as OP_CALL
                    // (including the BoundClassMethodGroupValue overload IC)
                    // but propagates the invoked function's return value as
                    // *this* frame's return value. Skips the separate OP_RET
                    // round-trip when an `return fn(args)` lands in tail
                    // position.
                    case Opcode.TailCall:
                    {
                        if (ctx.AreCallsBlocked)
                            throw new RaUserError(MakeIcError(ctx,
                                "function calls are not allowed in this context"));

                        byte fnSlot_tc = Encoding.A(instr);
                        byte argsBase_tc = Encoding.B(instr);
                        byte argCount_tc = Encoding.C(instr);
                        var fn_tc = locals[fnSlot_tc];
                        if (fn_tc == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: callee slot is null"));

                        // M69 trampolined TCO. Eligible callees skip
                        // the C# `Invoke` recursion entirely: the
                        // current frame is replaced with the
                        // callee's, all hoisted state vars are
                        // re-bound, and the dispatch loop restarts
                        // at PC 0. Recursive tail calls keep the C#
                        // stack flat — deep recursion via tail
                        // becomes equivalent to iteration. Eligibility:
                        //   * plain `FunctionValue` (no method group
                        //     dispatch / no overload pick).
                        //   * CompiledBody present (no AST fallback).
                        //   * Synchronous body (`IsAsync`/`IsAsyncStream`
                        //     create a Task — semantically distinct).
                        //   * No generic / variadic parameters.
                        //   * Exact arg-count match (no defaults to
                        //     evaluate — the PrepareExecutionContextForCall
                        //     async branch never enters).
                        // Ineligible callees fall through to the
                        // existing `Invoke` path so observable
                        // semantics stay identical.
                        if (fn_tc is Values.Functions.FunctionValue fvTail
                            && fvTail.CompiledBody != null
                            && !fvTail.IsAsync
                            && !fvTail.IsAsyncStream
                            && fvTail.GenericTypeParams.Count == 0
                            && !fvTail.HasVarArgs
                            && fvTail.ArgNames.Count == argCount_tc)
                        {
                            var tcArgs = RentArgList(argCount_tc);
                            bool tcArgsOk = true;
                            for (int i = 0; i < argCount_tc; i++)
                            {
                                var a = locals[argsBase_tc + i];
                                if (a == null) { tcArgsOk = false; break; }
                                tcArgs.Add(a);
                            }
                            if (tcArgsOk)
                            {
                                var emptyNamedTc = Runtime.Calls.FunctionCallExecutor.EmptyNamedArgs;
                                var prepTaskTc = fvTail.PrepareExecutionContextForCall(
                                    tcArgs, emptyNamedTc, fvTail.ArgNames, fvTail.ArgTypes,
                                    fvTail.ParamDefaults, false, null, null);
                                if (prepTaskTc.IsCompletedSuccessfully)
                                {
                                    var (execCtxTc, prepErrTc) = prepTaskTc.Result;
                                    // Sync prep bound the args into execCtxTc —
                                    // tcArgs is dead, recycle it. (The async-prep
                                    // fall-through below leaves it for the GC: an
                                    // un-awaited prep task may still hold it.)
                                    ReturnArgList(tcArgs);
                                    if (prepErrTc != null) throw new RaUserError(prepErrTc);
                                    // Trampoline: swap frame, replace ctx,
                                    // jump to the dispatch-loop preamble
                                    // so all hoisted vars rebind to the
                                    // callee's RaFunction.
                                    //
                                    // M79: return the outgoing frame to
                                    // its function's pool before
                                    // replacing it — the trampoline
                                    // discards the caller's stack frame
                                    // (TCO) so no upstream reference
                                    // outlives the swap.
                                    f.Pc = pc; // unreachable but keeps invariants
                                    var prevFrame = f;
                                    f = VmFrame.Rent(fvTail.CompiledBody);
                                    // Pool the outgoing frame UNLESS it is the
                                    // caller-owned entry frame (see fIsEntryFrame).
                                    if (!fIsEntryFrame) VmFrame.Return(prevFrame);
                                    fIsEntryFrame = false;
                                    ctx = execCtxTc!;
                                    goto TAILCALL_RESTART;
                                }
                                // Async prep — defaults need eval. Fall
                                // through to the heavy Invoke path.
                            }
                        }

                        var argList_tc = RentArgList(argCount_tc);
                        for (int i = 0; i < argCount_tc; i++)
                        {
                            var a = locals[argsBase_tc + i];
                            if (a == null)
                                throw new RaUserError(MakeIcError(ctx, $"VM: argument {i} slot is null"));
                            argList_tc.Add(a);
                        }
                        var emptyNamed_tc = Runtime.Calls.FunctionCallExecutor.EmptyNamedArgs;
                        var callIc_tc = f.Function.CallMethodIc;
                        int callIcPc_tc = pc - 1;
                        if (fn_tc is Values.Classes.BoundClassMethodGroupValue bgrp_tc
                            && callIc_tc != null && (uint)callIcPc_tc < (uint)callIc_tc.Length)
                        {
                            ref var slot_tc = ref callIc_tc[callIcPc_tc];
                            Parser.Nodes.Functions.FunctionDefinitionNode? selectedMethod_tc = null;
                            if (slot_tc.Primed
                                && slot_tc.ArgCount == argCount_tc
                                && ReferenceEquals(slot_tc.ReceiverShape, bgrp_tc.Definition))
                            {
                                selectedMethod_tc = (Parser.Nodes.Functions.FunctionDefinitionNode?)slot_tc.ChosenMethod;
                            }
                            else
                            {
                                selectedMethod_tc = CallMethodIcMissCold(
                                    ref slot_tc, bgrp_tc, argCount_tc, argList_tc, emptyNamed_tc);
                            }
                            if (selectedMethod_tc != null)
                            {
                                fn_tc = new Values.Primitives.BoundClassMethodValue(
                                    bgrp_tc.Definition, bgrp_tc.SelfInstance, selectedMethod_tc, isStatic: false)
                                    .SetContext(ctx)
                                    .SetPos(bgrp_tc.PositionStart, bgrp_tc.PositionEnd);
                            }
                        }
                        var pos_tc = DummyPos(ctx);
                        var invokeTask_tc = Runtime.Calls.FunctionCallExecutor.Invoke(
                            fn_tc, argList_tc, emptyNamed_tc, null, pos_tc, pos_tc, ctx);
                        RuntimeResult invokeRes_tc;
                        if (invokeTask_tc.IsCompletedSuccessfully)
                        {
                            invokeRes_tc = invokeTask_tc.Result;
                            ReturnArgList(argList_tc);
                        }
                        else
                            invokeRes_tc = await invokeTask_tc.ConfigureAwait(false);
                        if (invokeRes_tc.Error != null) throw new RaUserError(invokeRes_tc.Error);
                        f.Pc = pc;
                        return res.SuccessReturn(invokeRes_tc.Value ?? NullValue.Null);
                    }

                    case Opcode.DeclareLocal:
                    {
                        // `var/let/const/final x = expr` (single decl, no
                        // annotations). Delegates to DeclarationHelper so the
                        // semantics match VariableDeclarationNodeVisitor:
                        // redeclaration check, type check, generic-arg late
                        // binding for channel/stream/task, VariableDeclarationType
                        // tagging, SetLocalWithDeclarationType.
                        byte src = Encoding.A(instr);
                        ushort idx = Encoding.Imm16(instr);
                        if (idx >= astRefs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: DeclareLocal refIdx {idx} out of range"));
                        var declNode = astRefs[idx] as Parser.Nodes.Variables.VariableDeclarationNode;
                        if (declNode == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: DeclareLocal target is not a VariableDeclarationNode"));
                        var initValue = locals[src];
                        if (initValue == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: DeclareLocal src slot is null"));
                        var sub = Runtime.DeclarationHelper.ApplySingle(declNode, ctx, 0, initValue);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        // M14: cache the freshly-created SymbolEntry into the
                        // frame slot table so subsequent OP_LOAD_LOCAL_S /
                        // OP_STORE_LOCAL_S can skip the hash walk.
                        if (idx < (uint)declSlotByAstRef.Length)
                        {
                            int slot = declSlotByAstRef[idx];
                            if (slot >= 0 && slot < slots.Length)
                            {
                                var declName = declNode.Declarations[0].Item1.Value?.ToString();
                                if (!string.IsNullOrEmpty(declName))
                                {
                                    slots[slot] = ctx.SymbolTable!.GetLocalEntry(declName)
                                        ?? ctx.SymbolTable!.GetEntry(declName);
                                }
                            }
                        }
                        break;
                    }

                    case Opcode.StoreGlobal:
                    {
                        // `x = <expr>` assignment, EQ only. Replicates the
                        // post-RHS half of VariableAssignmentNodeVisitor via
                        // AssignmentHelper so semantics stay identical
                        // (compound ops, type coerce, IReferenceValue
                        // through-write, BorrowValue rebind, statically-typed
                        // diagnostics).
                        byte src = Encoding.A(instr);
                        ushort idx = Encoding.Imm16(instr);
                        if (idx >= astRefs.Length)
                            throw new RaUserError(MakeIcError(ctx, $"VM: StoreGlobal refIdx {idx} out of range"));
                        var node = astRefs[idx] as Parser.Nodes.Variables.VariableAssignmentNode;
                        if (node == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: StoreGlobal target is not a VariableAssignmentNode"));
                        var newValue = locals[src];
                        if (newValue == null)
                            throw new RaUserError(MakeIcError(ctx, "VM: StoreGlobal src slot is null"));
                        var entry = ctx.SymbolTable!.GetEntry(node.Name);
                        var preErr = Runtime.AssignmentHelper.PreCheck(node, entry, ctx);
                        if (preErr != null) throw new RaUserError(preErr);
                        var borrowErr = Runtime.AssignmentHelper.CheckBorrowGuard(node, entry!, newValue, ctx);
                        if (borrowErr != null) throw new RaUserError(borrowErr);
                        var applied = Runtime.AssignmentHelper.ApplyPrechecked(node, ctx, entry!, entry!.Value, newValue);
                        if (applied.Error != null) throw new RaUserError(applied.Error);
                        break;
                    }

                    // -------- arithmetic --------
                    // M45 type-specialised arith. Both operands proven
                    // Number by the static SlotTypeHints lattice — skip the
                    // type-tag + null-check cascade Binary normally pays.
                    case Opcode.AddNN:
                    case Opcode.SubNN:
                    case Opcode.MulNN:
                    {
                        byte aN = Encoding.A(instr);
                        byte bN = Encoding.B(instr);
                        byte cN = Encoding.C(instr);
                        var lb = locals[bN]!;
                        var rc = locals[cN]!;
                        var opN = (Opcode)(instr & 0xFF);
                        // The static SlotTypeHints lattice proves both operands
                        // are RuntimeValueType.Number, but that admits sibling
                        // numeric value classes (IntegerValue / LongValue /
                        // ByteValue / … — produced e.g. by an FFI int32 return
                        // or a typed-int binding) which are NOT NumberValue. A
                        // hard `(NumberValue)` cast threw InvalidCastException on
                        // those; guard the int64 fast path with `is` instead and
                        // let the virtual arithmetic handle every other case.
                        if (lb is NumberValue ln && rc is NumberValue rn
                            && TryGetInt64(ln, out long lvNN) && TryGetInt64(rn, out long rvNN))
                        {
                            RuntimeValue? prodNN = null;
                            if (opN == Opcode.AddNN)
                            {
                                long s = lvNN + rvNN;
                                if (((lvNN ^ s) & (rvNN ^ s)) >= 0)
                                    prodNN = NumberValue.OfInt64(s);
                            }
                            else if (opN == Opcode.SubNN)
                            {
                                long d = lvNN - rvNN;
                                if (((lvNN ^ rvNN) & (lvNN ^ d)) >= 0)
                                    prodNN = NumberValue.OfInt64(d);
                            }
                            else // MulNN
                            {
                                // 128-bit-product overflow check (see MulII):
                                // fits int64 iff hi == sign-extension of lo.
                                long hiNN = System.Math.BigMul(lvNN, rvNN, out long loNN);
                                if (hiNN == (loNN >> 63))
                                    prodNN = NumberValue.OfInt64(loNN);
                            }
                            if (prodNN != null)
                            {
                                locals[aN] = prodNN;
                                break;
                            }
                        }
                        // Fallback for non-NumberValue numeric operands, int64
                        // overflow, or fractional scale: the virtual arithmetic
                        // ops are defined on every RuntimeValue and build the
                        // proper (possibly BigNumber) result.
                        ValueResult rNN = opN switch
                        {
                            Opcode.AddNN => lb.AddedTo(rc),
                            Opcode.SubNN => lb.SubbedBy(rc),
                            Opcode.MulNN => lb.MultedBy(rc),
                            _ => new ValueResult(null, null),
                        };
                        if (rNN.Error != null) throw new RaUserError(rNN.Error);
                        locals[aN] = rNN.Value;
                        break;
                    }
                    case Opcode.Add: { var r = Binary(locals, instr, BinOp.Add); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Sub: { var r = Binary(locals, instr, BinOp.Sub); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Mul: { var r = Binary(locals, instr, BinOp.Mul); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Div: { var r = Binary(locals, instr, BinOp.Div); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Mod: { var r = Binary(locals, instr, BinOp.Mod); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Pow: { var r = Binary(locals, instr, BinOp.Pow); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Shl: { var r = Binary(locals, instr, BinOp.Shl); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Shr: { var r = Binary(locals, instr, BinOp.Shr); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Ushr: { var r = Binary(locals, instr, BinOp.Ushr); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Rol:  { var r = Binary(locals, instr, BinOp.Rol);  if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Ror:  { var r = Binary(locals, instr, BinOp.Ror);  if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.BAnd: { var r = Binary(locals, instr, BinOp.BAnd); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.BOr: { var r = Binary(locals, instr, BinOp.BOr); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.BXor: { var r = Binary(locals, instr, BinOp.BXor); if (r.Error != null) throw new RaUserError(r.Error); break; }

                    // -------- comparisons --------
                    case Opcode.Eq:  { var r = Binary(locals, instr, BinOp.Eq);  if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Ne:  { var r = Binary(locals, instr, BinOp.Ne);  if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.SEq: { var r = Binary(locals, instr, BinOp.SEq); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.SNe: { var r = Binary(locals, instr, BinOp.SNe); if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Lt:  { var r = Binary(locals, instr, BinOp.Lt);  if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Le:  { var r = Binary(locals, instr, BinOp.Le);  if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Gt:  { var r = Binary(locals, instr, BinOp.Gt);  if (r.Error != null) throw new RaUserError(r.Error); break; }
                    case Opcode.Ge:  { var r = Binary(locals, instr, BinOp.Ge);  if (r.Error != null) throw new RaUserError(r.Error); break; }

                    // -------- membership --------
                    // `left in right` → left.InCollection(right). `not in` is
                    // this op followed by OP_NOT (emitted by the IR compiler).
                    // Erroring RHS (non-collection) surfaces via RaUserError,
                    // same as Div / the comparison ops above.
                    case Opcode.In:  { var r = Binary(locals, instr, BinOp.In);  if (r.Error != null) throw new RaUserError(r.Error); break; }

                    // -------- branches --------
                    case Opcode.Jmp:
                    {
                        short jOffs = Encoding.SImm16(instr);
                        // M39: a negative jump delta is a loop back edge
                        // (control flow returning to a prior PC). Bump
                        // the loop-iteration counter so the tier-up
                        // compiler can spot hot loops independently of
                        // function invocation counts.
                        if (jOffs < 0) f.Function.LoopBackEdgeCount++;
                        pc += jOffs;
                        break;
                    }
                    case Opcode.JmpIf:
                    {
                        // M74: Bool-tag fast path via `ReadSlotTruth`
                        // helper (kept inline-light to preserve the
                        // dispatch-loop frame budget — `test_deep_
                        // recursion` at depth 2000 is the tripwire).
                        byte a = Encoding.A(instr);
                        if (ReadSlotTruth(f, locals, a))
                        {
                            short offs = Encoding.SImm16(instr);
                            if (offs < 0) f.Function.LoopBackEdgeCount++;
                            pc += offs;
                        }
                        break;
                    }
                    case Opcode.JmpIfNot:
                    {
                        byte a = Encoding.A(instr);
                        if (!ReadSlotTruth(f, locals, a))
                        {
                            short offs = Encoding.SImm16(instr);
                            if (offs < 0) f.Function.LoopBackEdgeCount++;
                            pc += offs;
                        }
                        break;
                    }
                    case Opcode.JmpIfStream:
                    {
                        // Lazy-foreach dispatch: branch when locals[a] is a
                        // sync stream so the materialising fast-path
                        // (ForEachIterable + ListLen + ListGet) is skipped.
                        byte a = Encoding.A(instr);
                        var v = locals[a];
                        if (v != null && v.Type == RuntimeValueType.Stream)
                        {
                            short offs = Encoding.SImm16(instr);
                            pc += offs;
                        }
                        break;
                    }
                    case Opcode.ForEachStreamPull:
                    {
                        // [op][itemSlot:a][streamSlot:b][continueSlot:c]
                        // Synchronous pull from a sync StreamValue. Sets
                        // continueSlot to a boolean (true if value produced;
                        // false if done). The follow-up `JmpIfNot
                        // continueSlot, exitOffset` exits the loop on done
                        // before AssignBinding would read itemSlot.
                        byte itemSlot = Encoding.A(instr);
                        byte streamSlot = Encoding.B(instr);
                        byte continueSlot = Encoding.C(instr);
                        var sv = locals[streamSlot];
                        if (sv is RaLanguage.Interpreter.Values.Streams.StreamValue stream)
                        {
                            var t = stream.PullNext(ctx);
                            var r = t.IsCompletedSuccessfully ? t.Result : t.AsTask().GetAwaiter().GetResult();
                            if (r.Error != null) throw new RaUserError(r.Error);
                            if (r.Done)
                            {
                                locals[continueSlot] = BooleanValue.False;
                            }
                            else
                            {
                                locals[itemSlot] = r.Value!;
                                locals[continueSlot] = BooleanValue.True;
                            }
                        }
                        else if (sv is RaLanguage.Interpreter.Values.Async.AsyncStreamValue astream)
                        {
                            // L8 — `for await x in stream`. The async-stream pull
                            // (AsyncStreamCore.PullNext) blocks on the channel until
                            // the producer FIBER (a thread-pool thread) sends or the
                            // stream closes — cross-thread, so the blocking pull is
                            // safe (no cooperative-yield needed) and adds no await
                            // point. Byte-identical to ForAwaitNodeVisitor's loop.
                            OpForAwaitPull(locals, itemSlot, continueSlot, astream, ctx);
                        }
                        else
                        {
                            throw new RaUserError(MakeIcError(ctx, "ForEachStreamPull: source slot is not a Stream"));
                        }
                        break;
                    }
                    case Opcode.AndJz:
                    {
                        byte a = Encoding.A(instr);
                        if (!ReadSlotTruth(f, locals, a))
                        {
                            short offs = Encoding.SImm16(instr);
                            pc += offs;
                        }
                        break;
                    }
                    case Opcode.OrJnz:
                    {
                        byte a = Encoding.A(instr);
                        if (ReadSlotTruth(f, locals, a))
                        {
                            short offs = Encoding.SImm16(instr);
                            pc += offs;
                        }
                        break;
                    }

                    // M90 fused compare-and-branch. One dispatch replaces the
                    // `cmpII; JmpIfNot` pair (37% of the bench suite's
                    // dispatches). The off-stack `FusedCmpBranchDelta` helper
                    // keeps the dispatch-loop frame compact (depth-2000
                    // recursion budget), same discipline as ExecuteUnboxedII.
                    case Opcode.JmpNotLtII:
                    case Opcode.JmpNotLeII:
                    case Opcode.JmpNotGtII:
                    case Opcode.JmpNotGeII:
                    case Opcode.JmpNotEqII:
                    case Opcode.JmpNotNeII:
                    {
                        int d = FusedCmpBranchDelta(f, locals, instr, op);
                        if (d < 0) f.Function.LoopBackEdgeCount++;
                        pc += d;
                        break;
                    }

                    // -------- unary --------
                    case Opcode.Neg:
                    {
                        // Mirrors UnaryOperationNodeVisitor: synthesize -x as
                        // x * NumberValue(-1). MultedBy on each numeric type
                        // returns the same-typed result so semantics are
                        // identical (overflow trapping, NaN behavior).
                        byte a = Encoding.A(instr);
                        byte b = Encoding.B(instr);
                        var v = locals[b];
                        if (v == null) throw new RaUserError(MakeIcError(ctx, "VM: null operand in neg"));
                        var (r, e) = v.MultedBy(s_negOne);
                        if (e != null) throw new RaUserError(e);
                        locals[a] = r;
                        break;
                    }
                    case Opcode.Not:
                    {
                        byte a = Encoding.A(instr);
                        byte b = Encoding.B(instr);
                        var v = locals[b];
                        if (v == null) throw new RaUserError(MakeIcError(ctx, "VM: null operand in not"));
                        var (r, e) = v.Notted();
                        if (e != null) throw new RaUserError(e);
                        locals[a] = r;
                        // M74: dual-rep — populate the Bool tag
                        // shadow so a downstream BB / typed reader
                        // can fast-path through `TryReadAsBool`
                        // without going through `BooleanValue.IsTrue`
                        // virtual dispatch. The `r` value is the
                        // boxed result; if it's a BooleanValue the
                        // tag mirrors it bit-for-bit, otherwise we
                        // leave the tag at the pre-clear's Ref.
                        if (a < f.Slots.Length && r is BooleanValue rb)
                        {
                            f.Slots[a].Tag = ValueSlotTag.Bool;
                            f.Slots[a].Bits = rb.Value ? 1 : 0;
                        }
                        break;
                    }
                    case Opcode.BNot:
                    {
                        byte a = Encoding.A(instr);
                        byte b = Encoding.B(instr);
                        var v = locals[b];
                        if (v == null) throw new RaUserError(MakeIcError(ctx, "VM: null operand in bnot"));
                        var (r, e) = v.BitwiseNotted();
                        if (e != null) throw new RaUserError(e);
                        locals[a] = r;
                        break;
                    }

                    // L7 (Match variant patterns). EnumTagEq: dst = scrutinee is
                    // an EnumValue whose member name == Names[c]. EnumPayload: dst
                    // = scrutinee.Payload[c]. Both read locals[B] (the scrutinee),
                    // C is an immediate. The bodies live in NoInlining helpers so
                    // their locals stay OUT of this async method's MoveNext frame:
                    // Execute recurses via `await Execute(...)` and that frame is
                    // held across every synchronous recursion level, so each byte
                    // added here is paid ×depth against the worker stack (the M85
                    // deep-recursion trap). The helper frame is popped before the
                    // recursive await, so it costs nothing per level.
                    case Opcode.EnumTagEq:
                        OpEnumTagEq(locals, instr, names, ctx);
                        break;
                    case Opcode.EnumPayload:
                        OpEnumPayload(locals, instr, ctx);
                        break;
                    case Opcode.MatchArity:
                        OpMatchArity(locals, instr, ctx);
                        break;
                    case Opcode.EnumNameEq:
                        OpEnumNameEq(locals, instr, names);
                        break;
                    case Opcode.MatchFail:
                        // Non-exhaustive match at runtime: no arm matched and there
                        // is no catch-all. Same error the visitor's no-match path
                        // raises (parity captures err.Details).
                        throw OpMatchFail(ctx);
                    case Opcode.TupleShape:
                        OpTupleShape(locals, instr);
                        break;
                    case Opcode.StructShape:
                        OpStructShape(locals, instr, names);
                        break;
                    case Opcode.StructFieldGet:
                        OpStructFieldGet(locals, instr, names, ctx);
                        break;
                    case Opcode.ListShape:
                        OpListShape(locals, instr);
                        break;
                    case Opcode.ListElemBack:
                        OpListElemBack(locals, instr);
                        break;
                    case Opcode.ListRestSlice:
                        OpListRestSlice(locals, instr);
                        break;
                    case Opcode.IsType:
                        OpIsType(locals, instr, wideHiC, f.Function.AstRefs, ctx);
                        break;
                    case Opcode.MapShape:
                        OpMapShape(locals, instr);
                        break;
                    case Opcode.MapHasKey:
                        OpMapHasKey(locals, instr, ctx);
                        break;
                    case Opcode.MapGetKey:
                        OpMapGetKey(locals, instr, ctx);
                        break;
                    case Opcode.TryUnwrap:
                    {
                        // Ok: dst is set, fall through. Err: early-return the Err
                        // value through the standard return channel (like Ret).
                        var early = OpTryUnwrap(locals, instr, ctx);
                        if (early != null) { f.Pc = pc; return res.SuccessReturn(early); }
                        break;
                    }
                    case Opcode.DeclareLocalByName:
                        OpDeclareLocalByName(locals, instr, names, ctx);
                        break;
                    case Opcode.DestructureFail:
                        throw new RaUserError(MakeIcError(ctx,
                            "destructuring pattern did not match the initializer value"));
                    case Opcode.Await:
                    {
                        // L8 — `await x`. The target (already evaluated by opcodes)
                        // is at B; await it via the shared AwaitNodeVisitor.Await
                        // ValueCore (byte-identical to the visitor). This adds an
                        // await point to Execute's MoveNext — the 128 MB worker
                        // stack carries the per-recursion-level cost.
                        byte aDst = Encoding.A(instr);
                        byte aSrc = Encoding.B(instr);
                        var awaited = await Visitors.Async.AwaitNodeVisitor.AwaitValueCore(
                            locals[aSrc], ctx, DummyPos(ctx), DummyPos(ctx)).ConfigureAwait(false);
                        if (awaited.Error != null) throw new RaUserError(awaited.Error);
                        locals[aDst] = awaited.Value ?? NullValue.Null;
                        break;
                    }
                    case Opcode.Emit:
                        // L8 — `emit x` (sync): push the already-evaluated value
                        // at A into the current async-stream producer.
                        OpEmit(locals[Encoding.A(instr)], ctx);
                        break;
                    case Opcode.Spawn:
                    {
                        // L8 — `spawn f(args)` (sync schedule). Callee at B, args at
                        // B+1..B+argCount (Call layout). Schedule the fiber, dst =
                        // the TaskValue. AsyncScheduler.Schedule returns immediately.
                        byte sDst = Encoding.A(instr);
                        var sub = OpSpawn(locals, Encoding.B(instr), Encoding.C(instr), ctx);
                        if (sub.Error != null) throw new RaUserError(sub.Error);
                        locals[sDst] = sub.Value ?? NullValue.Null;
                        break;
                    }

                    case Opcode.AsmInvoke:
                    {
                        // L9 — inline pure-text `asm { … }`. The AsmBlockNode is
                        // parked in DefineRefs[imm16]; OpAsmInvoke rebuilds its
                        // constant source, assembles-on-first-use (cached), and
                        // executes via the shared AsmBlockExecCore. dst = the
                        // narrowed return value (or a 2-tuple). Off-stack helper
                        // protects the M85 deep-recursion frame budget.
                        byte amDst = Encoding.A(instr);
                        locals[amDst] = OpAsmInvoke(f, Encoding.Imm16(instr), ctx);
                        break;
                    }

                    case Opcode.AsmInvokeI:
                    {
                        // L10 — interpolated inline `asm { … %{e} … }`. The parked
                        // AsmBlockNode is in DefineRefs[c]; the %{…} args are
                        // pre-evaluated in the band [b .. b+N-1]. OpAsmInvokeI
                        // formats them into the source + assembles + executes.
                        byte aiDst = Encoding.A(instr);
                        locals[aiDst] = OpAsmInvokeI(f, locals, Encoding.B(instr), Encoding.C(instr), ctx);
                        break;
                    }

                    case Opcode.FinallyEnd:
                    {
                        // L10 — end of a `finally` body, reached by normal fall-
                        // through. Apply whatever was stashed before the finally:
                        //  (1) a control-flow escape (return/yield) that occurred
                        //      in the try/catch body → resume it now;
                        //  (2) else a pending error → re-raise it.
                        // The finally's OWN control flow / a fresh throw would have
                        // exited before reaching here, naturally overriding both.
                        if (f.PendingFlowKind != 0)
                        {
                            byte k = f.PendingFlowKind;
                            var v = f.PendingFlowValue ?? NullValue.Null;
                            f.PendingFlowKind = 0;
                            f.PendingFlowValue = null;
                            f.Pc = pc;
                            return k == 2 ? res.SuccessYield(v) : res.SuccessReturn(v);
                        }
                        if (f.PendingError != null)
                        {
                            var pe = f.PendingError;
                            f.PendingError = null;
                            throw new RaUserError(pe);
                        }
                        break;
                    }

                    case Opcode.SetPendingFlow:
                    {
                        // L10 — stash a `return`/`yield` escaping through an
                        // enclosing finally (the IrCompiler emits this + a jump to
                        // the finally instead of OP_RET/RetYield). kind: 1=return,
                        // 2=yield; value at slot a.
                        f.PendingFlowKind = Encoding.B(instr);
                        f.PendingFlowValue = locals[Encoding.A(instr)] ?? NullValue.Null;
                        break;
                    }

                    case Opcode.AnnotationApply:
                    {
                        // L10 — standalone `@Name(args)` value. The parked
                        // AnnotationApplicationNode is in DefineRefs[imm16];
                        // OpAnnotationApply builds the AnnotationInstanceValue via
                        // the shared (sync) visitor core.
                        byte aDst = Encoding.A(instr);
                        locals[aDst] = OpAnnotationApply(f, Encoding.Imm16(instr), ctx);
                        break;
                    }

                    default:
                        throw new RaUserError(MakeIcError(ctx,
                            $"VM: opcode {op} (0x{(byte)op:X2}) not implemented yet (PC={pc - 1})"));
                }
                }
                catch (RaUserError ue)
                {
                    // User-visible Ra error raised from inside a switch case.
                    // Scan EhTable for a matching try/catch region; if found,
                    // restore the runtime ctx to the depth at try-entry,
                    // bind the error message into the catch slot, and jump
                    // to the catch PC. If no handler matches, propagate as a
                    // RuntimeResult.Failure to the outer caller.
                    var eh = f.Function.EhTable;
                    int faultPc = pc - 1;
                    // Pick the innermost (smallest-region) covering handler.
                    // Order in EhTable reflects compile order: nested try
                    // bodies register *before* the outer try, so naive
                    // scanning could prefer the outer. Span-based selection
                    // is unambiguous.
                    int bestIdx = -1;
                    int bestSpan = int.MaxValue;
                    for (int i = 0; i < eh.Length; i++)
                    {
                        var h = eh[i];
                        // L10: a handler covers the fault if it has a catch OR a
                        // finally to route into (a try/finally with no catch, or
                        // an exception escaping a catch body).
                        if (faultPc >= h.StartPc && faultPc < h.EndPc && (h.CatchPc >= 0 || h.FinallyPc >= 0))
                        {
                            int span = h.EndPc - h.StartPc;
                            if (span < bestSpan)
                            {
                                bestSpan = span;
                                bestIdx = i;
                            }
                        }
                    }
                    if (bestIdx >= 0)
                    {
                        var h = eh[bestIdx];
                        while (f.CtxDepth > h.ScopeDepth && ctx.Parent != null)
                        {
                            ctx = ctx.Parent;
                            f.CtxDepth--;
                        }
                        if (h.CatchPc >= 0)
                        {
                            // Prefer the raw thrown value carried by a user
                            // `throw expr` so pattern-based catch clauses can
                            // destructure the original (typed) value.
                            // System-raised errors leave ThrownValue null and
                            // fall back to a StringValue rendering.
                            RaLanguage.Interpreter.Values.RuntimeValue catchValue;
                            if (ue.Err is RaLanguage.Errors.Types.RuntimeError rerr && rerr.ThrownValue != null)
                            {
                                catchValue = rerr.ThrownValue;
                            }
                            else
                            {
                                string msg = ue.Err.Diagnostic?.Message ?? ue.Err.ToString() ?? "<error>";
                                catchValue = new StringValue(msg).SetContext(ctx);
                            }
                            // M83 — explicit catch-slot tag normalisation.
                            // See original comment block: the catch slot may
                            // not have been pre-cleared by the dispatch
                            // loop's bitmap, so we explicitly normalise to
                            // a Ref slot before the boxed write.
                            if ((uint)h.CatchSlot < (uint)f.Slots.Length)
                            {
                                ref var catchSlot = ref f.Slots[h.CatchSlot];
                                catchSlot.Tag = ValueSlotTag.Ref;
                                catchSlot.Bits = 0;
                                catchSlot.Ref = null;
                            }
                            locals[h.CatchSlot] = catchValue;
                            pc = h.CatchPc;
                        }
                        else
                        {
                            // L10 finally-only route (try/finally with no catch, or
                            // an exception escaping a catch body): stash the error
                            // and run the finally; OP_FINALLY_END re-raises it.
                            f.PendingError = ue.Err;
                            pc = h.FinallyPc;
                        }
                    }
                    else
                    {
                        f.Pc = pc;
                        return res.Failure(ue.Err);
                    }
                }
            }
            }
            finally
            {
                s_callDepth--;
            }
        }

        // M66.2 inverse-direction helper: read a slot as a long. If
        // `LongValid[slot]` is true the slot already lives in
        // `LongLocals` — return it directly. Otherwise try to coerce
        // the boxed `RuntimeValue` (must be a `NumberValue` fitting
        // int64). Returns `false` on coercion failure; caller deopts.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool TryReadAsLong(VmFrame f, LocalsView locals, int slot, out long value)
        {
            if (slot < f.Slots.Length && f.Slots[slot].Tag == ValueSlotTag.Int64)
            {
                value = f.Slots[slot].Bits;
                return true;
            }
            var v = locals[slot];
            if (v is NumberValue nv && TryGetInt64(nv, out value)) return true;
            value = 0;
            return false;
        }

        // M74 truthiness fast-read used by JmpIf / JmpIfNot /
        // AndJz / OrJnz. Returns the boolean truth of the slot —
        // Bool tag fast path first, then boxed `IsTrue()` virtual.
        // `[NoInlining]` keeps the dispatch-loop's C# stack frame
        // compact (depth-2000 recursion test is the tripwire).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadSlotTruth(VmFrame f, LocalsView locals, int slot)
        {
            if (slot < f.Slots.Length && f.Slots[slot].Tag == ValueSlotTag.Bool)
                return (f.Slots[slot].Bits & 1) != 0;
            var v = locals[slot];
            return v != null && v.IsTrue();
        }

        // M90 fused compare-and-branch. Returns the pc delta to apply
        // (0 = fall through, else the signed-8 branch offset). Reads the
        // two operand slots as int64; on a tag miss it deopts to the
        // boxed virtual comparison so observable semantics match the
        // unfused `cmpII; JmpIfNot` it replaces exactly. Branch is taken
        // when the comparison is FALSE (JmpIfNot semantics). `[NoInlining]`
        // keeps the dispatch-loop C# frame compact (depth-2000 recursion
        // tripwire) — identical discipline to ExecuteUnboxedII.
        // L4 OP_FMT off-stack body. `${expr:spec}` — value in slot b, the
        // FormatSpec packed into the int constant at index c (FormatSpec.Pack).
        // Unpack + run the same FormatEngine the visitor uses (no re-parse).
        // NoInlining keeps the dispatch-loop frame compact (depth-2000 budget).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue ExecuteFmt(VmFrame f, LocalsView locals, uint instr, Context ctx, int pc)
        {
            byte exprSlot = Encoding.B(instr);
            byte specConstIdx = Encoding.C(instr);
            var fval = locals[exprSlot];
            int packed = ((IntegerValue)f.Function.Consts[specConstIdx]).Value;
            var spec = Types.Formatting.FormatSpec.Unpack(packed);
            var (fStart, fEnd) = ResolveSpan(f, pc, ctx);
            var (ftext, ferr) = Types.Formatting.FormatEngine.Format(fval!, spec, fStart, fEnd, ctx);
            if (ferr != null) throw new RaUserError(ferr);
            return new StringValue(ftext ?? string.Empty).SetContext(ctx);
        }

        // L4 OP_WITH off-stack body. `recv with { f: v, ... }` — receiver at
        // slot base, the N pre-evaluated update values at base+1..base+N; the
        // WithExpressionNode parked in DefineRefs[c] supplies the static field
        // names / types. Shallow-clone + validate + field-set in the shared
        // WithExpressionOps helper (byte-identical to the visitor). NoInlining
        // keeps the locals off the dispatch-loop frame (depth-2000 budget).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue ExecuteWith(VmFrame f, LocalsView locals, uint instr, Context ctx)
        {
            byte baseSlot = Encoding.B(instr);
            byte refIdx = Encoding.C(instr);
            var wrefs = f.Function.DefineRefs;
            if (refIdx >= wrefs.Length ||
                wrefs[refIdx] is not Parser.Nodes.Operations.WithExpressionNode wnode)
                throw new RaUserError(MakeIcError(ctx, "VM: OP_WITH ref is not a WithExpressionNode"));
            int wcount = wnode.Updates.Count;
            if (baseSlot + wcount >= locals.Length)
                throw new RaUserError(MakeIcError(ctx, "VM: OP_WITH value slots exceed frame"));
            var wvalues = new RuntimeValue[wcount];
            for (int i = 0; i < wcount; i++) wvalues[i] = locals[baseSlot + 1 + i]!;
            var (wresult, werr) = Runtime.WithExpressionOps.Apply(locals[baseSlot], wnode, wvalues, ctx);
            if (werr != null) throw new RaUserError(werr);
            return wresult!;
        }

        // L10 OP_CALL_GENERIC off-stack body. Mirrors the OP_CALL inline handler
        // but reads argCount (= ArgNodes.Count) + the explicit GenericTypeArgs
        // from the FunctionCallNode parked in DefineRefs[c] (With-shaped), and
        // threads the type args to FunctionCallExecutor.Invoke (the SAME chokepoint
        // the AST visitor uses → identical generic-dispatch semantics).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static async System.Threading.Tasks.ValueTask<RuntimeValue> ExecuteCallGeneric(
            VmFrame f, LocalsView locals, uint instr, Context ctx)
        {
            if (ctx.AreCallsBlocked)
                throw new RaUserError(MakeIcError(ctx, "function calls are not allowed in this context"));
            byte fnSlot = Encoding.B(instr);
            int refIdx = Encoding.C(instr);
            var grefs = f.Function.DefineRefs;
            if (grefs == null || refIdx >= grefs.Length
                || grefs[refIdx] is not Parser.Nodes.Functions.FunctionCallNode gfc)
                throw new RaUserError(MakeIcError(ctx, "VM: OP_CALL_GENERIC ref is not a FunctionCallNode"));
            int argCount = gfc.ArgNodes.Count;
            var fn = locals[fnSlot];
            if (fn == null)
                throw new RaUserError(MakeIcError(ctx, "VM: callee slot is null"));
            // Split the contiguous arg band into positionals + named args by each
            // ArgNode's compile-time NameTok (values are runtime, names static) —
            // mirrors FunctionCallNodeVisitor → generic / named / mixed call.
            var argList = RentArgList(argCount);
            System.Collections.Generic.Dictionary<string, RuntimeValue>? named = null;
            for (int i = 0; i < argCount; i++)
            {
                var a = locals[fnSlot + 1 + i];
                if (a == null)
                    throw new RaUserError(MakeIcError(ctx, $"VM: argument {i} slot is null"));
                var nameTok = gfc.ArgNodes[i].NameTok;
                if (nameTok != null)
                {
                    named ??= new System.Collections.Generic.Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal);
                    named[nameTok.Value.ToString() ?? ""] = a;
                }
                else argList.Add(a);
            }
            var emptyNamed = Runtime.Calls.FunctionCallExecutor.EmptyNamedArgs;
            var pos = DummyPos(ctx);
            var invokeTask = Runtime.Calls.FunctionCallExecutor.Invoke(
                fn, argList, named ?? emptyNamed, gfc.GenericTypeArgs, pos, pos, ctx);
            RuntimeResult invokeRes;
            if (invokeTask.IsCompletedSuccessfully)
            {
                invokeRes = invokeTask.Result;
                ReturnArgList(argList);
            }
            else
                invokeRes = await invokeTask.ConfigureAwait(false);
            if (invokeRes.Error != null) throw new RaUserError(invokeRes.Error);
            return invokeRes.Value!;
        }

        // L5 OP_DEFINE_TYPE off-stack body. Reconstruct + register a one-shot
        // type from its flat descriptor (RaFunction.TypeDefs[imm16]). Dispatches
        // on the descriptor kind; each kind shares the SAME registration helper
        // the visitor fallback uses (byte-identical runtime type).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue ExecuteDefineType(VmFrame f, uint instr, Context ctx, int pc, IInterpreter interpreter)
        {
            ushort tdIdx = Encoding.Imm16(instr);
            var defs = f.Function.TypeDefs;
            if (defs == null || tdIdx >= defs.Length)
                throw new RaUserError(MakeIcError(ctx, $"VM: DefineType index {tdIdx} out of range"));
            var def = defs[tdIdx];
            switch (def.Kind)
            {
                case IR.Defs.TypeDefKind.Enum:
                    return DefineEnum((IR.Defs.EnumDef)def, ctx, f, pc, interpreter);
                case IR.Defs.TypeDefKind.Delegate:
                    return DefineDelegate((IR.Defs.DelegateDef)def, ctx, f, pc);
                case IR.Defs.TypeDefKind.Using:
                    return DefineUsing((IR.Defs.UsingDef)def, ctx, f, pc);
                case IR.Defs.TypeDefKind.Struct:
                    return DefineStruct((IR.Defs.StructDef)def, ctx, f, pc, interpreter);
                case IR.Defs.TypeDefKind.Record:
                    return DefineRecord((IR.Defs.RecordDef)def, ctx, f, pc, interpreter);
                case IR.Defs.TypeDefKind.Class:
                    return DefineClass((IR.Defs.ClassDef)def, ctx, f, pc, interpreter);
                case IR.Defs.TypeDefKind.Trait:
                    return DefineTrait((IR.Defs.TraitDef)def, ctx, f, pc, interpreter);
                case IR.Defs.TypeDefKind.Extension:
                    return DefineExtension((IR.Defs.ExtensionDef)def, ctx, f, pc);
                case IR.Defs.TypeDefKind.Interface:
                    return DefineInterface((IR.Defs.InterfaceDef)def, ctx, f, pc, interpreter);
                case IR.Defs.TypeDefKind.Annotation:
                    return DefineAnnotation((IR.Defs.AnnotationDef)def, ctx, f, pc, interpreter);
                case IR.Defs.TypeDefKind.Import:
                    return DefineImport((IR.Defs.ImportDef)def, ctx, f, pc, interpreter);
                case IR.Defs.TypeDefKind.Namespace:
                    return DefineNamespace((IR.Defs.NamespaceDef)def, ctx, f, pc, interpreter);
                default:
                    throw new RaUserError(MakeIcError(ctx, $"VM: DefineType unsupported kind {def.Kind}"));
            }
        }

        // L5e: reconstruct the (stub-bodied) StructDefinitionNode the runtime
        // StructTypeValue API expects from the flat StructDef, wiring each
        // method's precompiled RaFunction into CompiledBody, then run the SAME
        // visitor Apply — so registration, validation (to_string, const fields),
        // and dispatch are byte-identical to the AST path; only the method
        // bodies are pre-compiled (the visitor would compile the same body
        // lazily on first call).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineStruct(IR.Defs.StructDef def, Context ctx, VmFrame f, int pc, IInterpreter interpreter)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);

            var fields = new System.Collections.Generic.List<Parser.Nodes.Structs.StructFieldDefinitionNode>(def.Fields.Length);
            foreach (var fd in def.Fields)
            {
                // Rebuild a const default as a NumberNode whose CachedValue is
                // the folded value — NumberNodeVisitor returns CachedValue
                // verbatim (any type), so field-init evaluates to exactly the
                // folded const (byte-identical to the visitor's evaluation). A
                // NON-CONST default lowered to a thunk needs a NON-NULL stub
                // DefaultValueNode (so construction enters the default branch) — a
                // PassNode; the thunk is wired below (DefaultCompiledBody) and runs
                // instead of the stub. Mirrors ReconstructProperty.
                AstNode? defNode = null;
                if (fd.CompiledDefault != null)
                    defNode = new Parser.Nodes.Operations.PassNode(s, e);
                else if (fd.DefaultConst != null)
                {
                    defNode = new Parser.Nodes.Primitives.NumberNode(
                        new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e))
                    { CachedValue = fd.DefaultConst };
                }
                var fieldNode = new Parser.Nodes.Structs.StructFieldDefinitionNode(
                    fd.IsPublic,
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, fd.Name, s, e),
                    fd.FieldType,
                    defNode,
                    fd.IsStatic, fd.IsAbstract, fd.IsOverride,
                    (Parser.Nodes.Variables.VariableDeclarationType)fd.DeclKind);
                if (fd.CompiledDefault != null)
                {
                    fieldNode.DefaultCompiledBody = fd.CompiledDefault;
                    fieldNode.DefaultIrCompileTried = true;
                }
                fields.Add(fieldNode);
            }

            var methods = new System.Collections.Generic.List<Parser.Nodes.Structs.StructMethodDefinitionNode>(def.Methods.Length);
            foreach (var md in def.Methods)
                methods.Add(ReconstructStructMethod(md, s, e));

            var node = new Parser.Nodes.Structs.StructDefinitionNode(
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, def.Name, s, e),
                def.IsPublic, fields, methods,
                ReconstructOperators(def.Operators, s, e),
                new System.Collections.Generic.List<string>(def.Generics),
                ReconstructWheres(def.Wheres, s, e),
                ReconstructProperties(def.Properties, s, e),
                ReconstructEvents(def.Events, s, e));
            if (def.Annotations.Length > 0)
                node.Annotations = new System.Collections.Generic.List<Parser.Nodes.Annotations.AnnotationApplicationNode>(def.Annotations);

            var result = Visitors.Structs.StructDefinitionNodeVisitor.Apply(node, ctx, interpreter);
            if (result.Error != null) throw new RaUserError(result.Error);
            return result.Value!;
        }

        // Shared by struct + record: rebuild a StructMethodDefinitionNode (stub
        // PassNode body) with the precompiled RaFunction wired into CompiledBody.
        private static Parser.Nodes.Structs.StructMethodDefinitionNode ReconstructStructMethod(
            IR.Defs.StructMethodDef md, Lexer.Position s, Lexer.Position e)
        {
            var argToks = new System.Collections.Generic.List<Lexer.Tokens.Token>(md.ArgNames.Length);
            foreach (var an in md.ArgNames)
                argToks.Add(new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, an, s, e));
            var argTypes = new System.Collections.Generic.List<Types.TypeDescriptor?>(md.ArgTypes);
            var refParams = new System.Collections.Generic.List<bool>(md.IsRefParams);
            // Rebuild const-folded param defaults as NumberNodes carrying CachedValue
            // (NumberNodeVisitor returns CachedValue verbatim) — byte-identical to the
            // visitor's default-arg evaluation. Null slots stay null (no default).
            var paramDefaults = new System.Collections.Generic.List<AstNode?>(new AstNode?[md.ArgNames.Length]);
            for (int i = 0; i < md.ArgNames.Length && i < md.ParamDefaultConsts.Length; i++)
                if (md.ParamDefaultConsts[i] != null)
                    paramDefaults[i] = new Parser.Nodes.Primitives.NumberNode(
                        new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e))
                    { CachedValue = md.ParamDefaultConsts[i] };
            Lexer.Tokens.Token? varArgTok = md.VarArgName == null ? null
                : new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, md.VarArgName, s, e);

            var mnode = new Parser.Nodes.Structs.StructMethodDefinitionNode(
                md.IsPublic, md.IsConstructor,
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, md.Name, s, e),
                argToks, argTypes, refParams, paramDefaults,
                md.HasVarArgs, varArgTok, md.VarArgType, md.ReturnType,
                new Parser.Nodes.Operations.PassNode(s, e),
                md.ShouldAutoReturn);
            mnode.CompiledBody = md.Body;
            mnode.IrCompileTried = true;
            mnode.FrameId = md.FrameId;
            mnode.IsAsync = md.IsAsync;
            mnode.IsAsyncStream = md.IsAsyncStream;
            return mnode;
        }

        // L5e: reconstruct the (stub-bodied) RecordDefinitionNode from a flat
        // RecordDef + precompiled method bodies, then run the SAME visitor Apply.
        // First sub-stage: value records, no inheritance (BaseType always null).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineRecord(IR.Defs.RecordDef def, Context ctx, VmFrame f, int pc, IInterpreter interpreter)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);

            var primaryFields = new System.Collections.Generic.List<Parser.Nodes.Records.RecordPrimaryFieldNode>(def.PrimaryFields.Length);
            foreach (var pf in def.PrimaryFields)
            {
                AstNode? defNode = null;
                if (pf.DefaultConst != null)
                    defNode = new Parser.Nodes.Primitives.NumberNode(
                        new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e))
                    { CachedValue = pf.DefaultConst };
                primaryFields.Add(new Parser.Nodes.Records.RecordPrimaryFieldNode(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, pf.Name, s, e),
                    pf.FieldType, defNode, pf.IsPublic, pf.IsMutable));
            }

            var methods = new System.Collections.Generic.List<Parser.Nodes.Structs.StructMethodDefinitionNode>(def.Methods.Length);
            foreach (var md in def.Methods)
                methods.Add(ReconstructStructMethod(md, s, e));

            var node = new Parser.Nodes.Records.RecordDefinitionNode(
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, def.Name, s, e),
                def.IsPublic, def.IsRefRecord, def.IsAbstract,
                def.BaseType, ReconstructConstArgs(def.BaseArgConsts, s, e),
                primaryFields, methods,
                ReconstructOperators(def.Operators, s, e),
                new System.Collections.Generic.List<string>(def.Generics),
                ReconstructWheres(def.Wheres, s, e),
                ReconstructProperties(def.Properties, s, e),
                ReconstructEvents(def.Events, s, e));
            // Restore the @derive-controlled auto flags (default true).
            node.AutoEquals = def.AutoEquals;
            node.AutoToString = def.AutoToString;
            if (def.Annotations.Length > 0)
                node.Annotations = new System.Collections.Generic.List<Parser.Nodes.Annotations.AnnotationApplicationNode>(def.Annotations);

            var result = Visitors.Records.RecordDefinitionNodeVisitor.Apply(node, ctx, interpreter);
            if (result.Error != null) throw new RaUserError(result.Error);
            return result.Value!;
        }

        // Rebuild const-folded base-ctor args as NumberNodes carrying CachedValue
        // (NumberNodeVisitor returns CachedValue verbatim) — byte-identical to the
        // visitor evaluating the original literal base args.
        private static System.Collections.Generic.List<AstNode>? ReconstructConstArgs(
            RuntimeValue?[] consts, Lexer.Position s, Lexer.Position e)
        {
            if (consts == null || consts.Length == 0) return null;
            var list = new System.Collections.Generic.List<AstNode>(consts.Length);
            foreach (var c in consts)
                list.Add(new Parser.Nodes.Primitives.NumberNode(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e)) { CachedValue = c });
            return list;
        }

        // Reconstruct a class method (FunctionDefinitionNode) with stub body +
        // precompiled RaFunction wired into CompiledBody.
        private static Parser.Nodes.Functions.FunctionDefinitionNode ReconstructClassMethod(
            IR.Defs.ClassMethodDef md, Lexer.Position s, Lexer.Position e)
        {
            var argToks = new System.Collections.Generic.List<Lexer.Tokens.Token>(md.ArgNames.Length);
            foreach (var an in md.ArgNames)
                argToks.Add(new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, an, s, e));
            var argTypes = new System.Collections.Generic.List<Types.TypeDescriptor?>(md.ArgTypes);
            var refParams = new System.Collections.Generic.List<bool>(md.IsRefParams);
            // Rebuild const-folded param defaults as NumberNodes carrying CachedValue
            // (NumberNodeVisitor returns CachedValue verbatim) — byte-identical to the
            // visitor's default-arg evaluation. Null slots stay null (no default).
            var paramDefaults = new System.Collections.Generic.List<AstNode?>(new AstNode?[md.ArgNames.Length]);
            for (int i = 0; i < md.ArgNames.Length && i < md.ParamDefaultConsts.Length; i++)
                if (md.ParamDefaultConsts[i] != null)
                    paramDefaults[i] = new Parser.Nodes.Primitives.NumberNode(
                        new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e))
                    { CachedValue = md.ParamDefaultConsts[i] };
            Lexer.Tokens.Token? varArgTok = md.VarArgName == null ? null
                : new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, md.VarArgName, s, e);

            var mGenerics = md.Generics.Length == 0
                ? null
                : new System.Collections.Generic.List<string>(md.Generics);
            var mnode = new Parser.Nodes.Functions.FunctionDefinitionNode(
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, md.Name, s, e),
                argToks, argTypes, refParams, paramDefaults,
                md.HasVarArgs, varArgTok, md.VarArgType, md.ReturnType,
                new Parser.Nodes.Operations.PassNode(s, e), md.ShouldAutoReturn,
                mGenerics, md.IsPublic, md.IsConstructor, md.IsOverride, md.IsAbstract, md.IsStatic);
            // Abstract methods carry no body (md.Body == null) and are never invoked
            // (ClassTypeValue filters dispatch/compile on !IsAbstract); concrete
            // methods wire the precompiled RaFunction.
            mnode.CompiledBody = md.Body;
            mnode.IrCompileTried = true;
            mnode.FrameId = md.FrameId;
            mnode.IsAsync = md.IsAsync;
            mnode.IsAsyncStream = md.IsAsyncStream;
            mnode.IsFactory = md.IsFactory;          // L10 factory ctor
            mnode.ConstructorName = md.ConstructorName; // L10 named ctor
            return mnode;
        }

        // L10 one-shot-defn widening: reconstruct an OperatorDefinitionNode with a
        // stub body + the precompiled RaFunction wired into CompiledBody. The
        // operator-invocation path (BoundOperatorValue.Execute) routes through
        // GetOrCompileOperator → returns CompiledBody when IrCompileTried is set,
        // so the stub PassNode is never compiled/executed. Shared by struct/class/
        // record/extension reconstruction.
        private static Parser.Nodes.Classes.OperatorDefinitionNode ReconstructOperator(
            IR.Defs.OperatorDef od, Lexer.Position s, Lexer.Position e)
        {
            var onode = new Parser.Nodes.Classes.OperatorDefinitionNode(
                od.IsPublic, od.IsOverride, od.IsStatic,
                new Lexer.Tokens.Token(od.OpTokenType, od.Symbol, s, e),
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, od.ArgName, s, e),
                od.ArgType, od.ReturnType,
                new Parser.Nodes.Operations.PassNode(s, e), od.ShouldAutoReturn,
                new System.Collections.Generic.List<string>(od.Generics), null);
            onode.CompiledBody = od.Body;
            onode.IrCompileTried = true;
            onode.FrameId = od.FrameId;
            return onode;
        }

        // Shared: reconstruct an operator list from OperatorDef[] (empty → empty).
        private static System.Collections.Generic.List<Parser.Nodes.Classes.OperatorDefinitionNode> ReconstructOperators(
            IR.Defs.OperatorDef[] ops, Lexer.Position s, Lexer.Position e)
        {
            var list = new System.Collections.Generic.List<Parser.Nodes.Classes.OperatorDefinitionNode>(ops.Length);
            foreach (var od in ops) list.Add(ReconstructOperator(od, s, e));
            return list;
        }

        // L10 generic type-def widening: reconstruct a where-constraint list from
        // WhereConstraintDef[] (empty → empty). Shared by struct/class/record.
        private static System.Collections.Generic.List<Parser.Nodes.Special.WhereConstraintNode> ReconstructWheres(
            IR.Defs.WhereConstraintDef[] wheres, Lexer.Position s, Lexer.Position e)
        {
            var list = new System.Collections.Generic.List<Parser.Nodes.Special.WhereConstraintNode>(wheres.Length);
            foreach (var wd in wheres)
                list.Add(new Parser.Nodes.Special.WhereConstraintNode(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, wd.ParameterName, s, e),
                    wd.ConstraintType));
            return list;
        }

        // L10 property widening: reconstruct an AUTO PropertyDefinitionNode from a
        // flat PropertyDef — auto accessors (BodyNode null) + a const-folded default
        // (rebuilt as a NumberNode carrying CachedValue, like struct fields). The
        // visitor's PropertyBuilder.Build registers it (backing slot in the hidden
        // class shape); access lowers as field-slot access. Shared by struct/class/
        // record reconstruction.
        private static Parser.Nodes.Properties.PropertyDefinitionNode ReconstructProperty(
            IR.Defs.PropertyDef pd, Lexer.Position s, Lexer.Position e)
        {
            var accessors = new System.Collections.Generic.List<Parser.Nodes.Properties.PropertyAccessorNode>(pd.Accessors.Length);
            foreach (var ad in pd.Accessors)
            {
                var kind = (Parser.Nodes.Properties.PropertyAccessorKind)ad.Kind;
                string kindStr = kind switch
                {
                    Parser.Nodes.Properties.PropertyAccessorKind.Get => "get",
                    Parser.Nodes.Properties.PropertyAccessorKind.Set => "set",
                    Parser.Nodes.Properties.PropertyAccessorKind.Init => "init",
                    Parser.Nodes.Properties.PropertyAccessorKind.Observe => "observe",
                    _ => "get"
                };
                // AUTO accessor (Body null) → bodyNode null (IsAuto true). COMPUTED
                // accessor → a stub PassNode body (IsAuto false → the visitor builds
                // it computed) + the precompiled CompiledBody wired in (RunAccessorBody
                // returns it via GetOrCompileAccessor's IrCompileTried short-circuit;
                // the stub is never compiled/executed).
                AstNode? accBody = ad.Body == null ? null : new Parser.Nodes.Operations.PassNode(s, e);
                var accNode = new Parser.Nodes.Properties.PropertyAccessorNode(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, kindStr, s, e),
                    kind, (Parser.Nodes.Properties.PropertyAccessorVisibility)ad.Visibility, accBody);
                if (ad.Body != null)
                {
                    accNode.CompiledBody = ad.Body;
                    accNode.IrCompileTried = true;
                }
                accessors.Add(accNode);
            }
            // A LAZY default lowered to a thunk needs a NON-NULL DefaultValueNode
            // stub (the lazy first-touch path errors on a null initializer) — a
            // PassNode, mirroring the computed-accessor stub. The thunk is wired in
            // below (DefaultCompiledBody) and runs instead of the stub. A const
            // EAGER default reconstructs as a cached NumberNode.
            AstNode? defNode = null;
            if (pd.CompiledDefault != null)
                defNode = new Parser.Nodes.Operations.PassNode(s, e);
            else if (pd.DefaultConst != null)
                defNode = new Parser.Nodes.Primitives.NumberNode(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e))
                { CachedValue = pd.DefaultConst };
            var node = new Parser.Nodes.Properties.PropertyDefinitionNode(
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, pd.Name, s, e),
                pd.PropertyType, defNode, accessors,
                pd.IsPublic, pd.IsStatic, pd.IsAbstract, pd.IsOverride, pd.IsLazy);
            if (pd.CompiledDefault != null)
            {
                node.DefaultCompiledBody = pd.CompiledDefault;
                node.DefaultIrCompileTried = true;
            }
            return node;
        }

        // Shared: reconstruct a property list from PropertyDef[] (empty → empty).
        private static System.Collections.Generic.List<Parser.Nodes.Properties.PropertyDefinitionNode> ReconstructProperties(
            IR.Defs.PropertyDef[] props, Lexer.Position s, Lexer.Position e)
        {
            var list = new System.Collections.Generic.List<Parser.Nodes.Properties.PropertyDefinitionNode>(props.Length);
            foreach (var pd in props) list.Add(ReconstructProperty(pd, s, e));
            return list;
        }

        // L10 event widening: reconstruct an EventDefinitionNode from flat metadata
        // (events have no accessor bodies) → the visitor's EventBuilder registers it.
        private static Parser.Nodes.Events.EventDefinitionNode ReconstructEvent(
            IR.Defs.EventDef ed, Lexer.Position s, Lexer.Position e)
        {
            var payload = new System.Collections.Generic.List<Parser.Nodes.Events.EventPayloadParam>(ed.PayloadParams.Length);
            foreach (var pp in ed.PayloadParams)
                payload.Add(new Parser.Nodes.Events.EventPayloadParam(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, pp.Name, s, e), pp.Type));

            var accessors = new System.Collections.Generic.List<Parser.Nodes.Events.EventAccessorNode>(ed.Accessors.Length);
            foreach (var ad in ed.Accessors)
            {
                var kind = (Parser.Nodes.Events.EventAccessorKind)ad.Kind;
                string kindStr = kind switch
                {
                    Parser.Nodes.Events.EventAccessorKind.Subscribe => "subscribe",
                    Parser.Nodes.Events.EventAccessorKind.Raise => "raise",
                    _ => "subscribe"
                };
                accessors.Add(new Parser.Nodes.Events.EventAccessorNode(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, kindStr, s, e),
                    kind, (Parser.Nodes.Events.EventAccessorVisibility)ad.Visibility));
            }

            return new Parser.Nodes.Events.EventDefinitionNode(
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, ed.Name, s, e),
                payload, accessors,
                ed.IsPublic, ed.IsStatic, ed.IsAbstract, ed.IsOverride, ed.IsCancellable, ed.IsTolerant, ed.IsAsync);
        }

        private static System.Collections.Generic.List<Parser.Nodes.Events.EventDefinitionNode> ReconstructEvents(
            IR.Defs.EventDef[] events, Lexer.Position s, Lexer.Position e)
        {
            var list = new System.Collections.Generic.List<Parser.Nodes.Events.EventDefinitionNode>(events.Length);
            foreach (var ed in events) list.Add(ReconstructEvent(ed, s, e));
            return list;
        }

        // L5e: reconstruct the (stub-bodied) ClassDefinitionNode from a flat
        // ClassDef + precompiled method bodies, then run the SAME visitor Apply.
        // The visitor is async only to evaluate field defaults — folded const
        // defaults make those awaits complete synchronously, so blocking on the
        // ValueTask never actually blocks for the lowerable subset.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineClass(IR.Defs.ClassDef def, Context ctx, VmFrame f, int pc, IInterpreter interpreter)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);

            var fields = new System.Collections.Generic.List<Parser.Nodes.Structs.StructFieldDefinitionNode>(def.Fields.Length);
            foreach (var fd in def.Fields)
            {
                // A NON-CONST default lowered to a thunk needs a NON-NULL stub
                // DefaultValueNode (PassNode) so construction enters the default
                // branch; the thunk is wired below (DefaultCompiledBody) and runs
                // instead of the stub. A const default rebuilds as a cached NumberNode.
                AstNode? defNode = null;
                if (fd.CompiledDefault != null)
                    defNode = new Parser.Nodes.Operations.PassNode(s, e);
                else if (fd.DefaultConst != null)
                    defNode = new Parser.Nodes.Primitives.NumberNode(
                        new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e))
                    { CachedValue = fd.DefaultConst };
                var fieldNode = new Parser.Nodes.Structs.StructFieldDefinitionNode(
                    fd.IsPublic,
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, fd.Name, s, e),
                    fd.FieldType, defNode, fd.IsStatic, fd.IsAbstract, fd.IsOverride,
                    (Parser.Nodes.Variables.VariableDeclarationType)fd.DeclKind);
                if (fd.CompiledDefault != null)
                {
                    fieldNode.DefaultCompiledBody = fd.CompiledDefault;
                    fieldNode.DefaultIrCompileTried = true;
                }
                fields.Add(fieldNode);
            }

            var methods = new System.Collections.Generic.List<Parser.Nodes.Functions.FunctionDefinitionNode>(def.Methods.Length);
            foreach (var md in def.Methods)
                methods.Add(ReconstructClassMethod(md, s, e));

            var node = new Parser.Nodes.Classes.ClassDefinitionNode(
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, def.Name, s, e),
                def.IsPublic, def.IsAbstract, /*isStatic*/ false,
                def.BaseType,
                new System.Collections.Generic.List<Types.TypeDescriptor>(def.Interfaces),
                new System.Collections.Generic.List<Types.TypeDescriptor>(def.Traits),
                fields, methods,
                ReconstructOperators(def.Operators, s, e),
                new System.Collections.Generic.List<string>(def.Generics),
                ReconstructWheres(def.Wheres, s, e),
                ReconstructProperties(def.Properties, s, e),
                ReconstructEvents(def.Events, s, e));
            if (def.Annotations.Length > 0)
                node.Annotations = new System.Collections.Generic.List<Parser.Nodes.Annotations.AnnotationApplicationNode>(def.Annotations);

            var task = Visitors.Classes.ClassDefinitionNodeVisitor.Apply(node, ctx, interpreter);
            var result = task.IsCompleted ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (result.Error != null) throw new RaUserError(result.Error);
            return result.Value!;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineTrait(IR.Defs.TraitDef def, Context ctx, VmFrame f, int pc, IInterpreter interpreter)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);

            var fields = new System.Collections.Generic.List<Parser.Nodes.Structs.StructFieldDefinitionNode>(def.Fields.Length);
            foreach (var fd in def.Fields)
            {
                AstNode? defNode = null;
                if (fd.DefaultConst != null)
                    defNode = new Parser.Nodes.Primitives.NumberNode(
                        new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e))
                    { CachedValue = fd.DefaultConst };
                fields.Add(new Parser.Nodes.Structs.StructFieldDefinitionNode(
                    fd.IsPublic,
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, fd.Name, s, e),
                    fd.FieldType, defNode, fd.IsStatic, fd.IsAbstract, fd.IsOverride,
                    (Parser.Nodes.Variables.VariableDeclarationType)fd.DeclKind));
            }

            var methods = new System.Collections.Generic.List<Parser.Nodes.Traits.TraitMethodDefinitionNode>(def.Methods.Length);
            foreach (var md in def.Methods)
            {
                var argToks = new System.Collections.Generic.List<Lexer.Tokens.Token>(md.ArgNames.Length);
                foreach (var an in md.ArgNames)
                    argToks.Add(new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, an, s, e));
                var argTypes = new System.Collections.Generic.List<Types.TypeDescriptor?>(md.ArgTypes);
                var refParams = new System.Collections.Generic.List<bool>(md.IsRefParams);
                var paramDefaults = new System.Collections.Generic.List<AstNode?>(new AstNode?[md.ArgNames.Length]);
                Lexer.Tokens.Token? varArgTok = md.VarArgName == null ? null
                    : new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, md.VarArgName, s, e);
                // Provided methods get a stub body + the precompiled RaFunction;
                // abstract/required methods keep a null body.
                AstNode? bodyNode = md.Body != null ? new Parser.Nodes.Operations.PassNode(s, e) : null;

                var mnode = new Parser.Nodes.Traits.TraitMethodDefinitionNode(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, md.Name, s, e),
                    argToks, argTypes, refParams, paramDefaults,
                    md.HasVarArgs, varArgTok, md.VarArgType, md.ReturnType,
                    bodyNode, md.ShouldAutoReturn, md.IsAbstract);
                if (md.Body != null)
                {
                    mnode.CompiledBody = md.Body;
                    mnode.IrCompileTried = true;
                }
                mnode.FrameId = md.FrameId;
                mnode.IsAsync = md.IsAsync;
                mnode.IsAsyncStream = md.IsAsyncStream;
                methods.Add(mnode);
            }

            var node = new Parser.Nodes.Traits.TraitDefinitionNode(
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, def.Name, s, e),
                def.IsPublic, methods, fields,
                new System.Collections.Generic.List<string>(def.Generics),
                new System.Collections.Generic.List<Parser.Nodes.Special.WhereConstraintNode>(),
                ReconstructProperties(def.Properties, s, e),
                ReconstructEvents(def.Events, s, e));
            if (def.Annotations.Length > 0)
                node.Annotations = new System.Collections.Generic.List<Parser.Nodes.Annotations.AnnotationApplicationNode>(def.Annotations);

            var result = Visitors.Traits.TraitDefinitionNodeVisitor.Apply(node, ctx, interpreter);
            if (result.Error != null) throw new RaUserError(result.Error);
            return result.Value!;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineExtension(IR.Defs.ExtensionDef def, Context ctx, VmFrame f, int pc)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);
            var methods = new System.Collections.Generic.List<Parser.Nodes.Functions.FunctionDefinitionNode>(def.Methods.Length);
            foreach (var md in def.Methods)
                methods.Add(ReconstructClassMethod(md, s, e));

            // Extension fields: rebuild a StructFieldDefinitionNode (const default
            // → NumberNode carrying CachedValue, byte-identical to the visitor's
            // field-init eval) wrapped in an ExtensionFieldDeclaration. A NON-CONST or
            // LAZY default lowered to a thunk needs a NON-NULL stub DefaultValueNode (so
            // the descriptor's DefaultValueNode is set → first-access enters the default
            // branch) — a PassNode; the thunk runs instead (DefaultCompiledBody, wired
            // below). isLazy flows from the def so the runtime re-entrancy guard fires.
            var fields = new System.Collections.Generic.List<Parser.Nodes.Classes.ExtensionFieldDeclaration>(def.Fields.Length);
            foreach (var fd in def.Fields)
            {
                AstNode? defNode = null;
                if (fd.CompiledDefault != null)
                    defNode = new Parser.Nodes.Operations.PassNode(s, e);
                else if (fd.DefaultConst != null)
                    defNode = new Parser.Nodes.Primitives.NumberNode(
                        new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e))
                    { CachedValue = fd.DefaultConst };
                var fieldNode = new Parser.Nodes.Structs.StructFieldDefinitionNode(
                    fd.IsPublic,
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, fd.Name, s, e),
                    fd.FieldType, defNode,
                    fd.IsStaticField, /*isAbstract*/ false, /*isOverride*/ false,
                    (Parser.Nodes.Variables.VariableDeclarationType)fd.DeclKind);
                if (fd.CompiledDefault != null)
                {
                    fieldNode.DefaultCompiledBody = fd.CompiledDefault;
                    fieldNode.DefaultIrCompileTried = true;
                }
                fields.Add(new Parser.Nodes.Classes.ExtensionFieldDeclaration(fieldNode, fd.IsStaticField, fd.IsLazy));
            }

            // Extension indexers: re-derive the (method, is-setter) tuples pointing
            // at the SAME reconstructed method objects (by index into the methods
            // list) so the visitor's reference-identity filter excludes them from
            // the regular method bucket, exactly as the parser-produced list does.
            var indexers = new System.Collections.Generic.List<(Parser.Nodes.Functions.FunctionDefinitionNode, bool)>(def.Indexers.Length);
            foreach (var ix in def.Indexers)
                indexers.Add((methods[ix.MethodIndex], ix.IsSetter));

            var node = new Parser.Nodes.Classes.ExtensionDefinitionNode(
                def.TargetType, def.IsPublic, methods,
                ReconstructProperties(def.Properties, s, e),
                ReconstructOperators(def.Operators, s, e),
                ReconstructEvents(def.Events, s, e),
                indexers, fields, def.IsSealed);
            if (def.Annotations.Length > 0)
                node.Annotations = new System.Collections.Generic.List<Parser.Nodes.Annotations.AnnotationApplicationNode>(def.Annotations);

            var result = Visitors.Extensions.ExtensionDefinitionNodeVisitor.Apply(node, ctx);
            if (result.Error != null) throw new RaUserError(result.Error);
            return result.Value!;
        }

        // L5e: reconstruct the InterfaceDefinitionNode (signature nodes + field
        // nodes + contract property/event nodes) from a flat InterfaceDef and run
        // the SAME visitor Apply → byte-identical registration + field/method
        // conformance metadata. Interface methods are SIGNATURES only (no bodies)
        // — nothing to precompile; fields carry no defaults (defNode stays null);
        // contract properties/events are abstract/protocol members (no bodies).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineInterface(IR.Defs.InterfaceDef def, Context ctx, VmFrame f, int pc, IInterpreter interpreter)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);

            var fields = new System.Collections.Generic.List<Parser.Nodes.Structs.StructFieldDefinitionNode>(def.Fields.Length);
            foreach (var fd in def.Fields)
            {
                fields.Add(new Parser.Nodes.Structs.StructFieldDefinitionNode(
                    fd.IsPublic,
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, fd.Name, s, e),
                    fd.FieldType, /*default*/ null, fd.IsStatic, fd.IsAbstract, fd.IsOverride,
                    (Parser.Nodes.Variables.VariableDeclarationType)fd.DeclKind));
            }

            var methods = new System.Collections.Generic.List<Parser.Nodes.Interfaces.InterfaceMethodSignatureNode>(def.Methods.Length);
            foreach (var md in def.Methods)
            {
                var argToks = new System.Collections.Generic.List<Lexer.Tokens.Token>(md.ArgNames.Length);
                foreach (var an in md.ArgNames)
                    argToks.Add(new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, an, s, e));
                var argTypes = new System.Collections.Generic.List<Types.TypeDescriptor?>(md.ArgTypes);
                methods.Add(new Parser.Nodes.Interfaces.InterfaceMethodSignatureNode(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, md.Name, s, e),
                    argToks, argTypes, md.ReturnType));
            }

            var node = new Parser.Nodes.Interfaces.InterfaceDefinitionNode(
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, def.Name, s, e),
                def.IsPublic, methods, fields,
                new System.Collections.Generic.List<string>(def.Generics),
                /*whereConstraints*/ null,
                ReconstructProperties(def.Properties, s, e),
                ReconstructEvents(def.Events, s, e));
            if (def.Annotations.Length > 0)
                node.Annotations = new System.Collections.Generic.List<Parser.Nodes.Annotations.AnnotationApplicationNode>(def.Annotations);

            var result = Visitors.Interfaces.InterfaceDefinitionNodeVisitor.Apply(node, ctx, interpreter);
            if (result.Error != null) throw new RaUserError(result.Error);
            return result.Value!;
        }

        // L5e: reconstruct the AnnotationDefinitionNode (params with const-default
        // stubs) from a flat AnnotationDef and run the SAME visitor Apply →
        // byte-identical AnnotationTypeValue registration. First sub-stage: no
        // meta-annotations (the reconstructed node has none, so the visitor's
        // meta-annotation loop + AnnotationProcessor.Process are no-ops).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineAnnotation(IR.Defs.AnnotationDef def, Context ctx, VmFrame f, int pc, IInterpreter interpreter)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);

            var ps = new System.Collections.Generic.List<Parser.Nodes.Annotations.AnnotationParameterNode>(def.Parameters.Length);
            foreach (var pd in def.Parameters)
            {
                AstNode? defNode = null;
                if (pd.DefaultConst != null)
                    defNode = new Parser.Nodes.Primitives.NumberNode(
                        new Lexer.Tokens.Token(Lexer.Tokens.TokenType.INT, "0", s, e))
                    { CachedValue = pd.DefaultConst };
                ps.Add(new Parser.Nodes.Annotations.AnnotationParameterNode(
                    new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, pd.Name, s, e),
                    pd.DeclaredType, defNode, pd.IsVarArgs));
            }

            var node = new Parser.Nodes.Annotations.AnnotationDefinitionNode(
                new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, def.Name, s, e),
                def.IsPublic, ps);
            if (def.Annotations.Length > 0)
                node.Annotations = new System.Collections.Generic.List<Parser.Nodes.Annotations.AnnotationApplicationNode>(def.Annotations);

            var result = Visitors.Annotations.AnnotationDefinitionNodeVisitor.Apply(node, ctx, interpreter);
            if (result.Error != null) throw new RaUserError(result.Error);
            return result.Value!;
        }

        // L6: reconstruct the ModuleSpecifier + the matching ImportNode from a
        // flat ImportDef and run the SAME ImportNodeVisitor.Apply →
        // ModuleManager.Load resolution + symbol/alias binding is byte-identical.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineImport(IR.Defs.ImportDef def, Context ctx, VmFrame f, int pc, IInterpreter interpreter)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);

            var spec = def.SpecIsDotted
                ? RaLanguage.Interpreter.Modules.ModuleSpecifier.FromDotted(def.Segments, def.IsWildcard)
                : RaLanguage.Interpreter.Modules.ModuleSpecifier.FromStringLiteral(def.RawPath ?? "");

            Parser.Nodes.Imports.ImportNode node;
            switch (def.ImportKind)
            {
                case IR.Defs.ImportDefKind.Selective:
                {
                    var toks = new System.Collections.Generic.List<Lexer.Tokens.Token>(def.SymbolNames.Length);
                    foreach (var nm in def.SymbolNames)
                        toks.Add(new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, nm, s, e));
                    node = new Parser.Nodes.Imports.ImportSelectiveNode(spec, toks, s, e);
                    break;
                }
                case IR.Defs.ImportDefKind.Alias:
                {
                    var aliasTok = new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, def.Alias ?? "", s, e);
                    node = new Parser.Nodes.Imports.ImportAliasNode(spec, aliasTok, s, e);
                    break;
                }
                default:
                    node = new Parser.Nodes.Imports.ImportAllNode(spec, s, e);
                    break;
            }

            var result = Visitors.Imports.ImportNodeVisitor.Apply(node, ctx, interpreter);
            if (result.Error != null) throw new RaUserError(result.Error);
            return result.Value!;
        }

        // L6: reconstruct the NamespaceDeclarationNode (segments only; the body
        // is a stub — the visitor's precompiled-body path ignores node.Body) and
        // run the SAME NamespaceDeclarationNodeVisitor.Apply passing the
        // precompiled body RaFunctions → namespace opening / scope-chain /
        // closure-freezing is byte-identical. Apply is async but completes
        // synchronously for the definition bodies (no real `await`), so the
        // blocking unwrap never actually blocks (mirrors DefineClass).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineNamespace(IR.Defs.NamespaceDef def, Context ctx, VmFrame f, int pc, IInterpreter interpreter)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);

            var segToks = new System.Collections.Generic.List<Lexer.Tokens.Token>(def.Segments.Length);
            foreach (var seg in def.Segments)
                segToks.Add(new Lexer.Tokens.Token(Lexer.Tokens.TokenType.IDENTIFIER, seg, s, e));

            var body = new Parser.Nodes.Operations.PassNode(s, e);
            var node = new Parser.Nodes.Namespaces.NamespaceDeclarationNode(segToks, body, def.IsFileScoped, s, e);

            var task = Visitors.Namespaces.NamespaceDeclarationNodeVisitor.Apply(node, ctx, interpreter, def.Bodies);
            var result = task.IsCompleted ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (result.Error != null) throw new RaUserError(result.Error);
            return result.Value!;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineUsing(IR.Defs.UsingDef def, Context ctx, VmFrame f, int pc)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);
            var (value, err) = Runtime.UsingNamespaceOps.Apply(def.Segments, def.Alias, ctx, s, e);
            if (err != null) throw new RaUserError(err);
            return value!;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineDelegate(IR.Defs.DelegateDef def, Context ctx, VmFrame f, int pc)
        {
            var (s, e) = ResolveSpan(f, pc, ctx);
            var (value, err) = Runtime.DelegateDefOps.Register(
                def.Name, def.Signature,
                new System.Collections.Generic.List<string>(def.Generics),
                new System.Collections.Generic.List<Parser.Nodes.Special.WhereConstraintNode>(),
                def.IsPublic, ctx, s, e);
            if (err != null) throw new RaUserError(err);
            return value!;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue DefineEnum(IR.Defs.EnumDef def, Context ctx, VmFrame f, int pc, IInterpreter interpreter)
        {
            // Collision check mirrors the visitor (it runs BEFORE building the
            // variants; the lowered variants have no side effects so order is
            // immaterial here).
            if (ctx.SymbolTable.Get(def.Name) != null)
            {
                var (cs, ce) = ResolveSpan(f, pc, ctx);
                throw new RaUserError(new Errors.Types.RuntimeError(cs, ce, $"'{def.Name}' is already defined", ctx));
            }

            var variants = new System.Collections.Generic.List<Values.Primitives.EnumVariantInfo>(def.Variants.Length);
            for (int i = 0; i < def.Variants.Length; i++)
            {
                var v = def.Variants[i];
                System.Collections.Generic.IReadOnlyList<Types.TypeDescriptor>? payloads =
                    v.PayloadTypes.Length == 0 ? null : v.PayloadTypes;
                variants.Add(new Values.Primitives.EnumVariantInfo(v.Name, v.Ordinal, v.Value, payloads));
            }

            var (s, e) = ResolveSpan(f, pc, ctx);
            var enumTypeValue = Runtime.EnumDefOps.BuildAndRegister(
                def.Name, variants,
                new System.Collections.Generic.List<string>(def.Generics),
                new System.Collections.Generic.List<Parser.Nodes.Special.WhereConstraintNode>(),
                ctx, s, e);

            // Node-level annotations on the enum: process them exactly as
            // EnumDefinitionNodeVisitor does after BuildAndRegister (DefineEnum
            // does not reconstruct an AST node, so there is no node.Annotations to
            // reattach — run AnnotationProcessor.Process directly for parity).
            if (def.Annotations.Length > 0)
            {
                var target = new Runtime.Annotations.MetadataTarget(
                    Runtime.Annotations.AnnotationTargetKind.Enum, null, def.Name);
                var annErr = Runtime.Annotations.AnnotationProcessor.Process(def.Annotations, target, ctx, interpreter);
                if (annErr != null) throw new RaUserError(annErr);
            }

            return enumTypeValue;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int FusedCmpBranchDelta(VmFrame f, LocalsView locals, uint instr, Opcode op)
        {
            byte bSlot = Encoding.A(instr);
            byte cSlot = Encoding.B(instr);
            sbyte off = (sbyte)Encoding.C(instr);
            bool cmp;
            if (TryReadAsLong(f, locals, bSlot, out long lv) && TryReadAsLong(f, locals, cSlot, out long rv))
            {
                switch (op)
                {
                    case Opcode.JmpNotLtII: cmp = lv <  rv; break;
                    case Opcode.JmpNotLeII: cmp = lv <= rv; break;
                    case Opcode.JmpNotGtII: cmp = lv >  rv; break;
                    case Opcode.JmpNotGeII: cmp = lv >= rv; break;
                    case Opcode.JmpNotEqII: cmp = lv == rv; break;
                    default:                cmp = lv != rv; break; // JmpNotNeII
                }
            }
            else
            {
                // Deopt: a slot is not Int64-tagged (rare — fusion only
                // targets typed-promoted cmpII, but runtime tag drift is
                // possible via a mixed-type chain). Materialise boxed
                // values and run the virtual comparison.
                EnsureBoxed(f, locals, bSlot);
                EnsureBoxed(f, locals, cSlot);
                var lb = locals[bSlot] ?? NullValue.Null;
                var rb = locals[cSlot] ?? NullValue.Null;
                ValueResult vr = op switch
                {
                    Opcode.JmpNotLtII => lb.GetComparisonLt(rb),
                    Opcode.JmpNotLeII => lb.GetComparisonLte(rb),
                    Opcode.JmpNotGtII => lb.GetComparisonGt(rb),
                    Opcode.JmpNotGeII => lb.GetComparisonGte(rb),
                    Opcode.JmpNotEqII => lb.GetComparisonEq(rb),
                    _                 => lb.GetComparisonNe(rb),
                };
                if (vr.Error != null) throw new RaUserError(vr.Error);
                cmp = vr.Value != null && vr.Value.IsTrue();
            }
            // JmpIfNot semantics: branch when the condition is FALSE.
            return cmp ? 0 : off;
        }

        // M73 inverse-direction helper for the Bool tag. Returns the
        // boolean payload of the slot — either the `Bool`-tagged
        // bit (`Bits & 1`) or the `IsTrue()` virtual of the boxed
        // RuntimeValue. Mirrors `TryReadAsLong` / `TryReadAsDouble`
        // for the BB dispatch family.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool TryReadAsBool(VmFrame f, LocalsView locals, int slot, out bool value)
        {
            if (slot < f.Slots.Length && f.Slots[slot].Tag == ValueSlotTag.Bool)
            {
                value = (f.Slots[slot].Bits & 1) != 0;
                return true;
            }
            var v = locals[slot];
            if (v is BooleanValue bv) { value = bv.Value; return true; }
            // Fall back to RuntimeValue.IsTrue() for non-Boolean
            // truthiness — matches `if`/`while`/`and`/`or` semantics
            // where any value participates in the truth test.
            if (v != null) { value = v.IsTrue(); return true; }
            value = false;
            return false;
        }

        // M72 inverse-direction helper for the Float64 tag. If the
        // slot already carries a `Float64` payload, return it
        // directly. Otherwise try to coerce the boxed value —
        // accepts `DoubleValue`, `FloatValue`, and integer-valued
        // `NumberValue` so the FF dispatch path tolerates mixed
        // int / float chains.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool TryReadAsDouble(VmFrame f, LocalsView locals, int slot, out double value)
        {
            if (slot < f.Slots.Length && f.Slots[slot].Tag == ValueSlotTag.Float64)
            {
                value = System.BitConverter.Int64BitsToDouble(f.Slots[slot].Bits);
                return true;
            }
            var v = locals[slot];
            if (v is DoubleValue dv) { value = dv.Value; return true; }
            if (v is FloatValue fv) { value = fv.Value; return true; }
            if (v is NumberValue nv)
            {
                var bn = nv.Value;
                if (bn.Scale.IsZero)
                {
                    // Convert int-valued NumberValue to double if it fits.
                    if (TryGetInt64(nv, out long lv)) { value = lv; return true; }
                }
                // Fractional NumberValue: fall through to double via
                // BigInteger / scale (rare path; conservative skip
                // for now — caller deopts to boxed Binary).
            }
            value = 0;
            return false;
        }

        // M92 cold overflow path for the inlined AddII / SubII / MulII hot
        // cases. Reached only when the int64 result overflows 64 bits, so the
        // BigInteger arithmetic + its locals stay OUT of the dispatch-loop
        // MoveNext frame (the whole point of [NoInlining] here — keeps the
        // recursion frame budget intact). Writes the exact BigNumber result
        // as a boxed Ref slot, matching the pre-inline ExecuteUnboxedII
        // semantics byte-for-byte.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void BoxIIOverflow(VmFrame f, int a, long lv, long rv, Opcode op)
        {
            var bl = new System.Numerics.BigInteger(lv);
            var br = new System.Numerics.BigInteger(rv);
            System.Numerics.BigInteger r = op switch
            {
                Opcode.Add => bl + br,
                Opcode.Sub => bl - br,
                _          => bl * br, // Mul
            };
            ref var sa = ref f.Slots[a];
            sa.Tag = ValueSlotTag.Ref;
            sa.Ref = new NumberValue(new BigNumber(r, System.Numerics.BigInteger.Zero));
        }

        // M66 lazy box-on-read. When a boxed opcode reads `locals[slot]`
        // but the slot's current canonical value lives in
        // `f.LongLocals[slot]` (LongValid[slot] == true), materialise
        // a NumberValue boxed copy on the fly and clear the tag. Lets
        // unboxed and boxed opcodes interleave freely without forcing
        // the IR rewriter to insert explicit BoxI bridges at every
        // boundary. Cost is one branch + one allocation per read of a
        // tagged slot; subsequent reads of the now-boxed slot are
        // free.
        //
        // Called by the boxed opcode handlers via `EnsureBoxed(f,
        // locals, slot)` before reading `locals[slot]`. Currently
        // wired only where strictly needed; broader integration is a
        // follow-up so we can land the M66 step-1 infrastructure
        // without changing every existing handler.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void EnsureBoxed(VmFrame f, LocalsView locals, int slot)
        {
            if (slot >= f.Slots.Length) return;
            ref var s = ref f.Slots[slot];
            switch (s.Tag)
            {
                case ValueSlotTag.Int64:
                    locals[slot] = NumberValue.OfInt64(s.Bits);
                    s.Tag = ValueSlotTag.Ref;
                    break;
                case ValueSlotTag.Float64:
                    locals[slot] = DoubleValue.OfDouble(System.BitConverter.Int64BitsToDouble(s.Bits));
                    s.Tag = ValueSlotTag.Ref;
                    break;
                case ValueSlotTag.Bool:
                    locals[slot] = BooleanValue.Of((s.Bits & 1) != 0);
                    s.Tag = ValueSlotTag.Ref;
                    break;
                case ValueSlotTag.Null:
                    locals[slot] = NullValue.Null;
                    s.Tag = ValueSlotTag.Ref;
                    break;
                // Ref: locals[slot] is already canonical.
            }
        }

        // M66 tagged-union dispatcher. Extracted from the main loop so
        // the loop's C# stack frame stays compact — the dispatcher
        // method allocates its own frame on demand. NoInlining is
        // explicit to prevent .NET 10 from re-merging this back into
        // the caller and reinflating the dispatch-loop frame.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ExecuteUnboxedII(VmFrame f, LocalsView locals, uint instr)
        {
            // M75 finalisation: hoist the destination slot reference once
            // per case body. Each `f.Slots[a].X = ...` would otherwise
            // recompute the array element address (`&Slots[0] + a*24`)
            // for every field touch — JIT sometimes elides the duplicate
            // load but explicit `ref var sa = ref f.Slots[a]` makes the
            // single-base-register pattern visible and matches the
            // pinned 24-byte ValueSlot layout (LayoutKind.Explicit, Tag@0
            // / Bits@8 / Ref@16). All three field writes share `sa` so
            // the JIT emits `mov [base+a*24+offset]` triples with a
            // single base register held live across the case.
            var op = (Opcode)(instr & 0xFF);
            byte a = Encoding.A(instr);
            switch (op)
            {
                case Opcode.LoadIntS64:
                {
                    long imm = Encoding.SImm16(instr);
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = imm;
                    sa.Ref = null;
                    return;
                }
                case Opcode.UnboxI:
                {
                    byte b = Encoding.B(instr);
                    if (TryReadAsLong(f, locals, b, out long lv))
                    {
                        ref var sa = ref f.Slots[a];
                        sa.Tag = ValueSlotTag.Int64;
                        sa.Bits = lv;
                        sa.Ref = null;
                    }
                    else
                    {
                        var src = locals[b];
                        ref var sa = ref f.Slots[a];
                        sa.Tag = ValueSlotTag.Ref;
                        sa.Ref = src;
                    }
                    return;
                }
                case Opcode.BoxI:
                {
                    byte b = Encoding.B(instr);
                    long lv = f.Slots[b].Bits;
                    var boxed = NumberValue.OfInt64(lv);
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Ref;
                    sa.Ref = boxed;
                    return;
                }
                case Opcode.AddII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryII(f, locals, a, b, c, Opcode.Add); return; }
                    long sum = lv + rv;
                    ref var sa = ref f.Slots[a];
                    if (((lv ^ sum) & (rv ^ sum)) < 0)
                    {
                        sa.Tag = ValueSlotTag.Ref;
                        sa.Ref = new NumberValue(new BigNumber(
                            new System.Numerics.BigInteger(lv) + new System.Numerics.BigInteger(rv),
                            System.Numerics.BigInteger.Zero));
                    }
                    else
                    {
                        sa.Tag = ValueSlotTag.Int64;
                        sa.Bits = sum;
                        sa.Ref = null;
                    }
                    return;
                }
                case Opcode.SubII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryII(f, locals, a, b, c, Opcode.Sub); return; }
                    long diff = lv - rv;
                    ref var sa = ref f.Slots[a];
                    if (((lv ^ rv) & (lv ^ diff)) < 0)
                    {
                        sa.Tag = ValueSlotTag.Ref;
                        sa.Ref = new NumberValue(new BigNumber(
                            new System.Numerics.BigInteger(lv) - new System.Numerics.BigInteger(rv),
                            System.Numerics.BigInteger.Zero));
                    }
                    else
                    {
                        sa.Tag = ValueSlotTag.Int64;
                        sa.Bits = diff;
                        sa.Ref = null;
                    }
                    return;
                }
                case Opcode.MulII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryII(f, locals, a, b, c, Opcode.Mul); return; }
                    ref var sa = ref f.Slots[a];
                    // Full int64 multiply with exact overflow detection via the
                    // 128-bit product: the result fits a signed 64-bit slot iff
                    // the high half is the sign-extension of the low half. One
                    // `imul` on x64 — cheaper than the former four-compare
                    // int32-range gate AND keeps every int64-representable
                    // product (e.g. 2e16 * 2) on the unboxed fast path instead
                    // of spilling to a per-op BigNumber allocation.
                    {
                        long hi = System.Math.BigMul(lv, rv, out long lo);
                        if (hi == (lo >> 63))
                        {
                            sa.Tag = ValueSlotTag.Int64;
                            sa.Bits = lo;
                            sa.Ref = null;
                        }
                        else
                        {
                            sa.Tag = ValueSlotTag.Ref;
                            sa.Ref = new NumberValue(new BigNumber(
                                new System.Numerics.BigInteger(lv) * new System.Numerics.BigInteger(rv),
                                System.Numerics.BigInteger.Zero));
                        }
                    }
                    return;
                }
                case Opcode.LtII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryII(f, locals, a, b, c, Opcode.Lt); return; }
                    bool r = lv < rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
                case Opcode.LeII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryII(f, locals, a, b, c, Opcode.Le); return; }
                    bool r = lv <= rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
                case Opcode.GtII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryII(f, locals, a, b, c, Opcode.Gt); return; }
                    bool r = lv > rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
                case Opcode.GeII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryII(f, locals, a, b, c, Opcode.Ge); return; }
                    bool r = lv >= rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
                case Opcode.EqII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryII(f, locals, a, b, c, Opcode.Eq); return; }
                    bool r = lv == rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
                case Opcode.NeII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryII(f, locals, a, b, c, Opcode.Ne); return; }
                    bool r = lv != rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
            }
        }

        // M66.2 deopt for II opcodes when operands are non-int. Routes
        // to the boxed `Binary` path so observable semantics are
        // unchanged from the pre-M66 dispatch — a slot that the IR
        // rewriter optimistically marked as int but the runtime
        // observes a non-Number value (rare; would mean the type
        // hint lattice or SCCP was wrong) lands a boxed RuntimeValue
        // result in `locals[a]` exactly as the original `Add`/`Sub`/
        // `Mul`/`Lt`/`Le` would have.
        private static void DeoptBinaryII(VmFrame f, LocalsView locals, byte a, byte b, byte c, Opcode boxedOp)
        {
            uint synth = Encoding.Pack3(boxedOp, a, b, c);
            BinOp bop = boxedOp switch
            {
                Opcode.Add => BinOp.Add,
                Opcode.Sub => BinOp.Sub,
                Opcode.Mul => BinOp.Mul,
                Opcode.Lt  => BinOp.Lt,
                Opcode.Le  => BinOp.Le,
                Opcode.Gt  => BinOp.Gt,
                Opcode.Ge  => BinOp.Ge,
                Opcode.Eq  => BinOp.Eq,
                Opcode.Ne  => BinOp.Ne,
                _          => BinOp.Add,
            };
            EnsureBoxed(f, locals, b);
            EnsureBoxed(f, locals, c);
            var r = Binary(locals, synth, bop);
            if (r.Error != null) throw new RaUserError(r.Error);
            if (a < f.Slots.Length) f.Slots[a].Tag = ValueSlotTag.Ref;
        }

        // M72 FF dispatcher. Same off-stack discipline as
        // `ExecuteUnboxedII`; the `[NoInlining]` attribute keeps the
        // main dispatch-loop frame compact (sensitive for
        // test_deep_recursion at depth 2000+).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ExecuteUnboxedFF(VmFrame f, LocalsView locals, uint instr)
        {
            // M75 finalisation: hoist destination slot ref once per case
            // (see ExecuteUnboxedII for rationale — pinned ValueSlot
            // layout + ref-into-array idiom collapses field stores to
            // shared-base-register addressing).
            var op = (Opcode)(instr & 0xFF);
            byte a = Encoding.A(instr);
            switch (op)
            {
                case Opcode.UnboxF:
                {
                    byte b = Encoding.B(instr);
                    if (TryReadAsDouble(f, locals, b, out double dv))
                    {
                        ref var sa = ref f.Slots[a];
                        sa.Tag = ValueSlotTag.Float64;
                        sa.Bits = System.BitConverter.DoubleToInt64Bits(dv);
                        sa.Ref = null;
                    }
                    else
                    {
                        var src = locals[b];
                        ref var sa = ref f.Slots[a];
                        sa.Tag = ValueSlotTag.Ref;
                        sa.Ref = src;
                    }
                    return;
                }
                case Opcode.BoxF:
                {
                    byte b = Encoding.B(instr);
                    double dv = System.BitConverter.Int64BitsToDouble(f.Slots[b].Bits);
                    var boxed = DoubleValue.OfDouble(dv);
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Ref;
                    sa.Ref = boxed;
                    return;
                }
                case Opcode.AddFF:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsDouble(f, locals, b, out double lv) || !TryReadAsDouble(f, locals, c, out double rv))
                    { DeoptBinaryFF(f, locals, a, b, c, Opcode.Add); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Float64;
                    sa.Bits = System.BitConverter.DoubleToInt64Bits(lv + rv);
                    sa.Ref = null;
                    return;
                }
                case Opcode.SubFF:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsDouble(f, locals, b, out double lv) || !TryReadAsDouble(f, locals, c, out double rv))
                    { DeoptBinaryFF(f, locals, a, b, c, Opcode.Sub); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Float64;
                    sa.Bits = System.BitConverter.DoubleToInt64Bits(lv - rv);
                    sa.Ref = null;
                    return;
                }
                case Opcode.MulFF:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsDouble(f, locals, b, out double lv) || !TryReadAsDouble(f, locals, c, out double rv))
                    { DeoptBinaryFF(f, locals, a, b, c, Opcode.Mul); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Float64;
                    sa.Bits = System.BitConverter.DoubleToInt64Bits(lv * rv);
                    sa.Ref = null;
                    return;
                }
                case Opcode.DivFF:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsDouble(f, locals, b, out double lv) || !TryReadAsDouble(f, locals, c, out double rv))
                    { DeoptBinaryFF(f, locals, a, b, c, Opcode.Div); return; }
                    // IEEE-754 division: 0.0 / 0.0 = NaN, x / 0.0 = ±Inf.
                    // No deopt needed — matches DoubleValue semantics.
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Float64;
                    sa.Bits = System.BitConverter.DoubleToInt64Bits(lv / rv);
                    sa.Ref = null;
                    return;
                }
                case Opcode.LtFF:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsDouble(f, locals, b, out double lv) || !TryReadAsDouble(f, locals, c, out double rv))
                    { DeoptBinaryFF(f, locals, a, b, c, Opcode.Lt); return; }
                    bool r = lv < rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
                case Opcode.LeFF:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsDouble(f, locals, b, out double lv) || !TryReadAsDouble(f, locals, c, out double rv))
                    { DeoptBinaryFF(f, locals, a, b, c, Opcode.Le); return; }
                    bool r = lv <= rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
                case Opcode.GtFF:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsDouble(f, locals, b, out double lv) || !TryReadAsDouble(f, locals, c, out double rv))
                    { DeoptBinaryFF(f, locals, a, b, c, Opcode.Gt); return; }
                    bool r = lv > rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
                case Opcode.GeFF:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsDouble(f, locals, b, out double lv) || !TryReadAsDouble(f, locals, c, out double rv))
                    { DeoptBinaryFF(f, locals, a, b, c, Opcode.Ge); return; }
                    bool r = lv >= rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = r ? 1 : 0;
                    sa.Ref = BooleanValue.Of(r);
                    return;
                }
            }
        }

        // M72 deopt for FF opcodes when operands cannot be coerced
        // to double. Routes through the boxed `Binary` path so
        // observable semantics stay identical to the pre-rewrite
        // dispatch.
        private static void DeoptBinaryFF(VmFrame f, LocalsView locals, byte a, byte b, byte c, Opcode boxedOp)
        {
            uint synth = Encoding.Pack3(boxedOp, a, b, c);
            BinOp bop = boxedOp switch
            {
                Opcode.Add => BinOp.Add,
                Opcode.Sub => BinOp.Sub,
                Opcode.Mul => BinOp.Mul,
                Opcode.Div => BinOp.Div,
                Opcode.Lt  => BinOp.Lt,
                Opcode.Le  => BinOp.Le,
                Opcode.Gt  => BinOp.Gt,
                Opcode.Ge  => BinOp.Ge,
                _          => BinOp.Add,
            };
            EnsureBoxed(f, locals, b);
            EnsureBoxed(f, locals, c);
            var r = Binary(locals, synth, bop);
            if (r.Error != null) throw new RaUserError(r.Error);
            if (a < f.Slots.Length) f.Slots[a].Tag = ValueSlotTag.Ref;
        }

        // M73 BB dispatcher. Same off-stack discipline as the II /
        // FF families. Reads operands through `TryReadAsBool`,
        // writes the result as a `Bool`-tagged slot (Bits & 1).
        // `locals[a]` is cleared so any later boxed reader has to
        // go through `EnsureBoxed` to materialise a `BooleanValue`
        // — guaranteed by the dispatch-loop pre-clear's writer
        // bitmap excluding the BB family (their case bodies
        // assign the final tag).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ExecuteUnboxedBB(VmFrame f, LocalsView locals, uint instr)
        {
            var op = (Opcode)(instr & 0xFF);
            byte a = Encoding.A(instr);
            switch (op)
            {
                case Opcode.AndBB:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    bool result;
                    if (!TryReadAsBool(f, locals, b, out bool lv) || !TryReadAsBool(f, locals, c, out bool rv))
                    {
                        EnsureBoxed(f, locals, b);
                        EnsureBoxed(f, locals, c);
                        var lvb = locals[b]; var rvb = locals[c];
                        result = (lvb?.IsTrue() ?? false) && (rvb?.IsTrue() ?? false);
                    }
                    else result = lv && rv;
                    // M74 dual-rep: typed tag PLUS boxed mirror so
                    // backward-compat boxed readers (Move / Call
                    // arg passing / generic ops) see a real
                    // BooleanValue instead of a null `Ref`.
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = result ? 1 : 0;
                    sa.Ref = BooleanValue.Of(result);
                    return;
                }
                case Opcode.OrBB:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    bool result;
                    if (!TryReadAsBool(f, locals, b, out bool lv) || !TryReadAsBool(f, locals, c, out bool rv))
                    {
                        EnsureBoxed(f, locals, b);
                        EnsureBoxed(f, locals, c);
                        var lvb = locals[b]; var rvb = locals[c];
                        result = (lvb?.IsTrue() ?? false) || (rvb?.IsTrue() ?? false);
                    }
                    else result = lv || rv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = result ? 1 : 0;
                    sa.Ref = BooleanValue.Of(result);
                    return;
                }
                case Opcode.NotB:
                {
                    byte b = Encoding.B(instr);
                    bool result;
                    if (!TryReadAsBool(f, locals, b, out bool bv))
                    {
                        EnsureBoxed(f, locals, b);
                        var src = locals[b];
                        result = !(src?.IsTrue() ?? false);
                    }
                    else result = !bv;
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Bool;
                    sa.Bits = result ? 1 : 0;
                    sa.Ref = BooleanValue.Of(result);
                    return;
                }
            }
        }

        // M68 extended II/FF dispatcher. Handles Div / Mod /
        // bitwise / negate (int + float). Same `[NoInlining]`
        // discipline as ExecuteUnboxedII/FF/BB — the dispatch
        // loop's C# frame stays compact (depth-2000 recursion
        // tripwire). Each case deopts to the boxed `Binary` path
        // when operands cannot be coerced (preserving original
        // diagnostics for div-by-zero, etc.).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ExecuteUnboxedExtII(VmFrame f, LocalsView locals, uint instr)
        {
            // M75 finalisation: hoist destination slot ref once per case
            // (see ExecuteUnboxedII for rationale).
            var op = (Opcode)(instr & 0xFF);
            byte a = Encoding.A(instr);
            switch (op)
            {
                case Opcode.DivII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Div); return; }
                    // Division by zero + signed-overflow
                    // (long.MinValue / -1) both route to the
                    // boxed `Binary` path: it raises the proper
                    // RuntimeError with the call-site PC's
                    // diagnostic.
                    if (rv == 0 || (lv == long.MinValue && rv == -1))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Div); return; }
                    long q = lv / rv;
                    // Truncation toward zero leaves a remainder
                    // when (lv % rv) != 0. The BigNumber path
                    // would surface this as a non-zero Scale.
                    // For int-only chains we want exact integer
                    // division; if remainder, fall back to boxed.
                    if (lv - q * rv != 0)
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Div); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = q;
                    sa.Ref = null;
                    return;
                }
                case Opcode.ModII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Mod); return; }
                    if (rv == 0 || (lv == long.MinValue && rv == -1))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Mod); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = lv % rv;
                    sa.Ref = null;
                    return;
                }
                case Opcode.ShlII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Shl); return; }
                    // Mask shift count to [0, 63] — same semantics
                    // as C#'s `long << int`. Larger counts route
                    // through the boxed BigNumber path.
                    if (rv < 0 || rv >= 64)
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Shl); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = lv << (int)rv;
                    sa.Ref = null;
                    return;
                }
                case Opcode.ShrII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Shr); return; }
                    if (rv < 0 || rv >= 64)
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Shr); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = lv >> (int)rv;
                    sa.Ref = null;
                    return;
                }
                // Logical right shift on int64. The C# `>>` on `ulong` is the
                // zero-extending operation; we cast to `ulong`, shift, and
                // re-tag. Negative or oversized counts deopt to the boxed
                // `Ushr` path so the per-type semantics on a Number or
                // fixed-width type stay intact.
                case Opcode.UshrII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Ushr); return; }
                    if (rv < 0 || rv >= 64)
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Ushr); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = unchecked((long)((ulong)lv >> (int)rv));
                    sa.Ref = null;
                    return;
                }
                // Rotate-left on int64 — width = 64. BitOperations.RotateLeft
                // already masks the count modulo 64, but we keep the explicit
                // out-of-range deopt for parity with ShlII/ShrII (a count of
                // exactly 64 should NOT silently rotate back to the original
                // value at the typed layer; defer to the boxed `Rol` which
                // surfaces a uniform diagnostic).
                case Opcode.RolII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Rol); return; }
                    if (rv < 0 || rv >= 64)
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Rol); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = unchecked((long)System.Numerics.BitOperations.RotateLeft((ulong)lv, (int)rv));
                    sa.Ref = null;
                    return;
                }
                case Opcode.RorII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Ror); return; }
                    if (rv < 0 || rv >= 64)
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Ror); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = unchecked((long)System.Numerics.BitOperations.RotateRight((ulong)lv, (int)rv));
                    sa.Ref = null;
                    return;
                }
                case Opcode.BAndII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.BAnd); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = lv & rv;
                    sa.Ref = null;
                    return;
                }
                case Opcode.BOrII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.BOr); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = lv | rv;
                    sa.Ref = null;
                    return;
                }
                case Opcode.BXorII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv) || !TryReadAsLong(f, locals, c, out long rv))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.BXor); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = lv ^ rv;
                    sa.Ref = null;
                    return;
                }
                case Opcode.NegI:
                {
                    byte b = Encoding.B(instr);
                    if (!TryReadAsLong(f, locals, b, out long lv))
                    { DeoptUnaryExtII(f, locals, a, b, Opcode.Neg); return; }
                    // long.MinValue negation overflows — route to
                    // boxed BigNumber to preserve precise value.
                    if (lv == long.MinValue)
                    { DeoptUnaryExtII(f, locals, a, b, Opcode.Neg); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = -lv;
                    sa.Ref = null;
                    return;
                }
                case Opcode.NegF:
                {
                    byte b = Encoding.B(instr);
                    if (!TryReadAsDouble(f, locals, b, out double dv))
                    { DeoptUnaryExtII(f, locals, a, b, Opcode.Neg); return; }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Float64;
                    sa.Bits = System.BitConverter.DoubleToInt64Bits(-dv);
                    sa.Ref = null;
                    return;
                }
                // M80 — PowII (b^c with both Int64). Iterative
                // exponentiation by squaring. Negative exponent or
                // overflow during accumulation deopts to the boxed
                // `Pow` path so the BigNumber-precise result and the
                // original error site survive.
                case Opcode.PowII:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsLong(f, locals, b, out long baseV)
                        || !TryReadAsLong(f, locals, c, out long expV))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Pow); return; }
                    if (expV < 0)
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Pow); return; }
                    long result = 1;
                    long bb = baseV;
                    long ee = expV;
                    while (ee > 0)
                    {
                        if ((ee & 1) != 0)
                        {
                            // Overflow-checked multiply: if (long.MaxValue / |bb|) < |result|
                            // we'd overflow. Skip the checked() SEH cost
                            // with explicit predicate.
                            long prod = unchecked(result * bb);
                            if (bb != 0 && prod / bb != result)
                            { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Pow); return; }
                            result = prod;
                        }
                        ee >>= 1;
                        if (ee > 0)
                        {
                            long sq = unchecked(bb * bb);
                            if (bb != 0 && sq / bb != bb)
                            { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Pow); return; }
                            bb = sq;
                        }
                    }
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Int64;
                    sa.Bits = result;
                    sa.Ref = null;
                    return;
                }
                // M80 — PowFF (b^c with both Float64). IEEE-754
                // `Math.Pow` semantics — never throws (NaN / +-Inf
                // are valid results).
                case Opcode.PowFF:
                {
                    byte b = Encoding.B(instr);
                    byte c = Encoding.C(instr);
                    if (!TryReadAsDouble(f, locals, b, out double baseD)
                        || !TryReadAsDouble(f, locals, c, out double expD))
                    { DeoptBinaryExtII(f, locals, a, b, c, Opcode.Pow); return; }
                    double r = System.Math.Pow(baseD, expD);
                    ref var sa = ref f.Slots[a];
                    sa.Tag = ValueSlotTag.Float64;
                    sa.Bits = System.BitConverter.DoubleToInt64Bits(r);
                    sa.Ref = null;
                    return;
                }
            }
        }

        // M68 deopt for extended II/FF binary opcodes. Routes
        // through the boxed `Binary` so the original error
        // diagnostic (div-by-zero, overflow) surfaces at the
        // call-site PC.
        private static void DeoptBinaryExtII(VmFrame f, LocalsView locals, byte a, byte b, byte c, Opcode boxedOp)
        {
            uint synth = Encoding.Pack3(boxedOp, a, b, c);
            BinOp bop = boxedOp switch
            {
                Opcode.Div  => BinOp.Div,
                Opcode.Mod  => BinOp.Mod,
                Opcode.Shl  => BinOp.Shl,
                Opcode.Shr  => BinOp.Shr,
                Opcode.Ushr => BinOp.Ushr,
                Opcode.Rol  => BinOp.Rol,
                Opcode.Ror  => BinOp.Ror,
                Opcode.BAnd => BinOp.BAnd,
                Opcode.BOr  => BinOp.BOr,
                Opcode.BXor => BinOp.BXor,
                Opcode.Pow  => BinOp.Pow,
                _           => BinOp.Add,
            };
            EnsureBoxed(f, locals, b);
            EnsureBoxed(f, locals, c);
            var r = Binary(locals, synth, bop);
            if (r.Error != null) throw new RaUserError(r.Error);
            if (a < f.Slots.Length) f.Slots[a].Tag = ValueSlotTag.Ref;
        }

        // M68 unary deopt (NegI / NegF). Falls back to
        // `RuntimeValue.MultedBy(-1)` style via the boxed `Neg`
        // path — synthesises the canonical -1 constant and
        // invokes the virtual subtraction so all RuntimeValue
        // subclasses (int / long / float / double / BigNumber)
        // handle their own representation.
        private static void DeoptUnaryExtII(VmFrame f, LocalsView locals, byte a, byte b, Opcode boxedOp)
        {
            EnsureBoxed(f, locals, b);
            var v = locals[b];
            if (v == null) throw new RaUserError(MakeIcError(null, "VM: NegI src is null"));
            // Re-use the existing s_negOne constant.
            var (r, e) = v.MultedBy(s_negOne);
            if (e != null) throw new RaUserError(e);
            locals[a] = r;
            if (a < f.Slots.Length) f.Slots[a].Tag = ValueSlotTag.Ref;
        }

        // Binary-op dispatcher: looks up the operator by enum tag, invokes
        // the appropriate virtual on the left operand, writes the result to
        // locals[dst]. Returns the ValueResult so the caller can surface the
        // Error to the dispatch loop.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static ValueResult Binary(LocalsView locals, uint instr, BinOp op)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            byte c = Encoding.C(instr);
            var left = locals[b];
            var right = locals[c];
            if (left == null || right == null)
            {
                return new ValueResult(null,
                    new Errors.Error(new Lexer.Position(0, 0, 0, "<vm>", string.Empty),
                                     new Lexer.Position(0, 0, 0, "<vm>", string.Empty),
                                     "VM:NullOperand", "null operand"));
            }

            // M80: SEq / SNe identity fast path. Strict equality on the
            // same RuntimeValue instance is trivially true (SEq) / false
            // (SNe) regardless of the underlying type's virtual override.
            // Skips the `GetComparisonStrictEq` / `GetComparisonStrictNe`
            // dispatch entirely for the (common) self-compare case
            // (`x === x`) and for interned-singleton hits
            // (`NullValue.Null === NullValue.Null`, `BooleanValue.Of(true)
            // === BooleanValue.Of(true)`). The fast path is universally
            // safe — when the two slot reads land on the same heap
            // instance the strict-equality answer cannot depend on the
            // type's per-field comparison logic.
            if (op == BinOp.SEq || op == BinOp.SNe)
            {
                if (object.ReferenceEquals(left, right))
                {
                    var produced = BooleanValue.Of(op == BinOp.SEq);
                    locals[a] = produced;
                    return new ValueResult(produced, null);
                }
            }

            // M15: fast path for `NumberValue op NumberValue` where both
            // operands are integer-valued and fit in int64. Skips the virtual
            // call + the BigInteger arithmetic entirely; results land in the
            // small-int intern cache when they fit. Comparisons short-circuit
            // to BooleanValue.Of(bool) — no allocation at all. Overflow falls
            // through to the slow path (caught by the OverflowException try).
            if (left.Type == RuntimeValueType.Number && right.Type == RuntimeValueType.Number)
            {
                var ln = (NumberValue)left;
                var rn = (NumberValue)right;
                if (TryGetInt64(ln, out long lv) && TryGetInt64(rn, out long rv))
                {
                    // M26.2: branchless overflow detection replaces the
                    // try/catch `checked()` wrapper. The classic signed
                    // overflow predicate for additive ops is
                    // `((lv ^ sum) & (rv ^ sum)) < 0`: a true sign disagreement
                    // between both operands and the result indicates a
                    // wraparound, so we fall through to the slow BigNumber
                    // path. Avoids the SEH frame entry/exit cost the
                    // checked()+catch had per call.
                    RuntimeValue? produced = null;
                    bool handled = true;
                    switch (op)
                    {
                        case BinOp.Add:
                        {
                            long sum = lv + rv;
                            if (((lv ^ sum) & (rv ^ sum)) < 0) { handled = false; break; }
                            produced = NumberValue.OfInt64(sum);
                            break;
                        }
                        case BinOp.Sub:
                        {
                            long diff = lv - rv;
                            if (((lv ^ rv) & (lv ^ diff)) < 0) { handled = false; break; }
                            produced = NumberValue.OfInt64(diff);
                            break;
                        }
                        case BinOp.Mul:
                        {
                            // Full int64 multiply with exact overflow detection
                            // via the 128-bit product (see MulII). Keeps every
                            // int64-fitting product on the intern-cache fast
                            // path; only genuine >64-bit results fall through to
                            // the BigNumber operator.
                            long hi = System.Math.BigMul(lv, rv, out long lo);
                            if (hi == (lo >> 63))
                            {
                                produced = NumberValue.OfInt64(lo);
                            }
                            else { handled = false; }
                            break;
                        }
                        case BinOp.Div:
                        {
                            // Ra `number / number` is exact integer division
                            // only when it divides evenly (BigNumber operator/
                            // returns the integer quotient iff the remainder is
                            // zero, else a fractional decimal). Take the int64
                            // fast path only for exact division; defer div-by-
                            // zero and the long.MinValue / -1 overflow to the
                            // BigNumber path so the error / wide result is
                            // produced unchanged. Mirrors DivII's deopt gate.
                            if (rv != 0 && !(lv == long.MinValue && rv == -1) && (lv % rv) == 0)
                            {
                                produced = NumberValue.OfInt64(lv / rv);
                            }
                            else { handled = false; }
                            break;
                        }
                        case BinOp.Mod:
                        {
                            // Integer modulo: BigNumber.Mod computes
                            // `ToBigInteger(a) % ToBigInteger(b)`, whose sign
                            // (of the dividend) matches C#'s `long %`. Both
                            // operands are integer-valued (TryGetInt64 required
                            // Scale.IsZero), so `lv % rv` is identical. Div-by-
                            // zero defers to the BigNumber path for the error.
                            // `long.MinValue % -1` is 0 in C# (no overflow), so
                            // no special guard is needed.
                            if (rv != 0)
                            {
                                produced = NumberValue.OfInt64(lv % rv);
                            }
                            else { handled = false; }
                            break;
                        }
                        case BinOp.Lt:  produced = BooleanValue.Of(lv <  rv); break;
                        case BinOp.Le:  produced = BooleanValue.Of(lv <= rv); break;
                        case BinOp.Gt:  produced = BooleanValue.Of(lv >  rv); break;
                        case BinOp.Ge:  produced = BooleanValue.Of(lv >= rv); break;
                        case BinOp.Eq:  produced = BooleanValue.Of(lv == rv); break;
                        case BinOp.Ne:  produced = BooleanValue.Of(lv != rv); break;
                        case BinOp.SEq: produced = BooleanValue.Of(lv == rv); break;
                        case BinOp.SNe: produced = BooleanValue.Of(lv != rv); break;
                        default: handled = false; break;
                    }
                    if (handled && produced != null)
                    {
                        locals[a] = produced;
                        return new ValueResult(produced, null);
                    }
                }
            }

            ValueResult r = op switch
            {
                BinOp.Add  => left.AddedTo(right),
                BinOp.Sub  => left.SubbedBy(right),
                BinOp.Mul  => left.MultedBy(right),
                BinOp.Div  => left.DivedBy(right),
                BinOp.Mod  => left.ModuledBy(right),
                BinOp.Pow  => left.PowedBy(right),
                BinOp.Shl  => left.BitwiseLeftShiftedBy(right),
                BinOp.Shr  => left.BitwiseRightShiftedBy(right),
                BinOp.Ushr => left.BitwiseUnsignedRightShiftedBy(right),
                BinOp.Rol  => left.BitwiseRotateLeftedBy(right),
                BinOp.Ror  => left.BitwiseRotateRightedBy(right),
                BinOp.BAnd => left.BitwiseAndedBy(right),
                BinOp.BOr  => left.BitwiseOredBy(right),
                // BXor used to dispatch to BitwiseAndedBy — a long-standing
                // typo that was reachable only via IR-internal rewrites
                // (no user-visible token maps to XOR today; `^` is exponent
                // in Ra). Now routes to the dedicated BitwiseXoredBy virtual.
                BinOp.BXor => left.BitwiseXoredBy(right),
                BinOp.Eq   => left.GetComparisonEq(right),
                BinOp.Ne   => left.GetComparisonNe(right),
                BinOp.SEq  => left.GetComparisonStrictEq(right),
                BinOp.SNe  => left.GetComparisonStrictNe(right),
                BinOp.Lt   => left.GetComparisonLt(right),
                BinOp.Le   => left.GetComparisonLte(right),
                BinOp.Gt   => left.GetComparisonGt(right),
                BinOp.Ge   => left.GetComparisonGte(right),
                // `left in right` — membership. InCollection returns a
                // BooleanValue or an IllegalOperation error on a non-collection
                // RHS (mirrors BinaryOperationNodeVisitor's Keyword.In branch).
                BinOp.In   => left.InCollection(right),
                _ => new ValueResult(null, null),
            };

            if (r.Error == null) locals[a] = r.Value;
            return r;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool TryGetInt64(NumberValue n, out long result)
        {
            var bn = n.Value;
            if (!bn.Scale.IsZero) { result = 0; return false; }
            var u = bn.Unscaled;
            // BigInteger.GetByteCount() / TryWriteBytes would also work but
            // explicit range check against int64 is the cheapest path that
            // matches "fits in long".
            if (u < s_int64Min || u > s_int64Max) { result = 0; return false; }
            result = (long)u;
            return true;
        }

        private static readonly System.Numerics.BigInteger s_int64Min = long.MinValue;
        private static readonly System.Numerics.BigInteger s_int64Max = long.MaxValue;

        // M66.5: bitmap keyed by opcode tag — true iff the opcode writes
        // `locals[A]`. Used by the dispatch loop to invalidate
        // `LongValid[A]` before the case body, so a stale II shadow
        // does not survive slot reuse by a non-II writer. Mirrors
        // `SsaForm.DefinedSlot` — keep in sync when adding new opcodes.
        // M79: thread-static StringBuilder rented by `OP_INTERP` and
        // any other dispatch helper that builds an ad-hoc string.
        // Capacity is grown across calls but capped — pathological
        // single-Interp inputs that ballooned the buffer would
        // otherwise keep the bloat alive for the whole thread.
        [System.ThreadStatic]
        private static System.Text.StringBuilder? s_interpStringBuilder;
        private const int InterpStringBuilderMaxCapacity = 4096;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static System.Text.StringBuilder RentInterpStringBuilder()
        {
            var sb = s_interpStringBuilder;
            if (sb == null)
            {
                sb = new System.Text.StringBuilder(64);
                s_interpStringBuilder = sb;
                return sb;
            }
            if (sb.Capacity > InterpStringBuilderMaxCapacity)
            {
                // Replace bloated cache. The previous instance gets
                // GC'd after this dispatch — the Interp result has
                // already been materialised via `.ToString()`.
                sb = new System.Text.StringBuilder(64);
                s_interpStringBuilder = sb;
                return sb;
            }
            sb.Clear();
            return sb;
        }

        // M81 — cold paths for the per-PC inline caches. Extracted to
        // `[NoInlining]` helpers so the dispatch loop's C# stack frame
        // stays compact (the deep-recursion test exercises depth 2000
        // and the C# stack budget is the binding constraint). The
        // hot-path primary-hit branch stays inline at the case body;
        // primary-miss + PIC scan + PIC prime + virtual call land here.

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Parser.Nodes.Functions.FunctionDefinitionNode? CallMethodIcMissCold(
            ref RaLanguage.Interpreter.IR.CallMethodIcSlot slot,
            Values.Classes.BoundClassMethodGroupValue bgrp,
            int argCount,
            System.Collections.Generic.List<RuntimeValue> argList,
            System.Collections.Generic.Dictionary<string, RuntimeValue> emptyNamed)
        {
            // PIC scan (primary already missed by caller).
            if (slot.Pic != null)
            {
                for (int i = 0; i < slot.Pic.Length; i++)
                {
                    ref var pe = ref slot.Pic[i];
                    if (pe.ChosenMethod != null
                        && pe.ArgCount == argCount
                        && ReferenceEquals(pe.ReceiverShape, bgrp.Definition))
                    {
                        return (Parser.Nodes.Functions.FunctionDefinitionNode?)pe.ChosenMethod;
                    }
                }
            }
            var sel = bgrp.PickOverload(argList, emptyNamed);
            if (sel == null) return null;
            if (!slot.Primed)
            {
                slot.ReceiverShape = bgrp.Definition;
                slot.ArgCount = argCount;
                slot.ChosenMethod = sel;
                slot.IsStatic = false;
                slot.Primed = true;
            }
            else
            {
                if (slot.Pic == null)
                    slot.Pic = new RaLanguage.Interpreter.IR.CallMethodIcEntry[2];
                int free = -1;
                for (int i = 0; i < slot.Pic.Length; i++)
                {
                    if (slot.Pic[i].ChosenMethod == null) { free = i; break; }
                }
                if (free >= 0)
                {
                    slot.Pic[free].ReceiverShape = bgrp.Definition;
                    slot.Pic[free].ArgCount = argCount;
                    slot.Pic[free].ChosenMethod = sel;
                    slot.Pic[free].IsStatic = false;
                }
                else
                {
                    // LRU evict — shift left, write at tail.
                    slot.Pic[0] = slot.Pic[1];
                    slot.Pic[1].ReceiverShape = bgrp.Definition;
                    slot.Pic[1].ArgCount = argCount;
                    slot.Pic[1].ChosenMethod = sel;
                    slot.Pic[1].IsStatic = false;
                }
            }
            return sel;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static (RuntimeValue? Value, Errors.Error? Error) EnumAccessIcMissCold(
            ref RaLanguage.Interpreter.IR.EnumAccessIcSlot slot,
            Parser.Nodes.Enums.EnumAccessNode node,
            Context ctx,
            RuntimeValue enumValue)
        {
            // PIC scan.
            if (slot.Pic != null)
            {
                for (int i = 0; i < slot.Pic.Length; i++)
                {
                    ref var pe = ref slot.Pic[i];
                    if (pe.Result != null && ReferenceEquals(pe.EnumType, enumValue))
                        return (pe.Result, null);
                }
            }
            var sub = Runtime.EnumAccessHelper.Apply(node, ctx, enumValue);
            if (sub.Error != null) return (null, sub.Error);
            if (slot.Result == null)
            {
                slot.EnumType = enumValue;
                slot.Result = sub.Value;
            }
            else
            {
                if (slot.Pic == null)
                    slot.Pic = new RaLanguage.Interpreter.IR.EnumAccessIcEntry[2];
                int free = -1;
                for (int i = 0; i < slot.Pic.Length; i++)
                {
                    if (slot.Pic[i].Result == null) { free = i; break; }
                }
                if (free >= 0)
                {
                    slot.Pic[free].EnumType = enumValue;
                    slot.Pic[free].Result = sub.Value;
                }
                else
                {
                    slot.Pic[0] = slot.Pic[1];
                    slot.Pic[1].EnumType = enumValue;
                    slot.Pic[1].Result = sub.Value;
                }
            }
            return (sub.Value, null);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ValueResult CastIcMissCold(
            ref RaLanguage.Interpreter.IR.CastIcSlot slot,
            RuntimeValue v,
            Parser.Nodes.Operations.CastNode castNode,
            Context ctx)
        {
            var (cv, cerr) = v.CastTo(castNode.TargetType);
            if (cerr != null) return new ValueResult(null, cerr);
            bool isNoop = cv != null && cv.Type == v.Type;
            if (!slot.Primed)
            {
                slot.SrcType = v.Type;
                slot.IsNoop = isNoop;
                slot.Primed = true;
            }
            else if (slot.SrcType != v.Type)
            {
                if (slot.Pic == null)
                    slot.Pic = new RaLanguage.Interpreter.IR.CastIcEntry[2];
                bool placed = false;
                for (int i = 0; i < slot.Pic.Length; i++)
                {
                    if (slot.Pic[i].Primed && slot.Pic[i].SrcType == v.Type)
                    {
                        slot.Pic[i].IsNoop = isNoop;
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    int free = -1;
                    for (int i = 0; i < slot.Pic.Length; i++)
                    {
                        if (!slot.Pic[i].Primed) { free = i; break; }
                    }
                    if (free >= 0)
                    {
                        slot.Pic[free].SrcType = v.Type;
                        slot.Pic[free].IsNoop = isNoop;
                        slot.Pic[free].Primed = true;
                    }
                    else
                    {
                        slot.Pic[0] = slot.Pic[1];
                        slot.Pic[1].SrcType = v.Type;
                        slot.Pic[1].IsNoop = isNoop;
                        slot.Pic[1].Primed = true;
                    }
                }
            }
            return new ValueResult(cv?.SetContext(ctx).SetPos(castNode.PositionStart, castNode.PositionEnd), null);
        }

        // PERF: per-thread freelist of positional-arg lists for OP_Call /
        // OP_TailCall. The `positionalArgs` List is a C#-INTERNAL transport —
        // Ra code only ever observes copied *elements* (bound into the callee's
        // frame slots / symbol table), never the List object itself. So the
        // sole way the List can escape a call is a C# field / async-closure
        // capture; the only such capture (`FunctionValue`'s async dispatch,
        // `capturedArgs = positionalArgs`) fires exclusively for async callees,
        // which never report `IsCompletedSuccessfully`. Returning a list to the
        // pool STRICTLY on the synchronous-completion branch is therefore
        // escape-free: sync completion means the whole call (arg binding + body
        // execution) finished and the transport is dead. The pool is a stack so
        // nested calls each hold their own rented list (LIFO release).
        [System.ThreadStatic]
        private static System.Collections.Generic.Stack<System.Collections.Generic.List<RuntimeValue>>? t_argListPool;
        private const int ArgListPoolCap = 64;
        private const int ArgListMinCapacity = 4;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static System.Collections.Generic.List<RuntimeValue> RentArgList(int capacity)
        {
            var pool = t_argListPool;
            if (pool != null && pool.Count > 0)
            {
                var list = pool.Pop();
                list.Clear();
                return list;
            }
            return new System.Collections.Generic.List<RuntimeValue>(
                capacity < ArgListMinCapacity ? ArgListMinCapacity : capacity);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void ReturnArgList(System.Collections.Generic.List<RuntimeValue> list)
        {
            var pool = t_argListPool ??= new System.Collections.Generic.Stack<System.Collections.Generic.List<RuntimeValue>>();
            if (pool.Count < ArgListPoolCap) pool.Push(list);
        }

        private static readonly bool[] s_writesLocalsA = BuildWritesLocalsATable();
        private static bool[] BuildWritesLocalsATable()
        {
            var t = new bool[256];
            void Mark(Opcode op) => t[(byte)op] = true;
            // Constants / loads.
            Mark(Opcode.LoadConst); Mark(Opcode.LoadNull);
            Mark(Opcode.LoadTrue); Mark(Opcode.LoadFalse);
            Mark(Opcode.LoadIntS);
            Mark(Opcode.LoadGlobal); Mark(Opcode.LoadBuiltin);
            Mark(Opcode.LoadUpval); Mark(Opcode.LoadLocalS);
            Mark(Opcode.Move); Mark(Opcode.Alias); Mark(Opcode.MoveLet);
            Mark(Opcode.Borrow); Mark(Opcode.Deref);
            // Arithmetic / bitwise.
            Mark(Opcode.Add); Mark(Opcode.Sub); Mark(Opcode.Mul);
            Mark(Opcode.Div); Mark(Opcode.Mod); Mark(Opcode.Pow);
            Mark(Opcode.Shl); Mark(Opcode.Shr);
            Mark(Opcode.Ushr); Mark(Opcode.Rol); Mark(Opcode.Ror);
            Mark(Opcode.BAnd); Mark(Opcode.BOr); Mark(Opcode.BXor);
            Mark(Opcode.AddNN); Mark(Opcode.SubNN); Mark(Opcode.MulNN);
            Mark(Opcode.Neg); Mark(Opcode.Not); Mark(Opcode.BNot);
            // Comparisons.
            Mark(Opcode.Eq); Mark(Opcode.Ne);
            Mark(Opcode.SEq); Mark(Opcode.SNe);
            Mark(Opcode.Lt); Mark(Opcode.Le);
            Mark(Opcode.Gt); Mark(Opcode.Ge);
            // Membership — writes the BooleanValue result into locals[A].
            Mark(Opcode.In);
            Mark(Opcode.NullCoal);
            // Strings.
            Mark(Opcode.StrConcat); Mark(Opcode.Interp); Mark(Opcode.Fmt);
            Mark(Opcode.With);
            // Collections.
            Mark(Opcode.NewList); Mark(Opcode.NewMap);
            Mark(Opcode.NewSet); Mark(Opcode.NewTuple);
            Mark(Opcode.ListGet); Mark(Opcode.MapGet);
            Mark(Opcode.Range);
            // Member / index reads.
            Mark(Opcode.GetMember); Mark(Opcode.EnumAccess);
            Mark(Opcode.ForEachIterable); Mark(Opcode.ListLen);
            // Casting / introspection.
            Mark(Opcode.Cast); Mark(Opcode.Is);
            Mark(Opcode.Typeof); Mark(Opcode.Nameof);
            // Closures / functions / OOP.
            Mark(Opcode.Closure); Mark(Opcode.DefineFunction);
            Mark(Opcode.GetSelf); Mark(Opcode.GetSuper);
            Mark(Opcode.Call); Mark(Opcode.CallKw); Mark(Opcode.CallMethod);
            Mark(Opcode.NewInstance);
            Mark(Opcode.NativeDefine);
            Mark(Opcode.DefineType);
            // Async.
            Mark(Opcode.Await); Mark(Opcode.Spawn);
            // II family — these manage `LongValid[a]` themselves in
            // `ExecuteUnboxedII`. They are intentionally NOT marked
            // here: the pre-clear at dispatch entry would wipe
            // `LongValid[A]` *before* the case body runs, and an II
            // opcode whose `A` operand alias `B` or `C` (e.g.
            // `AddII iter, iter, step` for a loop-carried counter)
            // would then read the just-cleared shadow via
            // `TryReadAsLong` and deopt every iteration. Each II
            // case body assigns the correct final `LongValid[A]`
            // value at exit, preserving the slot-reuse invariant
            // that the bitmap-driven pre-clear gives non-II writers.
            //
            // M72: same exclusion for the FF family — `AddFF iter,
            // iter, step` etc. need their A=B aliasing read
            // unaffected by the pre-clear. Each FF case body
            // assigns the final tag itself.
            return t;
        }

        private enum BinOp
        {
            Add, Sub, Mul, Div, Mod, Pow, Shl, Shr, BAnd, BOr, BXor,
            // Extended bitwise — `>>>`, `<<<<`, `>>>>`. The logical LEFT shift
            // (`<<<`) shares Shl: identical bit pattern, distinct token only.
            Ushr, Rol, Ror,
            Eq, Ne, SEq, SNe, Lt, Le, Gt, Ge,
            // `in` / membership. `not in` is In + a following unary Not.
            // Dispatches to RuntimeValue.InCollection (boxed only — no numeric
            // fast path, since the RHS is always a collection/string).
            In,
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static Lexer.Position DummyPos(Context ctx)
            => new Lexer.Position(0, 0, 0, ctx?.DisplayName ?? "<vm>", string.Empty);

        // M44: binary-search the PC-span table for the source range covering
        // the currently-dispatched opcode. Returns (start, end) positions;
        // falls back to DummyPos when no entry covers `pc`. Called by every
        // opcode handler that constructs a RuntimeError so the user sees the
        // real source location instead of "1:1".
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static (Lexer.Position Start, Lexer.Position End) ResolveSpan(VmFrame f, int pc, Context ctx)
        {
            var pcs = f.Function.PcSpansPc;
            var spans = f.Function.PcSpansSpan;
            if (pcs == null || spans == null || pcs.Length == 0)
                return (DummyPos(ctx), DummyPos(ctx));
            // Binary search largest pcs[i] <= pc.
            int lo = 0, hi = pcs.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (pcs[mid] <= pc) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (found < 0) return (DummyPos(ctx), DummyPos(ctx));
            return (spans[found].Start, spans[found].End);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static Error MakeIcError(Context ctx, string message)
        {
            var empty = DummyPos(ctx);
            return new Errors.Types.RuntimeError(empty, empty, message, ctx!);
        }

        // L7 variant-pattern opcode bodies, kept OUT of the recursive async
        // Execute frame (see the call sites). NoInlining is load-bearing: an
        // inlined body would re-merge these locals into Execute's MoveNext frame
        // and reintroduce the per-recursion-level stack cost. LocalsView is a
        // readonly struct over the live ValueSlot[], so writes through the
        // by-value copy land in the caller's slots.
        // L7 — explicit-enum disambiguator. dst = scrut is an EnumValue whose
        // ENUM TYPE name == Names[c]. A record carries no EnumName → returns false
        // (an explicit `case Enum.Variant` never matches a record, mirroring the
        // visitor's `vap.EnumName == null` record-branch guard).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpEnumNameEq(LocalsView locals, uint instr, string[] names)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            string name = names[Encoding.C(instr)];
            bool matched = locals[b] is EnumValue ev
                && string.Equals(ev.EnumName, name, System.StringComparison.Ordinal);
            locals[a] = BooleanValue.Of(matched);
        }

        // L7 — no-match terminator for a catch-all-less match. Returns the
        // exception (the call site `throw`s it) so the cold construction stays out
        // of the hot Execute frame.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RaUserError OpMatchFail(Context ctx)
            => new RaUserError(MakeIcError(ctx, "no match arm covered the scrutinee value"));

        // L7 — tuple shape: dst = scrut is TupleValue with exactly c elements.
        // The count is the whole shape (mismatch is a no-match, not an error).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpTupleShape(LocalsView locals, uint instr)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            int len = Encoding.C(instr);
            locals[a] = BooleanValue.Of(locals[b] is TupleValue tv && tv.Elements.Count == len);
        }

        // L7 — struct/class/record nominal shape: dst = scrut is an instance whose
        // declared type name == Names[c]. StructInstanceValue covers records (its
        // subclass); ClassInstanceValue is checked separately (mirrors the visitor).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpStructShape(LocalsView locals, uint instr, string[] names)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            string name = names[Encoding.C(instr)];
            var sv = locals[b];
            bool matched =
                (sv is RaLanguage.Interpreter.Values.Structs.StructInstanceValue siv
                    && string.Equals(siv.Definition.StructName, name, System.StringComparison.Ordinal))
                || (sv is RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue civ
                    && string.Equals(civ.Definition.ClassName, name, System.StringComparison.Ordinal));
            locals[a] = BooleanValue.Of(matched);
        }

        // L7 — struct/class field-by-name extract. Reached only after a passing
        // StructShape (so the scrutinee IS the matched struct/class). Throws the
        // visitor's EXACT "struct/class 'X' has no field 'f'" error when absent.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpStructFieldGet(LocalsView locals, uint instr, string[] names, Context ctx)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            string field = names[Encoding.C(instr)];
            var sv = locals[b];
            if (sv is RaLanguage.Interpreter.Values.Structs.StructInstanceValue siv)
            {
                if (!siv.HasField(field))
                    throw new RaUserError(MakeIcError(ctx,
                        $"struct '{siv.Definition.StructName}' has no field '{field}'"));
                locals[a] = siv.GetField(field);
            }
            else if (sv is RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue civ)
            {
                if (!civ.HasField(field))
                    throw new RaUserError(MakeIcError(ctx,
                        $"class '{civ.Definition.ClassName}' has no field '{field}'"));
                locals[a] = civ.GetField(field);
            }
            else
            {
                throw new RaUserError(MakeIcError(ctx, "VM: StructFieldGet on non-struct/class value"));
            }
        }

        // L7 — list shape: dst = scrut is ListValue with the required length.
        // c packs (modeBit<<7)|len7 — mode 0 = exact (Count==len, a no-rest
        // pattern), mode 1 = at-least (Count>=len, a `..rest` pattern). A length
        // mismatch is a no-match (not an error).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpListShape(LocalsView locals, uint instr)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            int c = Encoding.C(instr);
            int len = c & 0x7F;
            bool atLeast = (c & 0x80) != 0;
            bool matched = locals[b] is ListValue lv
                && (atLeast ? lv.Elements.Count >= len : lv.Elements.Count == len);
            locals[a] = BooleanValue.Of(matched);
        }

        // L7 — list element from the END: dst = Elements[Count - kFromEnd]
        // (k 1-based; 1 == last). The suffix elements after a `..rest`. Reached
        // only after a passing ListShape that confirmed Count >= prefix+suffix.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpListElemBack(LocalsView locals, uint instr)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            int k = Encoding.C(instr);
            if (locals[b] is ListValue lv)
            {
                int idx = lv.Elements.Count - k;
                locals[a] = (idx >= 0 && idx < lv.Elements.Count) ? lv.Elements[idx] : NullValue.Null;
            }
            else locals[a] = NullValue.Null;
        }

        // L7 — captured middle of a `..rest`: dst = new ListValue of
        // Elements[prefix .. Count-suffix]. c packs (prefix4<<4)|suffix4. Reached
        // only after a passing ListShape (Count >= prefix+suffix).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpListRestSlice(LocalsView locals, uint instr)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            int c = Encoding.C(instr);
            int prefix = (c >> 4) & 0x0F;
            int suffix = c & 0x0F;
            if (locals[b] is ListValue lv)
            {
                int restLen = lv.Elements.Count - prefix - suffix;
                if (restLen < 0) restLen = 0;
                locals[a] = new ListValue(lv.Elements.GetRange(prefix, restLen));
            }
            else locals[a] = new ListValue(new System.Collections.Generic.List<RuntimeValue>());
        }

        // L7 — `case is T`. dst = the scrutinee's runtime type matches the
        // TestedType of the IsTypeNode parked in AstRefs[refIdx] (WideC-resolved,
        // like Cast), via TypeSystem.IsRuntimeTypeMatch — byte-identical to the
        // visitor's TryMatchTypePattern test. A null scrutinee never matches.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpIsType(LocalsView locals, uint instr, int wideHiC,
                                     RaLanguage.Parser.Nodes.AstNode[] astRefs, Context ctx)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            int refIdx = wideHiC >= 0 ? ((wideHiC << 8) | Encoding.C(instr)) : Encoding.C(instr);
            if (refIdx >= astRefs.Length
                || astRefs[refIdx] is not RaLanguage.Parser.Nodes.Operations.IsTypeNode isn)
                throw new RaUserError(MakeIcError(ctx, "VM: IsType ref out of range or not an IsTypeNode"));
            var sv = locals[b];
            bool matched = sv != null && RaLanguage.Types.TypeSystem.IsRuntimeTypeMatch(ctx, isn.TestedType, sv);
            locals[a] = BooleanValue.Of(matched);
        }

        // L7 — map shape: dst = scrut is MapValue with the required entry count
        // (c packs open-rest bit + count7; open => Count>=count, closed => ==).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpMapShape(LocalsView locals, uint instr)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            int c = Encoding.C(instr);
            int count = c & 0x7F;
            bool open = (c & 0x80) != 0;
            bool matched = locals[b] is MapValue mv
                && (open ? mv.Pairs.Count >= count : mv.Pairs.Count == count);
            locals[a] = BooleanValue.Of(matched);
        }

        // L7 — map structural key presence: dst = the map (slot B) contains the key
        // in slot C (linear GetComparisonEq scan, mirroring the visitor's
        // TryMapLookup). A non-map / missing key -> false (no-match, not an error).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpMapHasKey(LocalsView locals, uint instr, Context ctx)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            byte cSlot = Encoding.C(instr);
            bool found = false;
            if (locals[b] is MapValue mv && locals[cSlot] is RuntimeValue key)
            {
                for (int i = 0; i < mv.Pairs.Count; i++)
                {
                    var (eqVal, eqErr) = mv.Pairs[i].Key.GetComparisonEq(key);
                    if (eqErr != null) throw new RaUserError(eqErr);
                    if (eqVal is BooleanValue bv && bv.Value) { found = true; break; }
                }
            }
            locals[a] = BooleanValue.Of(found);
        }

        // L7 — map value-by-key (a preceding MapHasKey confirmed presence). dst =
        // the value paired with key slot C, or null if somehow absent.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpMapGetKey(LocalsView locals, uint instr, Context ctx)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            byte cSlot = Encoding.C(instr);
            RuntimeValue result = NullValue.Null;
            if (locals[b] is MapValue mv && locals[cSlot] is RuntimeValue key)
            {
                for (int i = 0; i < mv.Pairs.Count; i++)
                {
                    var (eqVal, eqErr) = mv.Pairs[i].Key.GetComparisonEq(key);
                    if (eqErr != null) throw new RaUserError(eqErr);
                    if (eqVal is BooleanValue bv && bv.Value) { result = mv.Pairs[i].Value; break; }
                }
            }
            locals[a] = result;
        }

        // L7 — `target?`. On Result.Ok(v): writes v to dst (A) and returns null
        // (the caller falls through). On Result.Err(e): returns the whole Result
        // value (the caller early-returns it). Non-Result / unexpected variant:
        // throws the visitor's EXACT error (parity captures err.Details).
        // Byte-identical to TryUnwrapNodeVisitor.Apply.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue? OpTryUnwrap(LocalsView locals, uint instr, Context ctx)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            var value = locals[b];
            if (value is not EnumValue ev || !string.Equals(ev.EnumName, "Result", System.StringComparison.Ordinal))
                throw new RaUserError(MakeIcError(ctx, "'?' can only be applied to a 'Result<T, E>' value"));
            if (string.Equals(ev.MemberName, "Ok", System.StringComparison.Ordinal))
            {
                if (ev.Payload.Count != 1)
                    throw new RaUserError(MakeIcError(ctx, $"Result.Ok payload arity {ev.Payload.Count} is unexpected"));
                locals[a] = ev.Payload[0].Aliased().SetContext(ctx);
                return null;
            }
            if (string.Equals(ev.MemberName, "Err", System.StringComparison.Ordinal))
                return value.Aliased().SetContext(ctx);
            throw new RaUserError(MakeIcError(ctx, $"'?' encountered unexpected Result variant '{ev.MemberName}'"));
        }

        // L7 — destructuring bind by name: SetLocal(Names[idx], locals[a]).
        // Mirrors the destructuring visitor's `context.SymbolTable.SetLocal(name,
        // value)` (plain var-kind binding) for binders the Resolver leaves
        // name-based. Reads A; no slot write.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpDeclareLocalByName(LocalsView locals, uint instr, string[] names, Context ctx)
        {
            byte a = Encoding.A(instr);
            int idx = Encoding.Imm16(instr);
            ctx.SymbolTable.SetLocal(names[idx], locals[a] ?? NullValue.Null);
        }

        // L8 — `emit value` into the current async-stream producer. Byte-identical
        // to EmitNodeVisitor (producer presence, element-type check / inference,
        // accepted check). Synchronous (producer.Emit returns bool, no await).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpEmit(RuntimeValue? value, Context ctx)
        {
            var producer = ctx.AsyncCtx?.CurrentStreamProducer;
            if (producer == null)
                throw new RaUserError(MakeIcError(ctx, "'emit' is only valid inside an 'async stream fn' body"));
            var v = value ?? (RuntimeValue)NullValue.Null;
            var owner = producer.OwnerValue;
            if (owner != null)
            {
                if (owner.ElementType != null && !owner.ElementType.IsTypeParameter
                    && !RaLanguage.Types.TypeSystem.IsAssignable(ctx, owner.ElementType, v))
                    throw new RaUserError(MakeIcError(ctx,
                        $"Stream element type mismatch: expected '{owner.ElementType}', got '{v.Type}'"));
                if (owner.ElementType == null && v.Type != RuntimeValueType.Null)
                    owner.ElementType = RaLanguage.Types.TypeSystem.GetDescriptorFromRuntimeValue(v);
            }
            if (!producer.Emit(v))
                throw new RaUserError(MakeIcError(ctx, "Stream consumer has been cancelled or closed"));
        }

        // L8 — gather the spawn callee (fnSlot) + positional args (fnSlot+1..) and
        // schedule via the shared SpawnNodeVisitor.SpawnCore (byte-identical to
        // the visitor). The lowered form is positional-only (named/ref/spread
        // spawns fall back), so namedArgs is always empty.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeResult OpSpawn(LocalsView locals, byte fnSlot, int argCount, Context ctx)
        {
            var res = new RuntimeResult();
            if (locals[fnSlot] is not RaLanguage.Interpreter.Values.Functions.BaseFunctionValue fn)
                return res.Failure(MakeIcError(ctx, "spawn requires a function call expression"));
            var posArgs = new System.Collections.Generic.List<RuntimeValue>(argCount);
            for (int i = 0; i < argCount; i++) posArgs.Add(locals[fnSlot + 1 + i] ?? NullValue.Null);
            var namedArgs = new System.Collections.Generic.Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal);
            return Visitors.Async.SpawnNodeVisitor.SpawnCore(fn, posArgs, namedArgs, ctx, DummyPos(ctx), DummyPos(ctx));
        }

        // L9 — OP_ASM_INVOKE: execute a parked pure-text inline asm block. The
        // AsmBlockNode lives in DefineRefs[idx]; rebuild its constant source from
        // the text parts (the IrCompiler gates this opcode to interp-free blocks),
        // then assemble-on-first-use (cached) + execute via the shared
        // AsmBlockExecCore. NoInlining keeps these locals off the recursive
        // Execute MoveNext frame (M85 deep-recursion budget).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue OpAsmInvoke(VmFrame f, ushort defineRefIdx, Context ctx)
        {
            var refs = f.Function.DefineRefs;
            if (defineRefIdx >= refs.Length)
                throw new RaUserError(MakeIcError(ctx, $"VM: AsmInvoke refIdx {defineRefIdx} out of range"));
            var node = (Parser.Nodes.Asm.AsmBlockNode)refs[defineRefIdx];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < node.Parts.Count; i++)
                sb.Append(((Parser.Nodes.Asm.AsmTextPartNode)node.Parts[i]).Text);
            var res = Visitors.Asm.AsmBlockNodeVisitor.AsmBlockExecCore(
                sb.ToString(), node.ReturnTypes, ctx, node.PositionStart, node.PositionEnd);
            if (res.Error != null) throw new RaUserError(res.Error);
            return res.Value ?? NullValue.Null;
        }

        // L10 — OP_ASM_INVOKE_I: interpolated inline asm. The parked AsmBlockNode
        // lives in DefineRefs[idx]; its %{…} args are pre-evaluated in the band
        // [argsBase .. argsBase+N-1] (N = interp parts, in part order). Format
        // them into the source via the shared TryBuildInterpSource (byte-identical
        // to the visitor), then assemble-on-first-use + execute. NoInlining keeps
        // these locals off the recursive Execute MoveNext frame (M85 budget).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RuntimeValue OpAsmInvokeI(VmFrame f, LocalsView locals, byte argsBase, byte defineRefIdx, Context ctx)
        {
            var refs = f.Function.DefineRefs;
            if (defineRefIdx >= refs.Length)
                throw new RaUserError(MakeIcError(ctx, $"VM: AsmInvokeI refIdx {defineRefIdx} out of range"));
            var node = (Parser.Nodes.Asm.AsmBlockNode)refs[defineRefIdx];

            int interpCount = 0;
            for (int i = 0; i < node.Parts.Count; i++)
                if (node.Parts[i].NodeType == AstNodeType.AsmInterpPart) interpCount++;
            var interpArgs = new System.Collections.Generic.List<RuntimeValue>(interpCount);
            for (int k = 0; k < interpCount; k++) interpArgs.Add(locals[argsBase + k] ?? NullValue.Null);

            if (!Visitors.Asm.AsmBlockNodeVisitor.TryBuildInterpSource(node, interpArgs, out string source, out string? buildErr))
                throw new RaUserError(new RaLanguage.Errors.Types.RuntimeError(
                    node.PositionStart, node.PositionEnd, buildErr!, ctx));

            var res = Visitors.Asm.AsmBlockNodeVisitor.AsmBlockExecCore(
                source, node.ReturnTypes, ctx, node.PositionStart, node.PositionEnd);
            if (res.Error != null) throw new RaUserError(res.Error);
            return res.Value ?? NullValue.Null;
        }

        // L10 — OP_ANNOTATION_APPLY: build the AnnotationInstanceValue for a
        // standalone `@Name(args)` value. The parked node lives in DefineRefs;
        // AnnotationApplicationNodeVisitor.Apply is effectively synchronous
        // (EvaluateArgs uses IrExpressionEvaluator.EvaluateBlocking), so
        // SyncAwait.Get returns immediately. NoInlining keeps the (re-entrant)
        // arg evaluation off the recursive Execute MoveNext frame (M85).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private RuntimeValue OpAnnotationApply(VmFrame f, ushort defineRefIdx, Context ctx)
        {
            var refs = f.Function.DefineRefs;
            if (defineRefIdx >= refs.Length)
                throw new RaUserError(MakeIcError(ctx, $"VM: AnnotationApply refIdx {defineRefIdx} out of range"));
            var node = (Parser.Nodes.Annotations.AnnotationApplicationNode)refs[defineRefIdx];
            var sub = RaLanguage.Interpreter.Runtime.Async.SyncAwait.Get(
                Visitors.Annotations.AnnotationApplicationNodeVisitor.Apply(node, ctx, _interpreter));
            if (sub.Error != null) throw new RaUserError(sub.Error);
            return sub.Value ?? NullValue.Null;
        }

        // L8 — one `for await` pull step. Mirrors ForAwaitNodeVisitor: honour
        // cancellation, pull the next item (blocking on the cross-thread channel),
        // set itemSlot + continueSlot (false when closed / done). Synchronous.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpForAwaitPull(LocalsView locals, byte itemSlot, byte continueSlot,
            RaLanguage.Interpreter.Values.Async.AsyncStreamValue astream, Context ctx)
        {
            var token = ctx.AsyncCtx?.Token ?? System.Threading.CancellationToken.None;
            if (token.IsCancellationRequested)
            {
                astream.Core.Cancel();
                throw new RaUserError(MakeIcError(ctx, "for-await cancelled"));
            }
            var (ok, value, closed, err) = astream.Core.PullNext(token);
            if (err != null) throw new RaUserError(err);
            if (closed || !ok)
            {
                locals[continueSlot] = BooleanValue.False;
            }
            else
            {
                locals[itemSlot] = value ?? NullValue.Null;
                locals[continueSlot] = BooleanValue.True;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpEnumTagEq(LocalsView locals, uint instr, string[] names, Context ctx)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            string name = names[Encoding.C(instr)];
            var sv = locals[b];
            bool matched;
            if (sv is EnumValue ev)
                // Inferred enum variant: match on the member name.
                matched = string.Equals(ev.MemberName, name, System.StringComparison.Ordinal);
            else if (sv is RaLanguage.Interpreter.Values.Records.RecordInstanceValue rec)
                // Record-positional: NOMINAL identity — the pattern name must
                // resolve to the SAME RecordTypeValue as the instance's
                // Definition (mirrors the visitor's ReferenceEquals check).
                matched = ctx.SymbolTable.Get(name) is RaLanguage.Interpreter.Values.Records.RecordTypeValue rt
                    && ReferenceEquals(rt, rec.Definition);
            else
                matched = false;
            locals[a] = BooleanValue.Of(matched);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpEnumPayload(LocalsView locals, uint instr, Context ctx)
        {
            byte a = Encoding.A(instr);
            byte b = Encoding.B(instr);
            int idx = Encoding.C(instr);
            var sv = locals[b];
            if (sv is EnumValue ev)
            {
                locals[a] = idx < ev.Payload.Count ? ev.Payload[idx] : NullValue.Null;
            }
            else if (sv is RaLanguage.Interpreter.Values.Records.RecordInstanceValue rec)
            {
                // Record-positional: extract the i-th primary field BY NAME
                // (the visitor reads PrimaryFields[i].NameTok then GetField).
                var pf = rec.Definition.PrimaryFields;
                if (idx < pf.Count)
                {
                    string fname = pf[idx].NameTok.Value?.ToString() ?? "";
                    locals[a] = rec.HasField(fname) ? rec.GetField(fname) : (RuntimeValue)NullValue.Null;
                }
                else locals[a] = NullValue.Null;
            }
            else if (sv is TupleValue tup)
            {
                // Tuple-positional element (the shape check confirmed arity).
                locals[a] = idx < tup.Elements.Count ? tup.Elements[idx] : NullValue.Null;
            }
            else if (sv is ListValue lst)
            {
                // List front element (the shape check confirmed enough length).
                locals[a] = idx < lst.Elements.Count ? lst.Elements[idx] : NullValue.Null;
            }
            else
            {
                throw new RaUserError(MakeIcError(ctx, "VM: EnumPayload on non-enum/record/tuple/list value"));
            }
        }

        // L7 — variant/record arity guard. Reached only after a passing
        // EnumTagEq, so the scrutinee IS the matched variant/record. Throws the
        // visitor's EXACT arity-mismatch message (parity captures err.Details) so
        // a wrong-arity pattern (`case Point(only_one)`) errors identically
        // whether lowered or not; nop when the arity matches.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OpMatchArity(LocalsView locals, uint instr, Context ctx)
        {
            byte b = Encoding.B(instr);
            int subCount = Encoding.C(instr);
            var sv = locals[b];
            if (sv is EnumValue ev)
            {
                if (ev.Payload.Count != subCount)
                    throw new RaUserError(MakeIcError(ctx,
                        $"variant '{ev.EnumName}.{ev.MemberName}' carries {ev.Payload.Count} value(s), pattern destructures {subCount}"));
            }
            else if (sv is RaLanguage.Interpreter.Values.Records.RecordInstanceValue rec)
            {
                int fc = rec.Definition.PrimaryFields.Count;
                if (fc != subCount)
                    throw new RaUserError(MakeIcError(ctx,
                        $"record '{rec.Definition.StructName}' has {fc} primary field(s), pattern destructures {subCount}"));
            }
        }
    }
}

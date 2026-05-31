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
                                    VmFrame.Return(prevFrame);
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
                        if (sv is not RaLanguage.Interpreter.Values.Streams.StreamValue stream)
                            throw new RaUserError(MakeIcError(ctx, "ForEachStreamPull: source slot is not a Stream"));
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
                        if (faultPc >= h.StartPc && faultPc < h.EndPc && h.CatchPc >= 0)
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
            Mark(Opcode.NullCoal);
            // Strings.
            Mark(Opcode.StrConcat); Mark(Opcode.Interp); Mark(Opcode.Fmt);
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
    }
}

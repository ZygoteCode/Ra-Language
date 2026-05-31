using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Vm
{
    // One per active VM invocation. Lives on the C# stack of the dispatch
    // loop; survives `await`s thanks to the async state machine snapshotting
    // it. See RA_VM_MIGRATION.md §3.5.
    //
    // Allocations per frame (when unpooled):
    //   - the VmFrame instance itself (heap-class for null-coalescing
    //     convenience and reference-stable identity across awaits)
    //   - Slots array sized to RaFunction.LocalCount (M75 unification —
    //     formerly two parallel arrays `Locals[]` + `Slots[]`)
    //   - SlotLocals array sized to RaFunction.SlotCount
    //   - Upvalues array (caller-provided, not allocated per frame)
    //
    // M79: per-RaFunction pool. Each `RaFunction` carries a
    // `ConcurrentStack<VmFrame>` capped at `PoolDepth` instances.
    // `Rent` pops a frame and reseats it (clearing Slots / SlotLocals
    // in place); `Return` clears references and pushes back if the
    // pool has room. Per-function pools guarantee correct array sizes
    // without bucket logic — the Slots / SlotLocals arrays stay
    // attached to the frame across reuse cycles and the GC only sees
    // one allocation per cold call.
    //
    // Return discipline: callers MUST only `Return` a frame when no
    // external reference can outlive the call. The current call sites
    // return on the SUCCESS path only (no error escape), so error
    // back-traces that capture `Parent` chains still find a live frame.
    public sealed class VmFrame
    {
        // Per-function pool depth. Matches RaTaskCore's 256-cap design
        // scaled down — most Ra programs don't have more than a few
        // concurrent calls of the same function in flight.
        internal const int PoolDepth = 4;

        public RaFunction Function;
        public RuntimeValue?[] Upvalues;

        // M71+M75: tagged-union slot storage — the SOLE per-frame value
        // store. One `ValueSlot` per local. Each slot's `Tag`
        // discriminator covers Null / Bool / Int64 / Float64 / Ref.
        //
        // Invariant: when `Slots[i].Tag != Ref`, the canonical value
        // for slot `i` lives in `Slots[i].Bits`. When `Slots[i].Tag
        // == Ref` (the default), `Slots[i].Ref` is the canonical
        // boxed RuntimeValue. The dispatch-loop `s_writesLocalsA`
        // pre-clear resets `Tag = Ref` on every non-II writer so a
        // previously-unboxed slot can be reused by boxed code
        // without aliasing.
        //
        // M75: always allocated when LocalCount > 0. The previous
        // `UsesUnboxedSlots` gate is gone — the slot array is now
        // the only physical store for locals (no parallel boxed
        // `Locals[]` array exists). Boxed-only functions still
        // pay only one array allocation per call (same as before
        // M71 when only `Locals[]` existed).
        public ValueSlot[] Slots;

        // Per-frame SymbolEntry slot table (M14). Indexed by Resolver
        // BindingId.Offset. Populated by OP_DECLARE_LOCAL when the IR compiler
        // recorded a slot for the declaration; consulted by OP_LOAD_LOCAL_S /
        // OP_STORE_LOCAL_S to bypass ctx.SymbolTable.GetEntry. Entries persist
        // across PushScope/PopScope because slots are frame-scoped, not
        // SymbolTable-scoped. Re-declaration via a fresh OP_DECLARE_LOCAL
        // overwrites the slot, so loop bodies that re-execute decls each
        // iteration stay correct.
        public SymbolEntry?[] SlotLocals;

        // PERF (O(n) string building): per-loop-string-accumulator
        // StringBuilders, indexed by the StrAcc* opcodes' imm16. Allocated only
        // when Function.StrAccCount > 0 (the common case allocates nothing).
        public System.Text.StringBuilder?[] StrAcc;

        public int Pc;

        // Runtime ctx depth — maintained by Push/Pop scope opcodes. The
        // exception-handler scan compares this to ExceptionHandler.ScopeDepth
        // when restoring ctx for a catch entry.
        public int CtxDepth;

        // Optional parent for traceback reconstruction. Null at the script
        // root frame.
        public VmFrame? Parent;

        public VmFrame(RaFunction function, RuntimeValue?[]? upvalues = null, VmFrame? parent = null)
        {
            Function = function;
            Upvalues = upvalues ?? System.Array.Empty<RuntimeValue?>();
            SlotLocals = function.SlotCount > 0
                ? new SymbolEntry?[function.SlotCount]
                : System.Array.Empty<SymbolEntry?>();
            // M75: tagged-union slot storage is now the SOLE physical
            // store for locals.
            Slots = function.LocalCount > 0
                ? new ValueSlot[function.LocalCount]
                : System.Array.Empty<ValueSlot>();
            StrAcc = function.StrAccCount > 0
                ? new System.Text.StringBuilder?[function.StrAccCount]
                : System.Array.Empty<System.Text.StringBuilder?>();
            Parent = parent;
            Pc = 0;
            CtxDepth = 0;
        }

        // M79: rent a frame from the per-function pool, or allocate
        // fresh on miss. Resets Slots / SlotLocals / Pc / CtxDepth
        // and re-attaches the caller-supplied `upvalues` and `parent`.
        // The Slots / SlotLocals arrays stay attached to the frame
        // across reuse cycles when their sizes still match the
        // function — saving the two array allocations per call.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VmFrame Rent(RaFunction function, RuntimeValue?[]? upvalues = null, VmFrame? parent = null)
        {
            var pool = function._framePool;
            if (pool != null && pool.TryPop(out var f) && f != null)
            {
                f.ResetForFunction(function, upvalues, parent);
                return f;
            }
            return new VmFrame(function, upvalues, parent);
        }

        // M79: return a frame to its function's pool. Clears references
        // (Slots / SlotLocals / Upvalues / Parent) so the GC can
        // reclaim collected boxed values held by Ref-tagged slots and
        // by SymbolEntry caches. Pushes onto the pool only if the
        // depth cap isn't reached — beyond that, drop on the floor
        // and let the GC reclaim.
        //
        // Safety contract: the caller guarantees no external reference
        // outlives this Return — typically by returning only on the
        // SUCCESS path (error-escape paths capture Parent for the
        // traceback chain and must NOT pool the frame).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Return(VmFrame frame)
        {
            if (frame == null) return;
            var fn = frame.Function;
            if (fn == null) return;
            if (frame.Slots.Length > 0)
                System.Array.Clear(frame.Slots, 0, frame.Slots.Length);
            if (frame.SlotLocals.Length > 0)
                System.Array.Clear(frame.SlotLocals, 0, frame.SlotLocals.Length);
            if (frame.StrAcc.Length > 0)
                System.Array.Clear(frame.StrAcc, 0, frame.StrAcc.Length);
            frame.Upvalues = System.Array.Empty<RuntimeValue?>();
            frame.Parent = null;
            frame.Pc = 0;
            frame.CtxDepth = 0;
            var pool = fn._framePool;
            if (pool == null)
            {
                pool = new ConcurrentStack<VmFrame>();
                // Race-tolerant — if multiple threads init concurrently,
                // the loser's pool is GC'd; the winner serves
                // subsequent rents.
                System.Threading.Interlocked.CompareExchange(ref fn._framePool, pool, null);
                pool = fn._framePool!;
            }
            if (pool.Count < PoolDepth) pool.Push(frame);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ResetForFunction(RaFunction function, RuntimeValue?[]? upvalues, VmFrame? parent)
        {
            Function = function;
            Upvalues = upvalues ?? System.Array.Empty<RuntimeValue?>();
            if (SlotLocals.Length != function.SlotCount)
            {
                SlotLocals = function.SlotCount > 0
                    ? new SymbolEntry?[function.SlotCount]
                    : System.Array.Empty<SymbolEntry?>();
            }
            else if (SlotLocals.Length > 0)
            {
                System.Array.Clear(SlotLocals, 0, SlotLocals.Length);
            }
            if (Slots.Length != function.LocalCount)
            {
                Slots = function.LocalCount > 0
                    ? new ValueSlot[function.LocalCount]
                    : System.Array.Empty<ValueSlot>();
            }
            else if (Slots.Length > 0)
            {
                System.Array.Clear(Slots, 0, Slots.Length);
            }
            if (StrAcc.Length != function.StrAccCount)
            {
                StrAcc = function.StrAccCount > 0
                    ? new System.Text.StringBuilder?[function.StrAccCount]
                    : System.Array.Empty<System.Text.StringBuilder?>();
            }
            else if (StrAcc.Length > 0)
            {
                System.Array.Clear(StrAcc, 0, StrAcc.Length);
            }
            Parent = parent;
            Pc = 0;
            CtxDepth = 0;
        }
    }
}

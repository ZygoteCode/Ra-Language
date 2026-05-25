using System.Runtime.CompilerServices;
using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Vm
{
    // M75 — Locals[] full unification into Slots[].
    //
    // Thin readonly struct that exposes the legacy `RuntimeValue?[] locals`
    // syntactic surface (indexer `locals[i]`) on top of the canonical
    // `ValueSlot[]` storage. Lets the dispatch-loop / opcode handlers keep
    // `locals[a]` everywhere while the underlying physical store is the
    // tagged-union Slots[] array introduced by M71.
    //
    // Setter contract: writes `Tag = Ref; Ref = value` — i.e. promotes
    // the slot to canonical-boxed shape. This matches the dispatch-loop
    // pre-clear bitmap (s_writesLocalsA) which already maintains
    // `Tag = Ref` for non-II writers, so the redundant tag write is a
    // cache-warm no-op for the common case AND a corrective write for
    // helper paths (Deopt*) that are invoked from II opcodes which the
    // pre-clear intentionally skips.
    //
    // Getter contract: assumes Tag == Ref (boxed canonical). Reads
    // `Slots[i].Ref` cast to RuntimeValue. Callers that may face a
    // typed-tag slot must invoke `EnsureBoxed` first — the same
    // invariant the legacy parallel-array layout had under M71.
    //
    // EnsureBoxed (in VmExecutor) writes directly to
    // `frame.Slots[slot].Ref` (bypassing this view) so the boxed mirror
    // is cached without clobbering the Int64/Float64 Tag — keeping the
    // dual-rep property that lets II/FF readers continue without
    // deopting after a single boxed observation of the slot.
    public readonly struct LocalsView
    {
        private readonly ValueSlot[] _slots;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public LocalsView(ValueSlot[] slots) { _slots = slots; }

        public RuntimeValue? this[int i]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ref var s = ref _slots[i];
                // Fast path: Tag == Ref. Ref holds the canonical boxed
                // RuntimeValue (possibly null). The vast majority of
                // boxed opcode reads land here because the pre-clear
                // bitmap guarantees Tag==Ref at handler entry for
                // every non-II writer's instruction.A, and the IR
                // rewriter only promotes a slot to Int64/Float64/Bool
                // tag when the whole producer→consumer chain is
                // typed.
                if (s.Tag == ValueSlotTag.Ref) return s.Ref as RuntimeValue;
                // Cold path: a typed-tag slot is being read by boxed
                // code (deopt / mixed-type fallback). Box on demand
                // via the ValueSlot.ToRuntimeValue() bridge — the
                // same materialisation EnsureBoxed performs, just
                // inline-cached here instead of writing back into
                // the slot. The deopt path that follows will write
                // its own result on top, so caching back would burn
                // a redundant write.
                return s.ToRuntimeValue();
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                ref var s = ref _slots[i];
                s.Tag = ValueSlotTag.Ref;
                s.Ref = value;
            }
        }

        // Exposed for the rare helper that needs the underlying
        // tagged-union storage (EnsureBoxed dual-rep cache write,
        // typed-tag deopt diagnostics, etc).
        public ValueSlot[] Slots
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _slots;
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _slots.Length;
        }
    }
}

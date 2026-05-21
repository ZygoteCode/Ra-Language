using System;
using System.Runtime.CompilerServices;

namespace RaLanguage.Interpreter.Pipeline
{
    // Compact 32-bit handle that names a single binding site in the resolved AST.
    //
    //   Raw layout (audit spec — keep stable):
    //     bits 31..16  frame_id  (which function/script frame owns the slot)
    //     bits 15..0   offset    (slot index within that frame, 0..N-1)
    //
    // A BindingId is produced by the Resolver pass after parsing; runtime visitors
    // can use it to bypass dictionary lookups, closure-capture analysers can build
    // the upvalue table, and tooling can implement go-to-definition by mapping a
    // BindingId back to the declaration site stored in the resolver's frame
    // registry. The Kind is intentionally NOT packed inside the 32-bit Raw — it is
    // stored alongside (see ResolvedBinding) so the bit layout is the simple
    // (frame_id<<16) | offset the audit asks for.
    public readonly struct BindingId : IEquatable<BindingId>
    {
        public readonly int Raw;

        public BindingId(int raw) { Raw = raw; }

        public BindingId(int frameId, int offset)
        {
            if ((uint)frameId > 0xFFFF)
                throw new ArgumentOutOfRangeException(nameof(frameId), $"frame id {frameId} exceeds 16-bit range");
            if ((uint)offset > 0xFFFF)
                throw new ArgumentOutOfRangeException(nameof(offset), $"slot offset {offset} exceeds 16-bit range");
            Raw = (frameId << 16) | (offset & 0xFFFF);
        }

        public int FrameId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw >> 16) & 0xFFFF;
        }

        public int Offset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Raw & 0xFFFF;
        }

        // Sentinel value emitted by the Resolver for identifiers it could not bind
        // statically (modules' lazy exports, names that only exist at runtime via
        // reflection, etc.). Visitors must fall back to the dictionary-based path
        // when IsResolved is false.
        public static readonly BindingId Unresolved = new BindingId(unchecked((int)0xFFFFFFFF));

        public bool IsResolved
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Raw != Unresolved.Raw;
        }

        public bool Equals(BindingId other) => Raw == other.Raw;
        public override bool Equals(object? obj) => obj is BindingId b && Equals(b);
        public override int GetHashCode() => Raw;
        public override string ToString() => IsResolved ? $"#{FrameId}:{Offset}" : "#unresolved";

        public static bool operator ==(BindingId a, BindingId b) => a.Raw == b.Raw;
        public static bool operator !=(BindingId a, BindingId b) => a.Raw != b.Raw;
    }

    // Classifies a resolved binding so visitors and downstream passes (closure
    // builder, LSP) can pick the right storage strategy without re-walking the
    // resolver's frame chain.
    public enum BindingKind : byte
    {
        // Resolver could not bind statically; runtime must do a dictionary lookup.
        // This covers imported symbols, dynamic builtins discovered after the pass,
        // and anything declared in a code path the resolver intentionally skipped.
        Unresolved = 0,

        // Slot lives in the current function/script frame. Reads/writes are direct
        // slot accesses on the frame's local table.
        Local,

        // Slot lives in an enclosing function frame and was referenced from a
        // nested function: it must be materialised in the inner closure's capture
        // record. The BindingId.FrameId names the OUTER frame; the resolver also
        // records the upvalue index inside that closure's capture list.
        Capture,

        // Top-level script/module binding. Resolves through the module's symbol
        // table.
        Global,

        // Built-in registered in BuiltinSymbolTable (print, channel, etc.). Stable
        // for the lifetime of the process — never invalidated by user code.
        Builtin,

        // Function parameter. Treated like Local at runtime but tagged separately
        // so closure-capture analysis can distinguish parameter borrowing from
        // local-let borrowing.
        Parameter,

        // `self` inside a class/struct method body. Bound to a synthetic slot at
        // the method frame's offset 0.
        SelfRef,
    }
}

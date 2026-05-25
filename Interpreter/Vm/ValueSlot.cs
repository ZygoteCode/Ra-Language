using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Vm
{
    // ValueSlot — canonical tagged-union representation for the Ra VM
    // dispatch loop. One per local / temporary in a `VmFrame`. After
    // M75 the sole physical store for locals (the legacy parallel
    // `RuntimeValue?[] Locals` array is gone).
    //
    // Pinned layout (M75 finalisation — `LayoutKind.Explicit`, 24 bytes):
    //
    //   offset 0  : Tag  (ValueSlotTag, byte) — discriminator
    //   offset 8  : Bits (long)               — primitive payload
    //                                          (int64 / bool-as-0/1 /
    //                                           double-via-reinterpret)
    //   offset 16 : Ref  (object?)            — heap-side payload
    //                                          (RuntimeValue for any
    //                                           non-unboxed type; doubles
    //                                           as the boxed mirror
    //                                           cache when the slot is
    //                                           a typed-tag slot whose
    //                                           value has been observed
    //                                           via EnsureBoxed)
    //   total     : 24 bytes (single L1 cache line — three fields fit
    //                         in the first 64-byte sector with room to
    //                         spare for the next slot's Tag)
    //
    // Why pin via `LayoutKind.Explicit` instead of leaving the layout
    // to the runtime?
    //
    //   1. RyuJIT + NativeAOT both honour `[FieldOffset]`, so the
    //      byte positions are stable across configurations and
    //      reordering optimisations the runtime applies to default
    //      `Sequential` layout no longer affect us.
    //   2. The VM dispatch loop reads / writes the same three byte
    //      offsets thousands of times per instruction. A pinned
    //      layout lets the JIT collapse repeated `&f.Slots[i].X`
    //      into a single base-plus-offset addressing mode
    //      (`mov rax, [rdx + idx*24 + 8]`) rather than recomputing
    //      the element pointer per field touch.
    //   3. Stable offsets unlock future tier-up native codegen
    //      (M57 JIT scaffold) emitting direct loads with constant
    //      displacement without any layout discovery cost.
    //   4. The SSA / SCCP / GVN passes can model the three fields
    //      as independent scalar SSA values per PC because the
    //      runtime cannot legally reshuffle them.
    //   5. Whole-slot copies (`slot = new ValueSlot { ... }`) lower
    //      to a 24-byte block move the JIT may emit as 3 quadword
    //      stores or a fused 16-byte SIMD store + 8-byte tail.
    //
    // Why `LayoutKind.Explicit` rather than `Sequential, Pack=8`?
    // Both pin the field order today, but `Explicit` is the only
    // attribute the runtime treats as a hard contract — `Sequential`
    // leaves the door open for future reordering optimisations. The
    // managed `object?` field is laid out at a fixed 8-byte-aligned
    // offset (16) and never overlaps the primitive payload — the
    // GC tracks its precise offset at type load, exactly as it
    // would under `Sequential`.
    //
    // `Tag` is the single source of truth for which payload the slot
    // holds:
    //
    //   Null    — slot is the `null`/None value. Both payload fields
    //             are ignored. `Ref` typically points at the cached
    //             `NullValue.Null` sentinel so legacy readers see a
    //             non-null `RuntimeValue` without an allocation.
    //   Bool    — `Bits & 1` is the boolean value (0/1). `Ref` may
    //             cache the corresponding `BooleanValue.Of(...)`
    //             singleton from a prior boxed read.
    //   Int64   — `Bits` is a signed 64-bit integer. `Ref` may
    //             cache a `NumberValue.OfBigNumber(...)` boxed
    //             mirror after `EnsureBoxed`.
    //   Float64 — `Bits` reinterpret-cast to double via
    //             `BitConverter.Int64BitsToDouble(Bits)`. `Ref`
    //             may cache a `DoubleValue.OfDouble(...)` boxed
    //             mirror after `EnsureBoxed`.
    //   Ref     — `Ref` is the canonical boxed RuntimeValue (the
    //             legacy representation). Used for every type the
    //             unboxed tags don't cover (String, collections,
    //             instances, functions, tasks, big-numbers,
    //             typed-primitive wrappers like `IntegerValue` /
    //             `LongValue` / `FloatValue` / `DoubleValue` /
    //             `DecimalValue` / ...). `Bits` is undefined in
    //             this state.
    //
    // The unboxed tags are an OPTIMISATION: any value can always be
    // expressed as `Ref` pointing at the corresponding RuntimeValue
    // subclass. The IR rewriter selects the most specific tag for
    // each opcode's def-use chain (M66.5 chain analyzer); the dispatch
    // loop maintains the invariant via `EnsureBoxed` / `TryReadAsLong` /
    // `TryReadAsDouble` / `TryReadAsBool`.
    //
    // Register-allocation properties (Windows x64 ABI):
    //
    //   - 24-byte structs are passed BY HIDDEN POINTER, so helper
    //     methods that accept a `ValueSlot` by value will spill
    //     to the stack. Hot paths therefore take a `ref ValueSlot`
    //     (or address into the `Slots[]` array directly) — the
    //     `LocalsView` indexer and the II/FF/BB/ExtII handlers
    //     uniformly hoist `ref var sa = ref f.Slots[a]` so the
    //     element address is computed once and the three field
    //     stores share a base register.
    //   - Reading `Tag` is a single byte load (`movzx`); JIT will
    //     keep it in a 32-bit register for the branch compare.
    //   - Reading `Bits` is a single qword load aligned to 8 —
    //     no partial-register stalls.
    //   - Reading `Ref` is a single qword load aligned to 8 —
    //     followed by an `as RuntimeValue` cast that JIT inlines
    //     to a type-check + branch.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct ValueSlot
    {
        [FieldOffset(0)]
        public ValueSlotTag Tag;

        [FieldOffset(8)]
        public long Bits;

        [FieldOffset(16)]
        public object? Ref;

        // -------- Constructors / static factories -----------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueSlot Null() => new ValueSlot { Tag = ValueSlotTag.Null, Bits = 0, Ref = null };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueSlot OfBool(bool v) => new ValueSlot { Tag = ValueSlotTag.Bool, Bits = v ? 1 : 0, Ref = null };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueSlot OfInt64(long v) => new ValueSlot { Tag = ValueSlotTag.Int64, Bits = v, Ref = null };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueSlot OfFloat64(double v) => new ValueSlot { Tag = ValueSlotTag.Float64, Bits = BitConverter.DoubleToInt64Bits(v), Ref = null };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueSlot OfRef(RuntimeValue? r) => new ValueSlot { Tag = ValueSlotTag.Ref, Bits = 0, Ref = r };

        // -------- Tag predicates ----------------------------------

        public bool IsNull => Tag == ValueSlotTag.Null;
        public bool IsInt64 => Tag == ValueSlotTag.Int64;
        public bool IsFloat64 => Tag == ValueSlotTag.Float64;
        public bool IsBool => Tag == ValueSlotTag.Bool;
        public bool IsRef => Tag == ValueSlotTag.Ref;

        // -------- Unboxed accessors -------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long AsInt64() => Bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double AsFloat64() => BitConverter.Int64BitsToDouble(Bits);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AsBool() => (Bits & 1) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RuntimeValue? AsRef() => Ref as RuntimeValue;

        // -------- Boxed-bridge -----------------------------------
        //
        // `ToRuntimeValue` materialises the slot as a heap RuntimeValue.
        // For Int64/Float64/Bool/Null the result is freshly allocated
        // (or pulled from the intern cache for small ints / shared
        // BooleanValue / NullValue singletons). For Ref it returns the
        // already-boxed value.
        //
        // Used at every legacy boundary: passing arguments to the
        // virtual `RuntimeValue` operator overloads, function-call
        // argument lists, builtin interop, error messages, and SCCP
        // const-pool entries.
        public RuntimeValue ToRuntimeValue()
        {
            switch (Tag)
            {
                case ValueSlotTag.Null: return NullValue.Null;
                case ValueSlotTag.Bool: return BooleanValue.Of((Bits & 1) != 0);
                case ValueSlotTag.Int64:
                    return NumberValue.OfBigNumber(new BigNumber(new System.Numerics.BigInteger(Bits), System.Numerics.BigInteger.Zero));
                case ValueSlotTag.Float64:
                    return DoubleValue.OfDouble(BitConverter.Int64BitsToDouble(Bits));
                case ValueSlotTag.Ref:
                    return (RuntimeValue)(Ref ?? NullValue.Null);
                default:
                    return NullValue.Null;
            }
        }

        // Inverse: classify a heap RuntimeValue into the most specific
        // tagged-union shape. Boolean / Null fall to their compact
        // tag; small-int NumberValues fold into Int64; the rest stay
        // as Ref. Called by the IR compiler when it lowers a
        // constant to a slot, and by the runtime bridges
        // (`EnsureBoxed` companion `UnboxIfPossible`).
        public static ValueSlot FromRuntimeValue(RuntimeValue? v)
        {
            if (v == null) return Null();
            switch (v.Type)
            {
                case RuntimeValueType.Null:    return Null();
                case RuntimeValueType.Boolean: return OfBool(((BooleanValue)v).Value);
                // Specialised tags are skipped here on purpose: the
                // NumberValue / DoubleValue subclass dance has its
                // own intern-cache + scale semantics that we don't
                // want to perturb at the entry point. The IR
                // rewriter is the only producer of OfInt64 / OfFloat64
                // slots (driven by M66.5 chain analysis) — every
                // other path stays Ref.
                default: return OfRef(v);
            }
        }
    }

    public enum ValueSlotTag : byte
    {
        Null    = 0,
        Bool    = 1,
        Int64   = 2,
        Float64 = 3,
        Ref     = 4,
    }
}

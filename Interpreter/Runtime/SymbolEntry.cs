using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    // Sealed + plain fields + packed bool flags. Auto-properties expand to a
    // hidden backing field plus get/set methods; the JIT inlines them in
    // Release but the IL is still larger and AOT trims worse. Fields are
    // a direct memory access on every read. Booleans are also packed into a
    // single byte bitfield (`_flags`) — the seven bools used to take seven
    // bytes plus padding; now they share one byte plus two ints for borrow
    // tracking. With StructLayout.Auto the runtime is free to order fields
    // for tightest packing.
    //
    // Borrow / ownership semantics (preserved verbatim from the old class):
    //   IsMutable        — assignment allowed through the binding. var is always
    //                      mutable; let mut is mutable; let / let const are not.
    //   IsConstBinding   — strongest immutability: cannot be reassigned, cannot
    //                      be borrowed mutably, cannot be moved out. Set for
    //                      `const` and `let const`.
    //   SharedBorrowCount — number of live `&entry` borrows. Mutation and `&mut`
    //                      are blocked while > 0.
    //   HasMutableBorrow  — true while a single `&mut entry` borrow is alive.
    //                      Blocks any other borrow and any direct mutation/read.
    //   IsBorrowed        — fast convenience: shared count > 0 OR mut borrow alive.
    [StructLayout(LayoutKind.Auto)]
    public sealed class SymbolEntry
    {
        public RuntimeValue Value;
        public TypeDescriptor? DeclaredType;
        public VariableDeclarationType DeclarationType;
        public int SharedBorrowCount;

        // Packed bool flags. Saves ~7 bytes per entry; with thousands of bindings
        // per script that's a non-trivial cache-pressure win.
        private byte _flags;
        private const byte F_IsLet            = 1 << 0;
        private const byte F_IsMoved          = 1 << 1;
        private const byte F_IsPublic         = 1 << 2;
        private const byte F_IsStaticallyTyped = 1 << 3;
        private const byte F_IsMutable        = 1 << 4;
        private const byte F_IsConstBinding   = 1 << 5;
        private const byte F_HasMutableBorrow = 1 << 6;

        public bool IsLet
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (_flags & F_IsLet) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _flags = (byte)(value ? (_flags | F_IsLet) : (_flags & ~F_IsLet));
        }
        public bool IsMoved
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (_flags & F_IsMoved) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _flags = (byte)(value ? (_flags | F_IsMoved) : (_flags & ~F_IsMoved));
        }
        public bool IsPublic
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (_flags & F_IsPublic) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _flags = (byte)(value ? (_flags | F_IsPublic) : (_flags & ~F_IsPublic));
        }
        public bool IsStaticallyTyped
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (_flags & F_IsStaticallyTyped) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _flags = (byte)(value ? (_flags | F_IsStaticallyTyped) : (_flags & ~F_IsStaticallyTyped));
        }
        public bool IsMutable
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (_flags & F_IsMutable) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _flags = (byte)(value ? (_flags | F_IsMutable) : (_flags & ~F_IsMutable));
        }
        public bool IsConstBinding
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (_flags & F_IsConstBinding) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _flags = (byte)(value ? (_flags | F_IsConstBinding) : (_flags & ~F_IsConstBinding));
        }
        public bool HasMutableBorrow
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (_flags & F_HasMutableBorrow) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _flags = (byte)(value ? (_flags | F_HasMutableBorrow) : (_flags & ~F_HasMutableBorrow));
        }

        public bool IsBorrowed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => SharedBorrowCount > 0 || (_flags & F_HasMutableBorrow) != 0;
        }

        public SymbolEntry(
            RuntimeValue value,
            bool isLet = false,
            bool isPublic = true,
            TypeDescriptor? declaredType = null,
            bool isStaticallyTyped = false,
            VariableDeclarationType declarationType = VariableDeclarationType.VARIABLE)
        {
            Value = value;
            DeclaredType = declaredType;
            DeclarationType = declarationType;
            byte f = 0;
            if (isLet) f |= F_IsLet;
            if (isPublic) f |= F_IsPublic;
            if (isStaticallyTyped) f |= F_IsStaticallyTyped;
            _flags = f;
            ApplyDeclarationTypeDefaults();
        }

        public SymbolEntry(RuntimeValue value, bool isLet, TypeDescriptor? declaredType, bool isStaticallyTyped)
            : this(value, isLet)
        {
            DeclaredType = declaredType;
            IsStaticallyTyped = isStaticallyTyped;
        }

        // Centralises the mapping from DeclarationType to the IsMutable / IsConstBinding
        // flags. Visitors should call this whenever they mutate DeclarationType so the
        // derived flags do not drift out of sync.
        public void ApplyDeclarationTypeDefaults()
        {
            switch (DeclarationType)
            {
                case VariableDeclarationType.CONST:
                    IsMutable = false;
                    IsConstBinding = true;
                    break;
                case VariableDeclarationType.FINAL:
                    IsMutable = false;
                    IsConstBinding = false;
                    break;
                case VariableDeclarationType.LET:
                    IsMutable = false;
                    IsConstBinding = false;
                    break;
                case VariableDeclarationType.LET_MUT:
                    IsMutable = true;
                    IsConstBinding = false;
                    break;
                case VariableDeclarationType.LET_CONST:
                    IsMutable = false;
                    IsConstBinding = true;
                    break;
                case VariableDeclarationType.VARIABLE:
                default:
                    IsMutable = true;
                    IsConstBinding = false;
                    break;
            }
        }

        public void ClearReference()
        {
            Value = null!;
            DeclaredType = null;
            DeclarationType = VariableDeclarationType.VARIABLE;
            SharedBorrowCount = 0;
            // Reset flags then set IsMutable=true to match VARIABLE defaults.
            _flags = F_IsMutable;
        }
    }
}

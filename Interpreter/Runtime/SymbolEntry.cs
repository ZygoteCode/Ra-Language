using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolEntry
    {
        public RuntimeValue Value { get; set; }
        public bool IsLet { get; set; }
        public bool IsMoved { get; set; }
        public bool IsPublic { get; set; }
        public bool IsStaticallyTyped { get; set; }
        public TypeDescriptor? DeclaredType { get; set; }
        public VariableDeclarationType DeclarationType { get; set; } = VariableDeclarationType.VARIABLE;

        // Borrow / ownership tracking. These fields are populated by `let` / `let mut`
        // / `let const` declarations and consulted by the borrow-aware visitors
        // (BorrowNodeVisitor, DereferenceNodeVisitor, VariableAssignmentNodeVisitor,
        // VariableAccessNodeVisitor) plus the static BorrowChecker pass.
        //
        // Semantics:
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
        public bool IsMutable { get; set; }
        public bool IsConstBinding { get; set; }
        public int SharedBorrowCount { get; set; }
        public bool HasMutableBorrow { get; set; }
        public bool IsBorrowed => SharedBorrowCount > 0 || HasMutableBorrow;

        public SymbolEntry(
            RuntimeValue value,
            bool isLet = false,
            bool isPublic = true,
            TypeDescriptor? declaredType = null,
            bool isStaticallyTyped = false,
            VariableDeclarationType declarationType = VariableDeclarationType.VARIABLE)
        {
            Value = value;
            IsLet = isLet;
            IsMoved = false;
            IsPublic = isPublic;
            DeclaredType = declaredType;
            IsStaticallyTyped = isStaticallyTyped;
            DeclarationType = declarationType;
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
                    // final is "assign once". Once initialised the visitor flips it
                    // to immutable; until then we treat it as still-uninitialised.
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
            Value = null;
            IsLet = false;
            IsMoved = false;
            DeclaredType = null;
            IsStaticallyTyped = false;
            DeclarationType = VariableDeclarationType.VARIABLE;
            IsMutable = true;
            IsConstBinding = false;
            SharedBorrowCount = 0;
            HasMutableBorrow = false;
        }
    }
}

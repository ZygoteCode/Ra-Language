using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared borrow-place logic for `&place` / `&mut place`, factored out of
    // BorrowNodeVisitor so the IR-lowered OP_BORROW / OP_BORROW_MUT handlers
    // and the (transitional) visitor fallback run byte-identical rules.
    //
    // The target is always a bare variable (the grammar rejects member / index
    // borrows here — those go through the IReferenceValue family), so this takes
    // a resolved `name` rather than an AstNode. No sub-expression evaluation
    // happens, so it is fully synchronous.
    public static class BorrowOps
    {
        public static ValueResult TryBorrow(
            Context context, string name, bool isMutable, string? lifetime,
            Position posStart, Position posEnd)
        {
            if (string.IsNullOrEmpty(name))
                return (null, new RuntimeError(posStart, posEnd,
                    "invalid borrow target", context,
                    code: DiagnosticCode.RuntimeBorrowViolation));

            var entry = context.SymbolTable.GetEntry(name);
            if (entry == null)
                return (null, new RuntimeError(posStart, posEnd,
                    $"'{name}' is not defined", context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such symbol in scope",
                    help: "declare the binding before borrowing it"));

            if (entry.IsMoved)
                return (null, new RuntimeError(posStart, posEnd,
                    $"cannot borrow '{name}': value was already moved", context,
                    code: DiagnosticCode.RuntimeMovedValue,
                    primaryLabel: "borrow of moved value",
                    help: "rebind the value, or take the borrow before the move"));

            // The entry can live in any ancestor scope; the BorrowValue needs
            // the exact owning table to walk back through.
            var ownerTable = FindOwnerTable(context.SymbolTable, name) ?? context.SymbolTable;

            if (isMutable)
            {
                if (entry.IsConstBinding)
                    return (null, new RuntimeError(posStart, posEnd,
                        $"cannot take '&mut {name}': '{name}' is a constant binding", context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "mutable borrow of const binding",
                        help: "use '&' for a read-only borrow, or declare the binding as 'let mut' / 'var'"));

                if (entry.DeclarationType == VariableDeclarationType.LET && !entry.IsMutable)
                    return (null, new RuntimeError(posStart, posEnd,
                        $"cannot take '&mut {name}': '{name}' is an immutable 'let' binding", context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "mutable borrow of immutable let",
                        help: "declare it as 'let mut' if you need to mutate through this binding"));

                if (entry.DeclarationType == VariableDeclarationType.FINAL)
                    return (null, new RuntimeError(posStart, posEnd,
                        $"cannot take '&mut {name}': '{name}' is 'final'", context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "mutable borrow of final binding",
                        help: "use 'var' or 'let mut' if you need to mutate through this binding"));

                if (entry.SharedBorrowCount > 0)
                    return (null, new RuntimeError(posStart, posEnd,
                        $"cannot take '&mut {name}': {entry.SharedBorrowCount} shared borrow(s) still alive", context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "mutable borrow while shared borrows live",
                        help: "wait until existing '&' borrows are released (their scope ends) before taking '&mut'"));

                if (entry.HasMutableBorrow)
                    return (null, new RuntimeError(posStart, posEnd,
                        $"cannot take '&mut {name}': another mutable borrow is already alive", context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "second mutable borrow",
                        help: "only one '&mut' may be live at a time"));

                entry.HasMutableBorrow = true;
            }
            else
            {
                if (entry.HasMutableBorrow)
                    return (null, new RuntimeError(posStart, posEnd,
                        $"cannot take '&{name}': a mutable borrow is already alive", context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "shared borrow while mutable borrow lives",
                        help: "shared borrows are allowed only after the existing '&mut' is released"));

                entry.SharedBorrowCount++;
            }

            var borrow = new BorrowValue(entry, ownerTable, name, isMutable, lifetime);
            borrow.SetContext(context).SetPos(posStart, posEnd);
            return (borrow, null);
        }

        private static SymbolTable? FindOwnerTable(SymbolTable start, string name)
        {
            SymbolTable? st = start;
            while (st != null)
            {
                if (st.GetLocalEntry(name) != null) return st;
                st = st.Parent;
            }
            return null;
        }
    }
}

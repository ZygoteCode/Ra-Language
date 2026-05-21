using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    // Runtime arm of the borrow grammar (`&place` / `&mut place`).
    //
    // Place resolution: the target must be a variable. Fields / indexed elements
    // are intentionally rejected at this layer — the existing IReferenceValue
    // family (ClassFieldReferenceValue, ListElementReferenceValue, ...) already
    // covers those cases for the var/auto/final/const interface. The borrow
    // interface is variable-scoped; future work can extend it.
    //
    // Rules enforced here (matched by the static BorrowChecker pass):
    //   * &mut a const binding (let const / const)        — rejected
    //   * &mut while any other borrow is live              — rejected
    //   * & while a &mut is live                           — rejected
    //   * & or &mut a moved binding                        — rejected
    public class BorrowNodeVisitor : NodeVisitor<BorrowNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(BorrowNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (node.Target.NodeType != AstNodeType.VariableAccess)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "borrow expression must target a variable",
                    context,
                    code: DiagnosticCode.RuntimeBorrowViolation,
                    primaryLabel: "not a borrowable place",
                    help: "use '&name' or '&mut name' on a let / let mut / var binding; member / index borrows are not yet supported"));

            var varAccess = (VariableAccessNode)node.Target;
            string name = varAccess.VarNameTok.Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(name))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "invalid borrow target",
                    context,
                    code: DiagnosticCode.RuntimeBorrowViolation));

            var entry = context.SymbolTable.GetEntry(name);
            if (entry == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"'{name}' is not defined",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such symbol in scope",
                    help: "declare the binding before borrowing it"));

            if (entry.IsMoved)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot borrow '{name}': value was already moved",
                    context,
                    code: DiagnosticCode.RuntimeMovedValue,
                    primaryLabel: "borrow of moved value",
                    help: "rebind the value, or take the borrow before the move"));

            // Find the actual SymbolTable that holds the entry so the BorrowValue can
            // safely walk back. This matters for nested scopes: GetEntry walks up the
            // parent chain, but the entry lives in exactly one of those tables.
            var ownerTable = FindOwnerTable(context.SymbolTable, name) ?? context.SymbolTable;

            if (node.IsMutable)
            {
                if (entry.IsConstBinding)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"cannot take '&mut {name}': '{name}' is a constant binding",
                        context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "mutable borrow of const binding",
                        help: "use '&' for a read-only borrow, or declare the binding as 'let mut' / 'var'"));

                if (entry.DeclarationType == VariableDeclarationType.LET && !entry.IsMutable)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"cannot take '&mut {name}': '{name}' is an immutable 'let' binding",
                        context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "mutable borrow of immutable let",
                        help: "declare it as 'let mut' if you need to mutate through this binding"));

                if (entry.DeclarationType == VariableDeclarationType.FINAL)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"cannot take '&mut {name}': '{name}' is 'final'",
                        context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "mutable borrow of final binding",
                        help: "use 'var' or 'let mut' if you need to mutate through this binding"));

                if (entry.SharedBorrowCount > 0)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"cannot take '&mut {name}': {entry.SharedBorrowCount} shared borrow(s) still alive",
                        context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "mutable borrow while shared borrows live",
                        help: "wait until existing '&' borrows are released (their scope ends) before taking '&mut'"));

                if (entry.HasMutableBorrow)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"cannot take '&mut {name}': another mutable borrow is already alive",
                        context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "second mutable borrow",
                        help: "only one '&mut' may be live at a time"));

                entry.HasMutableBorrow = true;
            }
            else
            {
                if (entry.HasMutableBorrow)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"cannot take '&{name}': a mutable borrow is already alive",
                        context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "shared borrow while mutable borrow lives",
                        help: "shared borrows are allowed only after the existing '&mut' is released"));

                entry.SharedBorrowCount++;
            }

            var borrow = new BorrowValue(entry, ownerTable, name, node.IsMutable, node.Lifetime);
            borrow.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
            return res.Success(borrow);
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

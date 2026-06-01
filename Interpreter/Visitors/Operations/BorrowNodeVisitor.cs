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
    // are intentionally rejected at this layer â€” the existing IReferenceValue
    // family (ClassFieldReferenceValue, ListElementReferenceValue, ...) already
    // covers those cases for the var/auto/final/const interface. The borrow
    // interface is variable-scoped; future work can extend it.
    //
    // Rules enforced here (matched by the static BorrowChecker pass):
    //   * &mut a const binding (let const / const)        â€” rejected
    //   * &mut while any other borrow is live              â€” rejected
    //   * & while a &mut is live                           â€” rejected
    //   * & or &mut a moved binding                        â€” rejected
    public class BorrowNodeVisitor : NodeVisitor<BorrowNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(BorrowNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(BorrowNode node, Context context, IInterpreter interpreter)
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

            // Shared place-borrow logic with the IR-lowered OP_BORROW /
            // OP_BORROW_MUT handlers (BorrowOps.TryBorrow).
            var (val, err) = BorrowOps.TryBorrow(
                context, name, node.IsMutable, node.Lifetime,
                node.PositionStart, node.PositionEnd);
            if (err != null) return res.Failure(err);
            return res.Success(val);
        }
    }
}

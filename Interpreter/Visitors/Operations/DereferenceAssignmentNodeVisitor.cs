using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    // Handles `*ref = value` and `*ref op= value`. The RefTarget must resolve to
    // an IReferenceValue (BorrowValue, ReferenceValue, ClassFieldReferenceValue,
    // ...). For BorrowValue specifically the setter enforces that the borrow is
    // mutable and live.
    public class DereferenceAssignmentNodeVisitor : NodeVisitor<DereferenceAssignmentNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(DereferenceAssignmentNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(DereferenceAssignmentNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var refValue = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.RefTarget, context, interpreter));
            if (res.ShouldReturn()) return res;

            var newValue = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.ValueNode, context, interpreter));
            if (res.ShouldReturn()) return res;

            // Shared operator + write-through with the IR-lowered OP_DEREF_STORE
            // handler (DerefStoreOps.Apply).
            var (result, error) = RaLanguage.Interpreter.Runtime.DerefStoreOps.Apply(
                refValue, newValue, node.AssignmentToken.Type, context,
                node.PositionStart, node.PositionEnd);
            if (error != null) return res.Failure(error);
            return res.Success(result);
        }
    }
}

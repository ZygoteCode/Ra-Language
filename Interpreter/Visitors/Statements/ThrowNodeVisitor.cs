using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class ThrowNodeVisitor : NodeVisitor<ThrowNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(ThrowNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(ThrowNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var value = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Expression, context, interpreter));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            string message = value == null ? "<null>" : value.ToString() ?? "<null>";
            return res.Failure(new RuntimeError(
                node.PositionStart,
                node.PositionEnd,
                message,
                context));
        }
    }
}

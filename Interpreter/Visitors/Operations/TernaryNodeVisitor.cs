using RaLanguage.Interpreter.Architecture;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class TernaryNodeVisitor : NodeVisitor<TernaryNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(TernaryNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var condVal = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Condition, context, interpreter));
            if (res.ShouldReturn()) return res;

            bool condIsTrue;
            if (condVal.Type == RuntimeValueType.Boolean)
                condIsTrue = ((BooleanValue)condVal).Value;
            else
                condIsTrue = condVal.IsTrue();

            if (condIsTrue)
            {
                var trueVal = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.TrueExpression, context, interpreter));
                if (res.ShouldReturn()) return res;
                return res.Success(trueVal);
            }
            else
            {
                var falseVal = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.FalseExpression, context, interpreter));
                if (res.ShouldReturn()) return res;
                return res.Success(falseVal);
            }
        }
    }
}
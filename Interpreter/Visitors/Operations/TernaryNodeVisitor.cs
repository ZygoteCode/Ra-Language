using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class TernaryNodeVisitor : NodeVisitor<TernaryNode>
    {
        protected sealed override RuntimeResult VisitNode(TernaryNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var condVal = res.Register(interpreter.Visit(node.Condition, context));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            bool condIsTrue;
            if (condVal.Type == RuntimeValueType.Boolean)
                condIsTrue = ((BooleanValue)condVal).Value;
            else
                condIsTrue = condVal.IsTrue();

            if (condIsTrue)
            {
                var trueVal = res.Register(interpreter.Visit(node.TrueExpression, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
                return res.Success(trueVal);
            }
            else
            {
                var falseVal = res.Register(interpreter.Visit(node.FalseExpression, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
                return res.Success(falseVal);
            }
        }
    }
}
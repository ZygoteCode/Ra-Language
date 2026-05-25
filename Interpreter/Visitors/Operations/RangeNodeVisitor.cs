using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class RangeNodeVisitor : NodeVisitor<RangeNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(RangeNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var start = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Start, context, interpreter));
            if (res.ShouldReturn()) return res;

            if (start.Type != RuntimeValueType.Number)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Start value should be a number", context));

            var end = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.End, context, interpreter));
            if (res.ShouldReturn()) return res;

            if (end.Type != RuntimeValueType.Number)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "End value should be a number", context));

            RuntimeValue? step = null;

            if (node.Step != null)
            {
                step = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Step, context, interpreter));
                if (res.ShouldReturn()) return res;

                if (step.Type != RuntimeValueType.Number)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Step value should be a number", context));
            }

            NumberValue startValue = (NumberValue)start, endValue = (NumberValue)end;
            NumberValue stepValue = step != null ? (NumberValue)step : NumberValue.One;

            if (startValue.Value > endValue.Value)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Start value should not be higher than the end value", context));

            List<RuntimeValue> values = new List<RuntimeValue>();

            if (node.Operator.Type == TokenType.DOUBLE_DOT)
            {
                for (BigNumber i = startValue.Value; i < endValue.Value; i += stepValue.Value)
                    values.Add(new NumberValue(i).SetContext(context));
            }
            else if (node.Operator.Type == TokenType.DOUBLE_DOT_EQ)
            {
                for (BigNumber i = startValue.Value; i <= endValue.Value; i += stepValue.Value)
                    values.Add(new NumberValue(i).SetContext(context));
            }

            return res.Success(new ListValue(values).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
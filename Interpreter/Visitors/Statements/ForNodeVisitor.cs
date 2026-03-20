using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class ForNodeVisitor : NodeVisitor<ForNode>
    {
        protected override RuntimeResult VisitNode(ForNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var elements = new List<RuntimeValue>();
            var initializationContext = context.Copy();
            var startValue = res.Register(interpreter.Visit(node.StartValueNode, initializationContext));
            if (res.Error != null) return res;
            context.ApplyChangesFrom(initializationContext);
            if (res.ShouldReturn()) return res;

            var endValue = res.Register(interpreter.Visit(node.EndValueNode, initializationContext));
            if (res.Error != null) return res;
            context.ApplyChangesFrom(initializationContext);
            if (res.ShouldReturn()) return res;

            RuntimeValue stepValue;

            if (node.StepValueNode != null)
            {
                stepValue = res.Register(interpreter.Visit(node.StepValueNode, initializationContext));
                if (res.Error != null) return res;
                context.ApplyChangesFrom(initializationContext);
                if (res.ShouldReturn()) return res;
            }
            else
            {
                stepValue = new NumberValue(1);
            }

            BigNumber i = ((NumberValue)startValue).Value;
            BigNumber end = ((NumberValue)endValue).Value;
            BigNumber step = ((NumberValue)stepValue).Value;

            Func<bool> condition = (step >= 0) ? () => i < end : () => i > end;
            var newContext = initializationContext.Copy();

            while (condition())
            {
                newContext.SymbolTable.Set(node.VarNameTok.Value.ToString(), new NumberValue(i));
                i += step;
                Context actualContext = newContext.Copy();
                var value = res.Register(interpreter.Visit(node.BodyNode, actualContext));
                if (res.Error != null) return res;
                context.ApplyChangesFrom(actualContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;
                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;

                elements.Add(value);
            }

            return res.Success(
                node.ShouldReturnNull ? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : new ListValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }
    }
}
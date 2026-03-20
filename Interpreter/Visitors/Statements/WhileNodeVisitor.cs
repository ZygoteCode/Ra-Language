using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class WhileNodeVisitor : NodeVisitor<WhileNode>
    {
        protected override RuntimeResult VisitNode(WhileNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var elements = new List<RuntimeValue>();
            Context newContext = context.Copy();

            while (true)
            {
                var condition = res.Register(interpreter.Visit(node.ConditionNode, newContext));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
                if (!condition.IsTrue()) break;

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
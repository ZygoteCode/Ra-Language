using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class DoWhileNodeVisitor : NodeVisitor<DoWhileNode>
    {
        protected sealed override RuntimeResult VisitNode(DoWhileNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            bool firstTime = true;
            Context newContext = context.Copy();

            while (true)
            {
                var condition = res.Register(interpreter.Visit(node.ConditionNode, newContext));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                if (!firstTime && !condition.IsTrue()) break;
                else firstTime = false;

                Context iterationContext = newContext.Copy();
                var value = res.Register(interpreter.Visit(node.BodyNode, iterationContext));
                if (res.Error != null) return res;
                newContext.ApplyChangesFrom(iterationContext);
                context.ApplyChangesFrom(newContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;
                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;
            }

            return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
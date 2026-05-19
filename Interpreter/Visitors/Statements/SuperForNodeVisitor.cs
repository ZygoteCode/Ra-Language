using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class SuperForNodeVisitor : NodeVisitor<SuperForNode>
    {
        protected sealed override RuntimeResult VisitNode(SuperForNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var newContext = context.Copy();

            foreach (var initializationNode in node.InitializationNodes)
            {
                res.Register(interpreter.Visit(initializationNode, context));
                if (res.Error != null) return res;
            }

            while (true)
            {
                bool canContinue = true;

                foreach (var conditionNode in node.ConditionNodes)
                {
                    var condition = res.Register(interpreter.Visit(conditionNode, newContext));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;

                    if (!condition.IsTrue())
                    {
                        canContinue = false;
                        break;
                    }
                }

                if (!canContinue)
                {
                    break;
                }

                Context actualContext = newContext.Copy();
                var value = res.Register(interpreter.Visit(node.BodyNode, actualContext));
                if (res.Error != null) return res;

                foreach (var stepNode in node.StepNodes)
                {
                    var condition = res.Register(interpreter.Visit(stepNode, newContext));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;
                }

                context.ApplyChangesFrom(actualContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;
                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;
            }

            return res.Success(NullValue.Null.SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }
    }
}
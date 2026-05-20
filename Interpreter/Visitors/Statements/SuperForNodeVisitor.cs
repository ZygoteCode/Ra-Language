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
            var loopContext = context.Copy();

            // Initialisation runs in the loop's own scope so iter/state vars (`var i = 0`)
            // stay LOCAL to the for-statement and do not pollute the surrounding scope.
            foreach (var initializationNode in node.InitializationNodes)
            {
                res.Register(interpreter.Visit(initializationNode, loopContext));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
            }

            // Single reusable body scope; cleared per iteration to drop iteration-local
            // declarations. Step expressions run in loopContext so they update the
            // iter vars declared by the init pass above.
            var bodyContext = loopContext.Copy();
            var bodySymbols = bodyContext.SymbolTable!;

            while (true)
            {
                bool canContinue = true;

                foreach (var conditionNode in node.ConditionNodes)
                {
                    var condition = res.Register(interpreter.Visit(conditionNode, loopContext));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;

                    if (!condition.IsTrue())
                    {
                        canContinue = false;
                        break;
                    }
                }

                if (!canContinue) break;

                bodySymbols.Clear();
                bodyContext.ScopeSkipCopy = true;
                res.Register(interpreter.Visit(node.BodyNode, bodyContext));
                if (res.Error != null) return res;

                if (res.LoopShouldBreak) break;
                if (res.ShouldReturn() && !res.LoopShouldContinue) return res;

                foreach (var stepNode in node.StepNodes)
                {
                    var stepRes = res.Register(interpreter.Visit(stepNode, loopContext));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;
                }
            }

            return res.Success(NullValue.Null.SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }
    }
}

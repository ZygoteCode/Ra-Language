using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class WhileNodeVisitor : NodeVisitor<WhileNode>
    {
        protected sealed override RuntimeResult VisitNode(WhileNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var loopContext = context.Copy();

            // Reused per-iteration body scope. Allocated once, cleared between iterations.
            var bodyContext = loopContext.Copy();
            var bodySymbols = bodyContext.SymbolTable!;

            while (true)
            {
                var condition = res.Register(interpreter.Visit(node.ConditionNode, loopContext));
                if (res.Error != null) return res;
                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;
                if (condition == null || !condition.IsTrue()) break;

                bodySymbols.Clear();
                bodyContext.ScopeSkipCopy = true;
                res.Register(interpreter.Visit(node.BodyNode, bodyContext));
                if (res.Error != null) return res;
                context.ApplyChangesFrom(bodyContext);

                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;
                if (res.ShouldReturn()) return res;
            }

            return res.Success(NullValue.Null);
        }
    }
}

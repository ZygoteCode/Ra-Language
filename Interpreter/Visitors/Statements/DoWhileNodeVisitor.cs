using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class DoWhileNodeVisitor : NodeVisitor<DoWhileNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(DoWhileNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            bool firstTime = true;
            var loopContext = context.Copy();

            // Single reusable body scope cleared per iteration to drop locals.
            var bodyContext = loopContext.Copy();
            var bodySymbols = bodyContext.SymbolTable!;

            while (true)
            {
                var condition = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.ConditionNode, loopContext, interpreter));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                if (!firstTime && !condition.IsTrue()) break;
                else firstTime = false;

                bodySymbols.Clear();
                bodyContext.ScopeSkipCopy = true;
                res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.BodyNode, bodyContext, interpreter));
                if (res.Error != null) return res;

                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;
                if (res.ShouldReturn()) return res;
            }

            return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

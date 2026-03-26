using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Special
{
    public class ScopeNodeVisitor : NodeVisitor<ScopeNode>
    {
        protected override RuntimeResult VisitNode(ScopeNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            Context newContext = context.Copy();

            foreach (var nodeToVisit in node.Nodes)
            {
                res.Register(interpreter.Visit(nodeToVisit, newContext));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
            }

            context.ApplyChangesFrom(newContext);
            newContext.Dispose();
            return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
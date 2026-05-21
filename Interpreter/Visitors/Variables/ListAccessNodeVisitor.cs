using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class ListAccessNodeVisitor : NodeVisitor<ListAccessNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(ListAccessNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var target = res.Register(await interpreter.Visit(node.Target, context));
            if (res.ShouldReturn()) return res;

            var index = res.Register(await interpreter.Visit(node.Index, context));
            if (res.ShouldReturn()) return res;

            ValueResult result = target.ListAccess(index);
            if (result.Item2 != null) return res.Failure(result.Item2);
            return res.Success(result.Item1!.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
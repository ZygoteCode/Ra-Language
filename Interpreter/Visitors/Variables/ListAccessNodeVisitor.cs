using RaLanguage.Errors;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class ListAccessNodeVisitor : NodeVisitor<ListAccessNode>
    {
        protected override RuntimeResult VisitNode(ListAccessNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var target = res.Register(interpreter.Visit(node.Target, context));
            if (res.Error != null) return res;

            var index = res.Register(interpreter.Visit(node.Index, context));
            if (res.Error != null) return res;

            (RuntimeValue?, Error?) result = target.ListAccess(index);
            if (result.Item2 != null) return res.Failure(result.Item2);
            return res.Success(result.Item1!.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
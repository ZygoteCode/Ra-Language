using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Interpreter.Visitors.Variables
{
    public class SelfNodeVisitor : NodeVisitor<SelfNode>
    {
        protected sealed override RuntimeResult VisitNode(SelfNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var selfEntry = context.SymbolTable.GetEntry("self");
            if (selfEntry == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "'self' is not available here", context));

            return res.Success(selfEntry.Value);
        }
    }
}
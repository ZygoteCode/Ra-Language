using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class PassNodeVisitor : NodeVisitor<PassNode>
    {
        protected sealed override RuntimeResult VisitNode(PassNode node, Context context, IInterpreter interpreter)
        {
            return new RuntimeResult().Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
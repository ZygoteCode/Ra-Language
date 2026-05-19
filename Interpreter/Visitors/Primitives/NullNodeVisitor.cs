using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class NullNodeVisitor : NodeVisitor<NullNode>
    {
        protected sealed override RuntimeResult VisitNode(NullNode node, Context context, IInterpreter interpreter)
        {
            return new RuntimeResult().Success(NullValue.Null.SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }
    }
}
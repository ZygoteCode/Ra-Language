using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class NumberNodeVisitor : NodeVisitor<NumberNode>
    {
        protected override RuntimeResult VisitNode(NumberNode node, Context context, IInterpreter interpreter)
        {
            return new RuntimeResult().Success(
                new NumberValue(BigNumber.Parse(node.Tok.Value.ToString()))
                    .SetContext(context)
                    .SetPos(node.PositionStart, node.PositionEnd)
            );
        }
    }
}
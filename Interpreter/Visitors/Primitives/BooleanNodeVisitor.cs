using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    public class BooleanNodeVisitor : NodeVisitor<BooleanNode>
    {
        protected sealed override RuntimeResult VisitNode(BooleanNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (((Keyword)node.Token.Value) == Keyword.True)
                return res.Success(new BooleanValue(true).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            else if (((Keyword)node.Token.Value) == Keyword.False)
                return res.Success(new BooleanValue(false).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid boolean value", context));
        }
    }
}
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Iterations
{
    public class BreakNode : AstNode
    {
        public BreakNode(Position positionStart, Position posEnd)
        {
            PosStart = positionStart;
            PosEnd = posEnd;
        }
    }
}
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Iterations
{
    public class ContinueNode : AstNode
    {
        public ContinueNode(Position positionStart, Position posEnd)
        {
            PosStart = positionStart;
            PosEnd = posEnd;
        }
    }
}
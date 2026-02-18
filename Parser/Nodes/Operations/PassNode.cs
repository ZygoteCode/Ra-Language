using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Operations
{
    public class PassNode : AstNode
    {
        public PassNode(Position positionStart, Position positionEnd)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
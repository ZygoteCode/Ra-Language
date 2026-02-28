using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Iterations
{
    public class BreakNode : AstNode
    {
        public BreakNode(Position positionStart, Position positionEnd) : base(AstNodeType.Break)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Iterations
{
    public class ContinueNode : AstNode
    {
        public ContinueNode(Position positionStart, Position positionEnd) : base(AstNodeType.Continue)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
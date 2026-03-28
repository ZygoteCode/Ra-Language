using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Classes
{
    public class SuperNode : AstNode
    {
        public SuperNode(Position positionStart, Position positionEnd) : base(AstNodeType.Super)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
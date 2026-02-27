using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class ListAccessNode : AstNode
    {
        public AstNode Target { get; }
        public AstNode Index { get; }

        public ListAccessNode(AstNode target, AstNode index, Position positionStart, Position positionEnd)
        {
            Target = target;
            Index = index;
            PositionStart = positionStart.Copy();
            PositionEnd = positionEnd.Copy();
        }
    }
}
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Operations
{
    public sealed class PassNode : AstNode
    {
        public PassNode(Position positionStart, Position positionEnd) : base(AstNodeType.Pass)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
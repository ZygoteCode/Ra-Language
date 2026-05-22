using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Statements
{
    public sealed class ThrowNode : AstNode
    {
        public AstNode Expression { get; }

        public ThrowNode(AstNode expression, Position positionStart, Position positionEnd)
            : base(AstNodeType.Throw)
        {
            Expression = expression;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}

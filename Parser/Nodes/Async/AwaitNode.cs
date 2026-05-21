using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Async
{
    public sealed class AwaitNode : AstNode
    {
        public AstNode Expression { get; }

        public AwaitNode(AstNode expression, Position positionStart, Position positionEnd) : base(AstNodeType.Await)
        {
            Expression = expression;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}

using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Async
{
    public class EmitNode : AstNode
    {
        public AstNode Expression { get; }

        public EmitNode(AstNode expression, Position positionStart, Position positionEnd) : base(AstNodeType.Emit)
        {
            Expression = expression;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}

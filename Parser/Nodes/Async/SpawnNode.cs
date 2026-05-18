using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Async
{
    public class SpawnNode : AstNode
    {
        public AstNode Expression { get; }

        public SpawnNode(AstNode expression, Position positionStart, Position positionEnd) : base(AstNodeType.Spawn)
        {
            Expression = expression;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}

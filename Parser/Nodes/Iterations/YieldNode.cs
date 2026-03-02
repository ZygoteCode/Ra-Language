using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Iterations
{
    public class YieldNode : AstNode
    {
        public AstNode Expression { get; }

        public YieldNode(AstNode expr, Position start, Position end) : base(AstNodeType.Yield)
        {
            Expression = expr;
            PositionStart = start;
            PositionEnd = end;
        }
    }
}
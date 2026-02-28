using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    public class SpreadNode : AstNode
    {
        public Token SpreadToken { get; }
        public AstNode Expression { get; }

        public SpreadNode(Token spreadToken, AstNode expression) : base(AstNodeType.Spread)
        {
            SpreadToken = spreadToken;
            Expression = expression;
            PositionStart = spreadToken.PositionStart.Copy();
            PositionEnd = expression.PositionEnd.Copy();
        }
    }
}
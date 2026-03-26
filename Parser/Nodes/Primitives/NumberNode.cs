using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class NumberNode : AstNode
    {
        public Token Tok { get; }
        public NumberNode(Token tok) : base(AstNodeType.Number)
        {
            Tok = tok;
            PositionStart = tok.PositionStart;
            PositionEnd = tok.PositionEnd;
        }
    }
}
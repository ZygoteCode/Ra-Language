using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class NumberNode : AstNode
    {
        public Token Tok { get; }
        public NumberNode(Token tok)
        {
            Tok = tok;
            PositionStart = tok.PositionStart;
            PositionEnd = tok.PositionEnd;
        }
        public override string ToString() => Tok.ToString();
    }
}
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class NumberNode : AstNode
    {
        public Token Tok { get; }
        public NumberNode(Token tok)
        {
            Tok = tok;
            PosStart = tok.PosStart;
            PosEnd = tok.PosEnd;
        }
        public override string ToString() => Tok.ToString();
    }
}
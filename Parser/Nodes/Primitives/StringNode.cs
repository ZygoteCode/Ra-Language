using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class StringNode : AstNode
    {
        public Token Tok { get; }
        public StringNode(Token tok)
        {
            Tok = tok;
            PosStart = tok.PosStart;
            PosEnd = tok.PosEnd;
        }
        public override string ToString() => Tok.ToString();
    }
}
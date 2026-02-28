using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class StringNode : AstNode
    {
        public Token Tok { get; }
        public StringNode(Token tok) : base(AstNodeType.String)
        {
            Tok = tok;
            PositionStart = tok.PositionStart;
            PositionEnd = tok.PositionEnd;
        }
        public override string ToString() => Tok.ToString();
    }
}
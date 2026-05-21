using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public sealed class NullNode : AstNode
    {
        public Token Token { get; }
        public NullNode(Token token) : base(AstNodeType.Null)
        {
            Token = token;
            PositionStart = token.PositionStart;
            PositionEnd = token.PositionEnd;
        }
    }
}
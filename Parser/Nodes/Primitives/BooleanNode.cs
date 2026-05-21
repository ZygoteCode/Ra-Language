using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public sealed class BooleanNode : AstNode
    {
        public Token Token { get; }
        public BooleanNode(Token token) : base(AstNodeType.Boolean)
        {
            Token = token;
            PositionStart = token.PositionStart;
            PositionEnd = token.PositionEnd;
        }
    }
}
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Special
{
    public class NameofNode : AstNode
    {
        public Token Token { get; }

        public NameofNode(Token token) : base(AstNodeType.Nameof)
        {
            Token = token;
            PositionStart = token.PositionStart;
            PositionEnd = token.PositionEnd;
        }
    }
}
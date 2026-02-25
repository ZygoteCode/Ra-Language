using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class BooleanNode : AstNode
    {
        public Token Token { get; }
        public BooleanNode(Token token)
        {
            Token = token;
            PositionStart = token.PositionStart;
            PositionEnd = token.PositionEnd;
        }
    }
}
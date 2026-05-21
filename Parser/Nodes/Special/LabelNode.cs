using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Special
{
    public sealed class LabelNode : AstNode
    {
        public Token Token { get; }
        public AstNode Statements { get; }

        public LabelNode(Token token, AstNode statements) : base(AstNodeType.Label)
        {
            Token = token;
            Statements = statements;
            PositionStart = token.PositionStart;
            PositionEnd = statements.PositionEnd;
        }
    }
}
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Functions
{
    public class ArgumentNode : AstNode
    {
        public Token? NameTok { get; }
        public AstNode Expr { get; }

        public ArgumentNode(Token? nameTok, AstNode expr) : base(AstNodeType.Argument)
        {
            NameTok = nameTok;
            Expr = expr;
            PositionStart = nameTok != null ? (nameTok != null ? nameTok.Value.PositionStart : expr.PositionStart) : expr.PositionStart;
            PositionEnd = expr.PositionEnd;
        }
    }
}
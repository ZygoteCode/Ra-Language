using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Functions
{
    public sealed class ArgumentNode : AstNode
    {
        public Token? NameTok { get; }
        public AstNode Expr { get; }
        public bool IsRef { get; }

        public ArgumentNode(Token? nameTok, AstNode expr, bool isRef = false) : base(AstNodeType.Argument)
        {
            NameTok = nameTok;
            Expr = expr;
            IsRef = isRef;
            PositionStart = nameTok != null ? (nameTok != null ? nameTok.Value.PositionStart : expr.PositionStart) : expr.PositionStart;
            PositionEnd = expr.PositionEnd;
        }
    }
}
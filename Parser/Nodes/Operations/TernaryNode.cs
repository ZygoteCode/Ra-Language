using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    public class TernaryNode : AstNode
    {
        public AstNode Condition { get; }
        public AstNode TrueExpression { get; }
        public AstNode FalseExpression { get; }
        public Token OperatorToken { get; }

        public TernaryNode(AstNode condition, AstNode trueExpr, AstNode falseExpr, Token opTok) : base(AstNodeType.Ternary)
        {
            Condition = condition;
            TrueExpression = trueExpr;
            FalseExpression = falseExpr;
            OperatorToken = opTok;

            PositionStart = condition.PositionStart;
            PositionEnd = falseExpr.PositionEnd;
        }
    }
}
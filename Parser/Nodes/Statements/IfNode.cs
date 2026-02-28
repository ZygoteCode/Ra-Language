namespace RaLanguage.Parser.Nodes.Statements
{
    public class IfNode : AstNode
    {
        public List<(AstNode Condition, AstNode Expr, bool ShouldReturnNull)> Cases { get; }
        public (AstNode Expr, bool ShouldReturnNull)? ElseCase { get; }

        public IfNode(List<(AstNode, AstNode, bool)> cases, (AstNode, bool)? elseCase) : base(AstNodeType.If)
        {
            Cases = cases;
            ElseCase = elseCase;
            PositionStart = cases[0].Item1.PositionStart;

            if (elseCase != null)
                PositionEnd = elseCase.Value.Item1.PositionEnd;
            else
                PositionEnd = cases[cases.Count - 1].Item2.PositionEnd;
        }
    }
}
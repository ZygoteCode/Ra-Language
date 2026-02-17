namespace RaLanguage.Parser.Nodes.Statements
{
    public class IfNode : AstNode
    {
        public List<(AstNode Condition, AstNode Expr, bool ShouldReturnNull)> Cases { get; }
        public (AstNode Expr, bool ShouldReturnNull)? ElseCase { get; }

        public IfNode(List<(AstNode, AstNode, bool)> cases, (AstNode, bool)? elseCase)
        {
            Cases = cases;
            ElseCase = elseCase;
            PosStart = cases[0].Item1.PosStart;

            if (elseCase != null)
                PosEnd = elseCase.Value.Item1.PosEnd;
            else
                PosEnd = cases[cases.Count - 1].Item2.PosEnd;
        }
    }
}
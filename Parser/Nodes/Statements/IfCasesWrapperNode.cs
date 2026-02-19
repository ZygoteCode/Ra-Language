using RaLanguage.Parser.Nodes;

namespace RaLanguage.Parser.Nodes.Statements
{
    public class IfCasesWrapperNode : AstNode
    {
        public List<(AstNode Condition, AstNode Body, bool ShouldReturnNull)> Cases { get; }
        public (AstNode Body, bool ShouldReturnNull)? ElseCase { get; }

        public IfCasesWrapperNode(List<(AstNode, AstNode, bool)> cases, (AstNode, bool)? elseCase)
        {
            Cases = cases;
            ElseCase = elseCase;
        }
    }
}
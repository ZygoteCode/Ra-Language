namespace RaLanguage.Parser.Nodes.Statements
{
    public class WhileNode : AstNode
    {
        public AstNode ConditionNode { get; }
        public AstNode BodyNode { get; }
        public bool ShouldReturnNull { get; }

        public WhileNode(AstNode conditionNode, AstNode bodyNode, bool shouldReturnNull)
        {
            ConditionNode = conditionNode;
            BodyNode = bodyNode;
            ShouldReturnNull = shouldReturnNull;
            PosStart = conditionNode.PosStart;
            PosEnd = bodyNode.PosEnd;
        }
    }
}
namespace RaLanguage.Parser.Nodes.Statements
{
    public sealed class DoWhileNode : AstNode
    {
        public AstNode ConditionNode, BodyNode;
        public bool ShouldReturnNull;

        public DoWhileNode(AstNode conditionNode, AstNode bodyNode, bool shouldReturnNull) : base(AstNodeType.DoWhile)
        {
            ConditionNode = conditionNode;
            BodyNode = bodyNode;
            ShouldReturnNull = shouldReturnNull;
            PositionStart = bodyNode.PositionStart;
            PositionEnd = conditionNode.PositionEnd;
        }
    }
}
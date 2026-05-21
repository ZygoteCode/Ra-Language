namespace RaLanguage.Parser.Nodes.Statements
{
    public sealed class SuperForNode : AstNode
    {
        public List<AstNode> InitializationNodes { get; }
        public List<AstNode> ConditionNodes { get; }
        public List<AstNode> StepNodes { get; }
        public AstNode BodyNode { get; }
        public bool ShouldReturnNull { get; }

        public SuperForNode(List<AstNode> initializationNodes, List<AstNode> conditionNodes, List<AstNode> stepNodes, AstNode bodyNode, bool shouldReturnNull) : base(AstNodeType.SuperFor)
        {
            InitializationNodes = initializationNodes;
            ConditionNodes = conditionNodes;
            StepNodes = stepNodes;
            BodyNode = bodyNode;
            ShouldReturnNull = shouldReturnNull;
           
            if (initializationNodes.Count > 0)
            {
                PositionStart = initializationNodes[0].PositionStart;
            }
            else if (conditionNodes.Count > 0)
            {
                PositionStart = conditionNodes[0].PositionStart;
            }
            else if (stepNodes.Count > 0)
            {
                PositionStart = stepNodes[0].PositionStart;
            }
            else
            {
                PositionStart = bodyNode.PositionStart;
            }

            PositionEnd = bodyNode.PositionEnd;
        }
    }
}
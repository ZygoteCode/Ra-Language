namespace RaLanguage.Parser.Nodes.Statements
{
    public sealed class RetryNode : AstNode
    {
        public AstNode CountNode { get; }
        public AstNode BodyNode { get; }
        public AstNode? ElseNode { get; }
        public AstNode? DelayNode { get; }

        public RetryNode(AstNode countNode, AstNode bodyNode, AstNode? delayNode, AstNode? elseNode)
            : base(AstNodeType.Retry)
        {
            CountNode = countNode;
            BodyNode = bodyNode;
            DelayNode = delayNode;
            ElseNode = elseNode;

            PositionStart = countNode.PositionStart;
            PositionEnd = elseNode == null ? (delayNode != null ? delayNode.PositionEnd : bodyNode.PositionEnd) : elseNode.PositionEnd;
        }
    }
}
namespace RaLanguage.Parser.Nodes.Special
{
    public sealed class TypeofNode : AstNode
    {
        public AstNode Node { get; }

        public TypeofNode(AstNode node) : base(AstNodeType.Typeof)
        {
            Node = node;
            PositionStart = node.PositionStart;
            PositionEnd = node.PositionEnd;
        }
    }
}
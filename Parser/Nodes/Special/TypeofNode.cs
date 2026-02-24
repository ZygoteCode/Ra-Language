namespace RaLanguage.Parser.Nodes.Special
{
    public class TypeofNode : AstNode
    {
        public AstNode Node { get; }

        public TypeofNode(AstNode node)
        {
            Node = node;
            PositionStart = node.PositionStart;
            PositionEnd = node.PositionEnd;
        }
    }
}
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class ListNode : AstNode
    {
        public List<AstNode> ElementNodes { get; }
        public ListNode(List<AstNode> elementNodes, Position positionStart, Position positionEnd) : base(AstNodeType.List)
        {
            ElementNodes = elementNodes;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
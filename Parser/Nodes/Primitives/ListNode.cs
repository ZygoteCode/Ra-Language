using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class ListNode : AstNode
    {
        public List<AstNode> ElementNodes { get; }
        public bool IsNewContext { get; }
        public ListNode(List<AstNode> elementNodes, Position positionStart, Position positionEnd, bool isNewContext = false)
        {
            ElementNodes = elementNodes;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
            IsNewContext = isNewContext;
        }
    }
}
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class ListNode : AstNode
    {
        public List<AstNode> ElementNodes { get; }
        public ListNode(List<AstNode> elementNodes, Position positionStart, Position posEnd)
        {
            ElementNodes = elementNodes;
            PosStart = positionStart;
            PosEnd = posEnd;
        }
    }
}
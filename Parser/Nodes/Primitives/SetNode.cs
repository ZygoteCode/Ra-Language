using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class SetNode : AstNode
    {
        public List<AstNode> ElementNodes { get; }
        public SetNode(List<AstNode> elementNodes, Position positionStart, Position positionEnd) : base(AstNodeType.Set)
        {
            ElementNodes = elementNodes;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
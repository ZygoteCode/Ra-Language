using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Special
{
    public class ScopeNode : AstNode
    {
        public List<AstNode> Nodes { get; }

        public ScopeNode(List<AstNode> elementNodes, Position positionStart, Position positionEnd, bool isNewContext = false) : base(AstNodeType.Scope)
        {
            Nodes = elementNodes;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
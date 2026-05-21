using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public sealed class TupleNode : AstNode
    {
        public List<AstNode> ElementNodes { get; }

        public TupleNode(List<AstNode> elementNodes, Position start, Position end) : base(AstNodeType.Tuple)
        {
            ElementNodes = elementNodes;
            PositionStart = start;
            PositionEnd = end;
        }
    }
}
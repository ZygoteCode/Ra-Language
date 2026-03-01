using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public class StringNode : AstNode
    {
        public List<AstNode> Parts { get; }
        public StringNode(List<AstNode> parts, Position posStart, Position posEnd) : base(AstNodeType.String)
        {
            Parts = parts;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}
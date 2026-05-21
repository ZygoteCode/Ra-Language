using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public sealed class MapNode : AstNode
    {
        public List<(AstNode Key, AstNode Value)> Pairs { get; }

        public MapNode(List<(AstNode, AstNode)> pairs, Position positionStart, Position positionEnd) : base(AstNodeType.Map)
        {
            Pairs = pairs;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
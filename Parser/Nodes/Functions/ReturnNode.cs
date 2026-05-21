using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Functions
{
    public sealed class ReturnNode : AstNode
    {
        public AstNode? NodeToReturn { get; }
        public ReturnNode(AstNode? nodeToReturn, Position positionStart, Position positionEnd) : base(AstNodeType.Return)
        {
            NodeToReturn = nodeToReturn;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
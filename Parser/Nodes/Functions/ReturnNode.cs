using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Functions
{
    public class ReturnNode : AstNode
    {
        public AstNode? NodeToReturn { get; }
        public ReturnNode(AstNode? nodeToReturn, Position positionStart, Position positionEnd)
        {
            NodeToReturn = nodeToReturn;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
        }
    }
}
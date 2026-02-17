using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Functions
{
    public class ReturnNode : AstNode
    {
        public AstNode? NodeToReturn { get; }
        public ReturnNode(AstNode? nodeToReturn, Position positionStart, Position posEnd)
        {
            NodeToReturn = nodeToReturn;
            PosStart = positionStart;
            PosEnd = posEnd;
        }
    }
}
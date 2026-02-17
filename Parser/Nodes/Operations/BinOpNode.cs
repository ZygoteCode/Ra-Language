using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    public class BinOpNode : AstNode
    {
        public AstNode LeftNode { get; }
        public Token OpTok { get; }
        public AstNode RightNode { get; }
        public BinOpNode(AstNode leftNode, Token opTok, AstNode rightNode)
        {
            LeftNode = leftNode;
            OpTok = opTok;
            RightNode = rightNode;
            PositionStart = leftNode.PositionStart;
            PositionEnd = rightNode.PositionEnd;
        }
        public override string ToString() => $"({LeftNode}, {OpTok}, {RightNode})";
    }
}
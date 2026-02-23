using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    public class UnaryOperationNode : AstNode
    {
        public Token OpTok { get; }
        public AstNode Node { get; }
        public bool IsLeft { get; }
        public UnaryOperationNode(Token opTok, AstNode node, bool isLeft = true)
        {
            OpTok = opTok;
            Node = node;
            PositionStart = opTok.PositionStart;
            PositionEnd = node.PositionEnd;
            IsLeft = isLeft;
        }
        public override string ToString() => $"({OpTok}, {Node})";
    }
}
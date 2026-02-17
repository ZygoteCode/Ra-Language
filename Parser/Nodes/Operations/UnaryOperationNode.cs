using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    public class UnaryOperationNode : AstNode
    {
        public Token OpTok { get; }
        public AstNode Node { get; }
        public UnaryOperationNode(Token opTok, AstNode node)
        {
            OpTok = opTok;
            Node = node;
            PositionStart = opTok.PositionStart;
            PositionEnd = node.PositionEnd;
        }
        public override string ToString() => $"({OpTok}, {Node})";
    }
}
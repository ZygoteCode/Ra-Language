using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    public class UnaryOpNode : AstNode
    {
        public Token OpTok { get; }
        public AstNode Node { get; }
        public UnaryOpNode(Token opTok, AstNode node)
        {
            OpTok = opTok;
            Node = node;
            PosStart = opTok.PosStart;
            PosEnd = node.PosEnd;
        }
        public override string ToString() => $"({OpTok}, {Node})";
    }
}
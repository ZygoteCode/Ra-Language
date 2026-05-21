using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    public sealed class NullCoalescingNode : AstNode
    {
        public AstNode Left { get; }
        public AstNode Right { get; }
        public Token Operator { get; }

        public NullCoalescingNode(AstNode left, AstNode right, Token opTok) : base(AstNodeType.NullCoalescing)
        {
            Left = left;
            Right = right;
            Operator = opTok;
            PositionStart = left.PositionStart;
            PositionEnd = right.PositionEnd;
        }
    }
}
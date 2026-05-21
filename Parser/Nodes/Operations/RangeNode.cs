using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Operations
{
    public sealed class RangeNode : AstNode
    {
        public AstNode Start { get; }
        public AstNode End { get; }
        public Token Operator { get; }
        public AstNode? Step { get; }

        public RangeNode(AstNode start, AstNode end, Token opTok, AstNode? step = null) : base(AstNodeType.Range)
        {
            Start = start;
            End = end;
            Operator = opTok;
            Step = step;
            PositionStart = start.PositionStart;
            PositionEnd = step?.PositionEnd ?? end.PositionEnd;
        }
    }
}
using System.Collections.Generic;
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Patterns
{
    public sealed class MatchArmNode
    {
        public PatternNode Pattern { get; }
        public AstNode? Guard { get; }
        public AstNode Body { get; }
        public Position PositionStart { get; }
        public Position PositionEnd { get; }

        public MatchArmNode(PatternNode pattern, AstNode? guard, AstNode body, Position s, Position e)
        {
            Pattern = pattern;
            Guard = guard;
            Body = body;
            PositionStart = s;
            PositionEnd = e;
        }
    }

    public sealed class MatchNode : AstNode
    {
        public AstNode Scrutinee { get; }
        public List<MatchArmNode> Arms { get; }

        public MatchNode(AstNode scrutinee, List<MatchArmNode> arms, Position s, Position e)
            : base(AstNodeType.Match)
        {
            Scrutinee = scrutinee;
            Arms = arms;
            PositionStart = s;
            PositionEnd = e;
        }
    }
}

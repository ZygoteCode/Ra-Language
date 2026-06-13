using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Operations
{
    public sealed class CastNode : AstNode
    {
        public AstNode Expression { get; }
        public TypeDescriptor TargetType { get; }

        // `as?` — safe cast: on conversion failure yields `null` instead of
        // raising. `as` and `as!` keep the throwing semantics (Safe == false).
        public bool Safe { get; }

        public CastNode(AstNode expression, TypeDescriptor targetType, bool safe = false) : base(AstNodeType.Cast)
        {
            Expression = expression;
            TargetType = targetType;
            Safe = safe;

            PositionStart = expression.PositionStart;
            PositionEnd = expression.PositionEnd;
        }
    }
}
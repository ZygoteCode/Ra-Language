using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Operations
{
    public class CastNode : AstNode
    {
        public AstNode Expression { get; }
        public TypeDescriptor TargetType { get; }

        public CastNode(AstNode expression, TypeDescriptor targetType) : base(AstNodeType.Cast)
        {
            Expression = expression;
            TargetType = targetType;

            PositionStart = expression.PositionStart;
            PositionEnd = expression.PositionEnd;
        }

        public override string ToString() => $"({Expression} as {TargetType})";
    }
}
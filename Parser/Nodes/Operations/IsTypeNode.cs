using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Operations
{
    // `expr is Type` and `expr is not Type`. Yields a bool at runtime and is
    // the narrowing primitive consumed by the flow analyzer to refine a
    // variable's static type inside the branch where the test holds (or
    // does not hold, for the negated form). The carried `Negated` flag is
    // semantic, not just sugar: it lets the narrowing analyzer flip the
    // refinement direction without re-walking through a UnaryOperation.
    public sealed class IsTypeNode : AstNode
    {
        public AstNode Expression { get; }
        public TypeDescriptor TestedType { get; }
        public bool Negated { get; }

        public IsTypeNode(AstNode expression, TypeDescriptor testedType, bool negated) : base(AstNodeType.IsType)
        {
            Expression = expression;
            TestedType = testedType;
            Negated = negated;

            PositionStart = expression.PositionStart;
            PositionEnd = expression.PositionEnd;
        }
    }
}

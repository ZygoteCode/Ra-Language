using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    // Interpolation segment that carries a `:spec` format directive parsed out
    // of `${expr:spec}`. The spec is held as an already-parsed FormatSpec so the
    // runtime visitor does not re-parse the textual specifier on every visit
    // (the AST lives across menu re-runs and across iterations of hot loops).
    public class FormattedInterpolationNode : AstNode
    {
        public AstNode Expression { get; }
        public Types.Formatting.FormatSpec FormatSpec { get; }
        public string RawSpec { get; }

        public FormattedInterpolationNode(AstNode expression, Types.Formatting.FormatSpec formatSpec, string rawSpec, Position posStart, Position posEnd)
            : base(AstNodeType.FormattedInterpolation)
        {
            Expression = expression;
            FormatSpec = formatSpec;
            RawSpec = rawSpec;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}

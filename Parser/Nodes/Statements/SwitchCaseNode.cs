using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Statements
{
    public enum SwitchCaseSeparator
    {
        Colon,
        Arrow
    }

    public class SwitchCaseNode : AstNode
    {
        public List<AstNode> Labels { get; }
        public bool IsDefault { get; }
        public SwitchCaseSeparator Separator { get; }
        public AstNode? Body { get; }

        public SwitchCaseNode(List<AstNode> labels, bool isDefault, SwitchCaseSeparator sep, AstNode? body, Position start, Position end) : base(AstNodeType.SwitchCase)
        {
            Labels = labels ?? new List<AstNode>();
            IsDefault = isDefault;
            Separator = sep;
            Body = body;
            PositionStart = start;
            PositionEnd = end;
        }
    }
}
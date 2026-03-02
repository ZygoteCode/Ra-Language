using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Statements
{
    public class SwitchNode : AstNode
    {
        public AstNode Expression { get; }
        public List<SwitchCaseNode> Cases { get; }

        public SwitchNode(AstNode expression, List<SwitchCaseNode> cases, Position start, Position end) : base(AstNodeType.Switch)
        {
            Expression = expression;
            Cases = cases;
            PositionStart = start;
            PositionEnd = end;
        }
    }
}
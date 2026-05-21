using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public sealed class ListAssignmentNode : AstNode
    {
        public AstNode Target { get; }
        public Token AssignmentToken { get; }
        public AstNode Value { get; }

        public ListAssignmentNode(AstNode target, Token assignmentToken, AstNode value) : base(AstNodeType.ListAssignment)
        {
            Target = target;
            AssignmentToken = assignmentToken;
            Value = value;
            PositionStart = target.PositionStart;
            PositionEnd = value.PositionEnd;
        }
    }
}
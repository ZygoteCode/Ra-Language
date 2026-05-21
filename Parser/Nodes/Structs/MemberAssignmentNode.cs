using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Structs
{
    public sealed class MemberAssignmentNode : AstNode
    {
        public MemberAccessNode TargetNode { get; }
        public Token AssignmentToken { get; }
        public AstNode ValueNode { get; }

        public MemberAssignmentNode(MemberAccessNode targetNode, Token assignmentToken, AstNode valueNode)
            : base(AstNodeType.MemberAssignment)
        {
            TargetNode = targetNode;
            AssignmentToken = assignmentToken;
            ValueNode = valueNode;
            PositionStart = targetNode.PositionStart;
            PositionEnd = valueNode.PositionEnd;
        }
    }
}
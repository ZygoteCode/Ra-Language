using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VariableAssignmentNode : AstNode
    {
        public Token VarNameTok { get; }
        public Token AssignmentToken { get; }
        public AstNode ValueNode { get; }

        public VariableAssignmentNode(Token varNameTok, Token assignmentToken, AstNode valueNode) : base(AstNodeType.VariableAssignment)
        {
            VarNameTok = varNameTok;
            AssignmentToken = assignmentToken;
            ValueNode = valueNode;
            PositionStart = varNameTok.PositionStart;
            PositionEnd = valueNode.PositionEnd;
        }
    }
}
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Parser.Nodes.Enums
{
    public class EnumDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public List<(Token MemberTok, AstNode? ValueNode)> Members { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public EnumDefinitionNode(
            Token nameTok,
            List<(Token MemberTok, AstNode? ValueNode)> members,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null): base(AstNodeType.EnumDefinition)
        {
            NameTok = nameTok;
            Members = members;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            PositionStart = nameTok.PositionStart;
            PositionEnd = members.Count > 0
                ? (members[members.Count - 1].ValueNode?.PositionEnd ?? members[members.Count - 1].MemberTok.PositionEnd)
                : nameTok.PositionEnd;
        }
    }
}

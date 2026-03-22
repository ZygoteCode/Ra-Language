using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Enums
{
    public class EnumDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public List<(Token MemberTok, AstNode? ValueNode)> Members { get; }

        public EnumDefinitionNode(Token nameTok, List<(Token MemberTok, AstNode? ValueNode)> members): base(AstNodeType.EnumDefinition)
        {
            NameTok = nameTok;
            Members = members;
            PositionStart = nameTok.PositionStart;
            PositionEnd = members.Count > 0
                ? (members[members.Count - 1].ValueNode?.PositionEnd ?? members[members.Count - 1].MemberTok.PositionEnd)
                : nameTok.PositionEnd;
        }
    }
}
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Enums
{
    public sealed class EnumAccessNode : AstNode
    {
        public AstNode EnumNode { get; }
        public Token MemberTok { get; }

        public EnumAccessNode(AstNode enumNode, Token memberTok)
            : base(AstNodeType.EnumAccess)
        {
            EnumNode = enumNode;
            MemberTok = memberTok;
            PositionStart = enumNode.PositionStart;
            PositionEnd = memberTok.PositionEnd;
        }
    }
}
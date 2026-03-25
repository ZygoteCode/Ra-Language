using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Structs
{
    public class MemberAccessNode : AstNode
    {
        public AstNode TargetNode { get; }
        public Token MemberTok { get; }

        public MemberAccessNode(AstNode targetNode, Token memberTok) : base(AstNodeType.MemberAccess)
        {
            TargetNode = targetNode;
            MemberTok = memberTok;
            PositionStart = targetNode.PositionStart;
            PositionEnd = memberTok.PositionEnd;
        }
    }
}
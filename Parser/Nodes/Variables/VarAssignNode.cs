using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VarAssignNode : AstNode
    {
        public Token VarNameTok { get; }
        public AstNode ValueNode { get; }
        public VarAssignNode(Token varNameTok, AstNode valueNode)
        {
            VarNameTok = varNameTok;
            ValueNode = valueNode;
            PositionStart = varNameTok.PositionStart;
            PositionEnd = valueNode.PositionEnd;
        }
    }
}
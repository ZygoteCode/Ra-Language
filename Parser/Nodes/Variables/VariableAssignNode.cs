using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VariableAssignNode : AstNode
    {
        public Token VarNameTok { get; }
        public AstNode ValueNode { get; }
        public VariableAssignNode(Token varNameTok, AstNode valueNode)
        {
            VarNameTok = varNameTok;
            ValueNode = valueNode;
            PositionStart = varNameTok.PositionStart;
            PositionEnd = valueNode.PositionEnd;
        }
    }
}
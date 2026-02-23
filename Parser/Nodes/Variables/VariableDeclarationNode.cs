using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VariableDeclarationNode : AstNode
    {
        public Token VarNameTok { get; }
        public AstNode ValueNode { get; }
        public VariableDeclarationNode(Token varNameTok, AstNode valueNode)
        {
            VarNameTok = varNameTok;
            ValueNode = valueNode;
            PositionStart = varNameTok.PositionStart;
            PositionEnd = valueNode.PositionEnd;
        }
    }
}
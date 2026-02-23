using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VariableDeclarationNode : AstNode
    {
        public VariableDeclarationType DeclarationType { get; }
        public Token VarNameTok { get; }
        public AstNode ValueNode { get; }
        public VariableDeclarationNode(VariableDeclarationType declarationType, Token varNameTok, AstNode valueNode)
        {
            DeclarationType = declarationType;
            VarNameTok = varNameTok;
            ValueNode = valueNode;
            PositionStart = varNameTok.PositionStart;
            PositionEnd = valueNode.PositionEnd;
        }
    }

    public enum VariableDeclarationType
    {
        VARIABLE,
        CONST,
        FINAL
    }
}
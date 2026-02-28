using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VariableAccessNode : AstNode
    {
        public Token VarNameTok { get; }
        public VariableAccessNode(Token varNameTok) : base(AstNodeType.VariableAccess)
        {
            VarNameTok = varNameTok;
            PositionStart = varNameTok.PositionStart;
            PositionEnd = varNameTok.PositionEnd;
        }
    }
}
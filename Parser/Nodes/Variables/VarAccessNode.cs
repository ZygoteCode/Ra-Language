using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public class VarAccessNode : AstNode
    {
        public Token VarNameTok { get; }
        public VarAccessNode(Token varNameTok)
        {
            VarNameTok = varNameTok;
            PositionStart = varNameTok.PositionStart;
            PositionEnd = varNameTok.PositionEnd;
        }
    }
}
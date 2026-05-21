using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Special
{
    public sealed class GotoNode : AstNode
    {
        public Token VarName { get; }

        public GotoNode(Position start, Token varName) : base(AstNodeType.Goto)
        {
            VarName = varName;
            PositionStart = start;
            PositionEnd = varName.PositionEnd;
        }
    }
}
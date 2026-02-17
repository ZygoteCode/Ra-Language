using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes
{
    public abstract class AstNode
    {
        public Position PositionStart { get; set; }
        public Position PositionEnd { get; set; }
    }
}
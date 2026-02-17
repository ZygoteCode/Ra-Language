using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes
{
    public abstract class AstNode
    {
        public Position PosStart { get; set; }
        public Position PosEnd { get; set; }
    }
}
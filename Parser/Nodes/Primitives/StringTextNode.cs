using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public sealed class StringTextNode : AstNode
    {
        public string Text { get; }
        public StringTextNode(string text, Position posStart, Position posEnd) : base(AstNodeType.StringPart)
        {
            Text = text;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}
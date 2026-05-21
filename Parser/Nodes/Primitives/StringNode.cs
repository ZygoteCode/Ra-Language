using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public sealed class StringNode : AstNode
    {
        public List<AstNode> Parts { get; }

        // When every Part is a literal StringTextNode, the string value is constant. The
        // visitor populates CachedValue lazily on the first execution so subsequent visits
        // skip the StringBuilder + interpolation loop entirely.
        public RuntimeValue? CachedValue { get; set; }

        public StringNode(List<AstNode> parts, Position posStart, Position posEnd) : base(AstNodeType.String)
        {
            Parts = parts;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}
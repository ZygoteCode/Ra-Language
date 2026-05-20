using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer;

namespace RaLanguage.Parser.Nodes.Primitives
{
    // AST representation of a `re"pattern"flags` literal. Carries the raw
    // pattern text plus the flag suffix so the runtime visitor can either
    // compile-once-and-cache or report a precise diagnostic against the
    // original source span.
    public class RegexLiteralNode : AstNode
    {
        public string Pattern { get; }
        public string Flags { get; }

        // Lazily populated by the visitor on first execution. Constant
        // patterns therefore compile exactly once per program run, even when
        // the literal sits inside a hot loop.
        public RuntimeValue? CachedValue { get; set; }

        public RegexLiteralNode(string pattern, string flags, Position posStart, Position posEnd)
            : base(AstNodeType.RegexLiteral)
        {
            Pattern = pattern ?? string.Empty;
            Flags = flags ?? string.Empty;
            PositionStart = posStart;
            PositionEnd = posEnd;
        }
    }
}

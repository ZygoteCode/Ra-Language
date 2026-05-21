using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Primitives
{
    public sealed class NumberNode : AstNode
    {
        public Token Tok { get; }

        // Lazily cached parsed value. A numeric literal in source code is constant for the
        // lifetime of the program; parsing it once and reusing the RuntimeValue eliminates a
        // BigNumber.Parse + allocation per visit, which is the dominant cost for `i + 1`
        // style hot loops. Visitor populates this on first use under single-threaded
        // execution semantics; the interpreter is not concurrent.
        public RuntimeValue? CachedValue { get; set; }

        public NumberNode(Token tok) : base(AstNodeType.Number)
        {
            Tok = tok;
            PositionStart = tok.PositionStart;
            PositionEnd = tok.PositionEnd;
        }
    }
}
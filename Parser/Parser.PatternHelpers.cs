using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Patterns;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Parser
{
    public partial class Parser
    {
        // Destructuring 'let' / 'var' / 'const' / 'final' declaration.
        // The kind keyword (and any 'mut' / 'const' modifier) has already
        // been consumed by ParseVariableDeclaration when control reaches
        // this method; the current token starts the pattern.
        private ParserResult ParseDestructuringDeclaration(
            ParserResult res,
            VariableDeclarationType kind,
            bool isPublic,
            bool isStatic,
            Position declStart)
        {
            var pattern = ParsePattern(res);
            if (res.Error != null) return res;
            if (pattern == null) return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                "a pattern after the declaration keyword",
                contextHint: "destructuring declarations expect a pattern, e.g. 'let (a, b) = expr;'"));

            TypeDescriptor? declaredType = null;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();
                declaredType = ParseType(res);
                if (declaredType == null)
                {
                    return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));
                }
            }

            if (_currentToken.Type != TokenType.EQ)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "'=' to provide the initializer of a destructuring declaration",
                    contextHint: "destructuring declarations require an initializer; e.g. 'let (a, b) = expr;'"));
            }
            res.RegisterAdvancement();
            Advance();

            var initializer = res.Register(ParseExpression());
            if (res.Error != null) return res;
            if (initializer == null) return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                "an expression after '=' in a destructuring declaration"));

            // We *intentionally* do not reject refutable patterns here.
            // Many idiomatic Ra programs destructure a value that is known
            // by construction to fit the pattern — e.g. 'let [h, ..t] =
            // pop_nonempty()' — and would have to be awkwardly rewritten
            // through 'if let' otherwise. The runtime engine still raises
            // a RuntimeError if the pattern fails to match, so safety is
            // preserved; the analyzer is free to emit a warning when it
            // can statically prove the pattern is irrefutable / refutable
            // (TODO once the type system carries enough information).
            // PatternRefutability is kept around for that future use.
            _ = PatternRefutability.IsIrrefutable(pattern);

            return res.Success(new DestructuringDeclarationNode(
                pattern, initializer, kind, declaredType,
                declStart, _currentToken.PositionStart,
                isPublic, isStatic));
        }
    }

    // Pure helper: refutability classification for the v1 pattern grammar.
    //
    //   Irrefutable patterns always succeed for *any* value of the right
    //   structural shape — they never observe value identity. These are
    //   safe in 'let' / parameter / loop destructuring contexts.
    //
    //   Refutable patterns may reject specific values (literal compare,
    //   range bounds, variant tag, type test, map key check, or-pattern
    //   alternation). They are only allowed in 'match' / 'if let' /
    //   'while let' contexts.
    internal static class PatternRefutability
    {
        public static bool IsIrrefutable(PatternNode p)
        {
            switch (p)
            {
                case WildcardPatternNode _:
                    return true;
                case VariablePatternNode _:
                    // Bare-identifier patterns *might* resolve to a zero-arity
                    // enum variant at runtime (refutable), but the parser
                    // cannot decide that without a symbol-table lookup. The
                    // analyzer rejects the misuse with a clearer message;
                    // here we are conservative-optimistic and trust the
                    // analyzer to do the deeper check.
                    return true;
                case TuplePatternNode tp:
                    foreach (var e in tp.Elements)
                        if (!IsIrrefutable(e)) return false;
                    return true;
                case StructPatternNode sp:
                    foreach (var (_, fp) in sp.Fields)
                        if (fp != null && !IsIrrefutable(fp)) return false;
                    return true;
                case AliasPatternNode ap:
                    return IsIrrefutable(ap.Inner);
                case ListPatternNode _:
                case VariantPatternNode _:
                case LiteralPatternNode _:
                case RangePatternNode _:
                case RelationalPatternNode _:
                case OrPatternNode _:
                case TypePatternNode _:
                case MapPatternNode _:
                case RestPatternNode _:
                default:
                    return false;
            }
        }
    }
}

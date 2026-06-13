using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Special
{
    public sealed class NameofNode : AstNode
    {
        public Token Token { get; }

        // The resolved textual name — the FINAL segment for a member chain
        // (`nameof(a.b.c)` → "c", matching C#). Known at parse time, so the
        // operator folds to a compile-time string constant (zero runtime cost).
        public string Name { get; }

        // The full segment path: Path[0] is the base symbol, the rest are the
        // member chain (`nameof(a.b.c)` → ["a","b","c"]). Retained so the static
        // analyzer can validate member-chain segments against the base's type;
        // does not affect the folded result (always Path[^1] == Name).
        public IReadOnlyList<string> Path { get; }

        public NameofNode(Token token, string? name = null, IReadOnlyList<string>? path = null) : base(AstNodeType.Nameof)
        {
            Token = token;
            Name = name ?? token.Value?.ToString() ?? "";
            Path = path ?? new[] { Name };
            PositionStart = token.PositionStart;
            PositionEnd = token.PositionEnd;
        }
    }
}
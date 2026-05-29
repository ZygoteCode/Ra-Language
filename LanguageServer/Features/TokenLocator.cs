using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Position → token lookups over the (offset-sorted) token stream. This is the
    /// tolerant resolution primitive used by the cursor-driven features: it works
    /// from the lexer output alone, so it keeps answering even when the parser could
    /// not build a tree for the surrounding (broken) code.
    /// </summary>
    public static class TokenLocator
    {
        public static string Text(in Token token) => token.Value?.ToString() ?? string.Empty;

        public static bool Contains(in Token token, int offset)
            => offset >= token.PositionStart.Idx && offset < token.PositionEnd.Idx;

        /// <summary>Index of the right-most token whose start offset is &lt;= <paramref name="offset"/>, or -1.</summary>
        public static int FloorIndex(IReadOnlyList<Token> tokens, int offset)
        {
            int lo = 0, hi = tokens.Count - 1, result = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (tokens[mid].PositionStart.Idx <= offset)
                {
                    result = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return result;
        }

        /// <summary>
        /// The identifier-like token under the cursor. Accepts the cursor on either
        /// boundary of the token (LSP word semantics), and falls back to the token
        /// immediately to the left when the cursor sits at its trailing edge.
        /// </summary>
        public static bool TryGetIdentifierAt(IReadOnlyList<Token> tokens, int offset, out Token token)
        {
            token = default;
            int i = FloorIndex(tokens, offset);
            if (i < 0) return false;

            // The token starting at/just before the cursor.
            var candidate = tokens[i];
            if (IsNameLike(candidate.Type) && offset <= candidate.PositionEnd.Idx)
            {
                token = candidate;
                return true;
            }

            // Cursor at the trailing edge of the previous token (e.g. "foo|").
            if (i > 0)
            {
                var prev = tokens[i - 1];
                if (IsNameLike(prev.Type) && offset == prev.PositionEnd.Idx)
                {
                    token = prev;
                    return true;
                }
            }
            return false;
        }

        public static bool IsNameLike(TokenType type)
            => type == TokenType.IDENTIFIER || type == TokenType.KEYWORD;

        /// <summary>
        /// Skips backwards over NEWLINE tokens to find the previous meaningful token
        /// index, or -1. Used by completion / signature help to read left context.
        /// </summary>
        public static int PreviousMeaningful(IReadOnlyList<Token> tokens, int fromIndex)
        {
            for (int i = fromIndex; i >= 0; i--)
            {
                if (tokens[i].Type != TokenType.NEWLINE) return i;
            }
            return -1;
        }
    }
}

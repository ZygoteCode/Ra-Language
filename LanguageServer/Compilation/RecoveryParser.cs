using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.LanguageServer.Compilation
{
    /// <summary>
    /// Tooling-side error recovery. When the whole-file parse trips on broken input,
    /// the core parser can abandon everything after the failure. This splits the token
    /// stream into top-level declaration segments (boundaries detected at bracket
    /// depth 0) and parses each in isolation, so a single malformed declaration no
    /// longer blanks the outline for the rest of the file. It mutates nothing in the
    /// core parser and reuses the original tokens, so recovered nodes keep exact
    /// source positions.
    /// </summary>
    public static class RecoveryParser
    {
        /// <summary>
        /// Attempt to rebuild a richer top-level scope from <paramref name="tokens"/>.
        /// Returns null when segmentation would not help (fewer than two segments).
        /// </summary>
        public static ScopeNode? TryRecover(IReadOnlyList<Token> tokens, out List<Diagnostic> diagnostics)
        {
            diagnostics = new List<Diagnostic>();
            if (tokens == null || tokens.Count == 0) return null;

            var boundaries = FindSegmentBoundaries(tokens);
            if (boundaries.Count < 2) return null; // single segment ≡ whole-file parse; no gain

            Token eof = FindEof(tokens);
            var merged = new List<AstNode>();

            for (int b = 0; b < boundaries.Count; b++)
            {
                int start = boundaries[b];
                int end = b + 1 < boundaries.Count ? boundaries[b + 1] : tokens.Count;
                var slice = SliceWithEof(tokens, start, end, eof);
                if (slice.Count <= 1) continue; // only EOF

                try
                {
                    var parser = new RaLanguage.Parser.Parser(slice);
                    var result = parser.Parse();
                    diagnostics.AddRange(result.Diagnostics.Diagnostics);
                    if (result.Node is ScopeNode scope)
                    {
                        merged.AddRange(scope.Nodes);
                    }
                    else if (result.Node != null)
                    {
                        merged.Add(result.Node);
                    }
                }
                catch
                {
                    // Segment unrecoverable; its diagnostics already came from the main parse.
                }
            }

            if (merged.Count == 0) return null;

            Position startPos = merged[0].PositionStart;
            Position endPos = merged[merged.Count - 1].PositionEnd;
            return new ScopeNode(merged, startPos, endPos);
        }

        private static List<int> FindSegmentBoundaries(IReadOnlyList<Token> tokens)
        {
            var boundaries = new List<int>();
            int depth = 0;
            int prevMeaningful = -1;

            for (int i = 0; i < tokens.Count; i++)
            {
                var type = tokens[i].Type;
                switch (type)
                {
                    case TokenType.LPAREN:
                    case TokenType.LSQUARE:
                    case TokenType.LBRACKET:
                        depth++;
                        break;
                    case TokenType.RPAREN:
                    case TokenType.RSQUARE:
                    case TokenType.RBRACKET:
                        if (depth > 0) depth--;
                        break;
                }

                if (type == TokenType.NEWLINE) continue;
                if (type == TokenType.EOF) break;

                bool atStatementStart = prevMeaningful < 0
                    || tokens[prevMeaningful].Type == TokenType.NEWLINE
                    || tokens[prevMeaningful].Type == TokenType.RBRACKET;

                if (depth == 0 && atStatementStart && IsDeclarationStart(tokens[i]))
                {
                    boundaries.Add(i);
                }

                prevMeaningful = i;
            }

            return boundaries;
        }

        private static bool IsDeclarationStart(in Token token)
            => token.Type == TokenType.KEYWORD && token.Value is Keyword kw && s_declStart.Contains(kw);

        private static List<Token> SliceWithEof(IReadOnlyList<Token> tokens, int start, int end, Token eof)
        {
            var list = new List<Token>(end - start + 1);
            for (int i = start; i < end; i++)
            {
                if (tokens[i].Type == TokenType.EOF) continue;
                list.Add(tokens[i]);
            }
            list.Add(eof);
            return list;
        }

        private static Token FindEof(IReadOnlyList<Token> tokens)
        {
            for (int i = tokens.Count - 1; i >= 0; i--)
                if (tokens[i].Type == TokenType.EOF) return tokens[i];
            // Synthesize one anchored at the last token's end.
            var last = tokens[tokens.Count - 1];
            return new Token(TokenType.EOF, null, last.PositionEnd);
        }

        private static readonly HashSet<Keyword> s_declStart = new()
        {
            // Modifiers (a declaration can start with one).
            Keyword.Pub, Keyword.Static, Keyword.Abstract, Keyword.Final, Keyword.Override, Keyword.Async,
            // Declaration keywords.
            Keyword.Fn, Keyword.Class, Keyword.Struct, Keyword.Enum, Keyword.Interface, Keyword.Trait,
            Keyword.Record, Keyword.Annotation, Keyword.Delegate, Keyword.Namespace, Keyword.Using,
            Keyword.Extend, Keyword.Import, Keyword.From, Keyword.Let, Keyword.Var, Keyword.Const,
        };
    }
}

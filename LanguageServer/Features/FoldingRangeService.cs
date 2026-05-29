using System;
using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Text;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Folding ranges from three sources: balanced multi-line bracket pairs (blocks,
    /// lists, argument lists), a contiguous import header, and explicit
    /// <c>region</c>/<c>endregion</c> comment markers.
    /// </summary>
    public sealed class FoldingRangeService : IFoldingRangeService
    {
        public FoldingRange[] Compute(RaDocument document)
        {
            var compilation = document.GetCompilation();
            var tokens = compilation.Tokens;
            var lines = document.Document.Lines;
            var ranges = new List<FoldingRange>();

            // 1. Bracket-delimited blocks.
            foreach (var pair in BracketMatcher.Match(tokens))
            {
                int openLine = lines.OffsetToPosition(tokens[pair.OpenIndex].PositionStart.Idx).Line;
                int closeLine = lines.OffsetToPosition(tokens[pair.CloseIndex].PositionStart.Idx).Line;
                if (closeLine > openLine)
                {
                    ranges.Add(new FoldingRange { StartLine = openLine, EndLine = closeLine - 1 });
                }
            }

            // 2. Import header (run of import/using lines from first to last).
            CollectImportRegion(tokens, lines, ranges);

            // 3. region / endregion comment markers.
            CollectCommentRegions(lines, ranges);

            return ranges.ToArray();
        }

        private static void CollectImportRegion(IReadOnlyList<Token> tokens, LineIndex lines, List<FoldingRange> ranges)
        {
            int firstLine = -1, lastLine = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Type == TokenType.KEYWORD && tokens[i].Value is Keyword kw &&
                    (kw == Keyword.Import || kw == Keyword.Using || kw == Keyword.From))
                {
                    int line = lines.OffsetToPosition(tokens[i].PositionStart.Idx).Line;
                    if (firstLine < 0) firstLine = line;
                    lastLine = line;
                }
            }
            if (firstLine >= 0 && lastLine > firstLine)
            {
                ranges.Add(new FoldingRange { StartLine = firstLine, EndLine = lastLine, Kind = FoldingRangeKind.Imports });
            }
        }

        private static void CollectCommentRegions(LineIndex lines, List<FoldingRange> ranges)
        {
            string text = lines.Text;
            var open = new Stack<int>();
            for (int line = 0; line < lines.LineCount; line++)
            {
                int start = lines.LineStart(line);
                int end = LineContentEnd(lines, line);
                string content = text.Substring(start, Math.Max(0, end - start)).Trim();
                string? marker = CommentBody(content);
                if (marker == null) continue;

                if (marker.StartsWith("region", StringComparison.OrdinalIgnoreCase))
                {
                    open.Push(line);
                }
                else if (marker.StartsWith("endregion", StringComparison.OrdinalIgnoreCase) && open.Count > 0)
                {
                    int startLine = open.Pop();
                    if (line > startLine)
                    {
                        ranges.Add(new FoldingRange { StartLine = startLine, EndLine = line, Kind = FoldingRangeKind.Region });
                    }
                }
            }
        }

        /// <summary>If the line is a single-line comment, returns its body trimmed; else null.</summary>
        private static string? CommentBody(string trimmed)
        {
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) return trimmed.Substring(2).Trim();
            if (trimmed.StartsWith("---", StringComparison.Ordinal)) return trimmed.Substring(3).Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) return trimmed.Substring(1).Trim();
            return null;
        }

        private static int LineContentEnd(LineIndex lines, int line)
        {
            int end = lines.LineEndExclusive(line);
            string text = lines.Text;
            if (end > 0 && end - 1 < text.Length && text[end - 1] == '\n') end--;
            if (end > 0 && end - 1 < text.Length && text[end - 1] == '\r') end--;
            return end;
        }
    }
}

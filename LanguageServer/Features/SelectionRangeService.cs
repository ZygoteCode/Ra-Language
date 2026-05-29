using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Smart-select ranges. For each requested position it builds a nesting chain
    /// (innermost token → enclosing bracket pairs → whole document). Bracket pairs
    /// that contain a common offset are inherently nested, so ordering by span size
    /// yields a valid parent chain.
    /// </summary>
    public sealed class SelectionRangeService : ISelectionRangeService
    {
        public SelectionRange[] Compute(RaDocument document, Position[] positions)
        {
            var compilation = document.GetCompilation();
            var tokens = compilation.Tokens;
            var doc = document.Document;
            int length = doc.Text.Length;
            var pairs = BracketMatcher.Match(tokens);

            var result = new SelectionRange[positions.Length];
            for (int p = 0; p < positions.Length; p++)
            {
                int offset = doc.OffsetAt(positions[p]);
                result[p] = BuildChain(tokens, pairs, doc, length, offset);
            }
            return result;
        }

        private static SelectionRange BuildChain(
            IReadOnlyList<Token> tokens,
            List<BracketPair> pairs,
            Workspace.TextDocument doc,
            int length,
            int offset)
        {
            var spans = new List<(int Start, int End)>();
            var seen = new HashSet<long>();

            void Add(int start, int end)
            {
                if (end < start) return;
                if (offset < start || offset > end) return;
                long key = ((long)start << 32) ^ (uint)end;
                if (seen.Add(key)) spans.Add((start, end));
            }

            // Innermost: the token under the cursor.
            int ti = TokenLocator.FloorIndex(tokens, offset);
            if (ti >= 0 && TokenLocator.Contains(tokens[ti], offset))
            {
                Add(tokens[ti].PositionStart.Idx, tokens[ti].PositionEnd.Idx);
            }

            // Enclosing bracket pairs.
            foreach (var pair in pairs)
            {
                int start = tokens[pair.OpenIndex].PositionStart.Idx;
                int end = tokens[pair.CloseIndex].PositionEnd.Idx;
                Add(start, end);
            }

            // Whole document as the outermost range.
            Add(0, length);

            spans.Sort(static (a, b) => (a.End - a.Start).CompareTo(b.End - b.Start));

            SelectionRange? parent = null;
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                parent = new SelectionRange
                {
                    Range = doc.RangeOf(spans[i].Start, spans[i].End),
                    Parent = parent,
                };
            }
            return parent ?? new SelectionRange { Range = doc.RangeOf(offset, offset) };
        }
    }
}

using RaLanguage.Lexer;
using System.Text;
using System.Runtime.CompilerServices;

namespace RaLanguage.Utilities
{
    public static class Utils
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string StringWithArrows(string text, Position positionStart, Position positionEnd)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            ReadOnlySpan<char> span = text.AsSpan();
            var sb = new StringBuilder(capacity: Math.Min(1024, text.Length + 64));

            int startSearchIdx = Math.Min(positionStart.Idx, span.Length - 1);
            int idxLastNewline = span.Slice(0, startSearchIdx + 1).LastIndexOf('\n');
            int idxStart = (idxLastNewline == -1) ? 0 : (idxLastNewline + 1);

            int nextNewlineRel = span.Slice(idxStart).IndexOf('\n');
            int idxEnd = (nextNewlineRel < 0) ? span.Length : (idxStart + nextNewlineRel);

            int lineCount = Math.Max(1, positionEnd.Ln - positionStart.Ln + 1);

            for (int lineNumber = 0; lineNumber < lineCount; lineNumber++)
            {
                if (idxStart > idxEnd || idxStart >= span.Length) break;

                ReadOnlySpan<char> lineSpan = span.Slice(idxStart, idxEnd - idxStart);

                for (int i = 0; i < lineSpan.Length; i++)
                {
                    char ch = lineSpan[i];
                    if (ch != '\t')
                        sb.Append(ch);
                }
                sb.AppendLine();

                int colStart = (lineNumber == 0) ? positionStart.Col : 0;
                int colEnd = (lineNumber == lineCount - 1) ? positionEnd.Col : (GetFilteredLength(lineSpan) - 1);

                if (colStart < 0) colStart = 0;
                if (colEnd < colStart) colEnd = colStart;

                int caretCount = Math.Max(1, colEnd - colStart + 1);

                if (colStart > 0) sb.Append(' ', colStart);
                sb.Append('^', caretCount);
                sb.AppendLine();

                idxStart = idxEnd + 1;
                if (idxStart >= span.Length) break;

                nextNewlineRel = span.Slice(idxStart).IndexOf('\n');
                idxEnd = (nextNewlineRel < 0) ? span.Length : (idxStart + nextNewlineRel);
            }

            return sb.ToString();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetFilteredLength(ReadOnlySpan<char> s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] != '\t') n++;
            return n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHexDigit(char c) => (uint)(c - '0') <= (uint)('9' - '0') || ((uint)(c | 0x20) >= (uint)('a') && (uint)(c | 0x20) <= (uint)('f'));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsBinaryDigit(char c) => c == '0' || c == '1';

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsOctalDigit(char c) => (uint)(c - '0') <= (uint)('7' - '0');
    }
}
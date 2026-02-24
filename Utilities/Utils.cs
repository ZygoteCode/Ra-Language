using RaLanguage.Lexer;
using System.Text;

namespace RaLanguage.Utilities
{
    public static class Utils
    {
        public static string StringWithArrows(string text, Position positionStart, Position positionEnd)
        {
            var result = new StringBuilder();
            int idxStart = text.LastIndexOf('\n', Math.Min(positionStart.Idx, text.Length - 1));

            if (idxStart == -1)
            {
                idxStart = 0;
            }
            else
            {
                idxStart += 1;
            }

            int idxEnd = text.IndexOf('\n', idxStart);

            if (idxEnd < 0)
            {
                idxEnd = text.Length;
            }

            int lineCount = positionEnd.Ln - positionStart.Ln + 1;

            for (int i = 0; i < lineCount; i++)
            {
                if (idxStart > idxEnd)
                {
                    break;
                }

                string line = text.Substring(idxStart, idxEnd - idxStart);

                int colStart = (i == 0) ? positionStart.Col : 0;
                int colEnd = (i == lineCount - 1) ? positionEnd.Col : line.Length - 1;

                result.AppendLine(line);
                result.Append(new string(' ', colStart));
                result.AppendLine(new string('^', Math.Max(1, colEnd - colStart)));

                idxStart = idxEnd + 1;

                if (idxStart >= text.Length)
                {
                    break;
                }

                idxEnd = text.IndexOf('\n', idxStart);

                if (idxEnd < 0)
                {
                    idxEnd = text.Length;
                }
            }

            return result.ToString().Replace("\t", "");
        }

        public static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        public static bool IsBinaryDigit(char c) => c == '0' || c == '1';
        public static bool IsOctalDigit(char c) => c >= '0' && c <= '7';
    }
}
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RaLanguage.Lexer;

namespace RaLanguage.Errors
{
    /// <summary>
    /// Renders <see cref="Diagnostic"/> instances in a modern compiler-style format
    /// (file:line:col header, multi-line source window with caret markers,
    /// secondary labels, help/note hints, traceback frames, and chained causes).
    /// </summary>
    public static class DiagnosticRenderer
    {
        public const int TabWidth = 4;
        public const int ContextLinesBefore = 2;
        public const int ContextLinesAfter = 1;
        public const int MaxLineLength = 240;

        private static int _colorState = -1;

        public static bool ColorEnabled
        {
            get
            {
                int s = _colorState;
                if (s != -1) return s == 1;
                bool enabled = DetectColorSupport();
                _colorState = enabled ? 1 : 0;
                return enabled;
            }
        }

        public static void DisableColor() => _colorState = 0;
        public static void EnableColor() => _colorState = TryEnableVirtualTerminal() ? 1 : 0;

        public static string Render(Diagnostic diagnostic)
        {
            var sb = new StringBuilder(512);
            RenderInto(sb, diagnostic, depth: 0);
            return sb.ToString().TrimEnd('\n');
        }

        public static string RenderMany(IEnumerable<Diagnostic> diagnostics)
        {
            var sb = new StringBuilder(1024);
            bool first = true;
            foreach (var d in diagnostics)
            {
                if (!first) sb.Append('\n');
                RenderInto(sb, d, depth: 0);
                first = false;
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static void RenderInto(StringBuilder sb, Diagnostic d, int depth)
        {
            RenderHeader(sb, d);

            if (d.PrimarySpan.IsValid && !string.IsNullOrEmpty(d.PrimarySpan.Start.Ftxt))
            {
                RenderSourceWindow(sb, d);
            }
            else if (d.PrimarySpan.IsValid)
            {
                sb.Append("  --> ").Append(d.PrimarySpan).Append('\n');
            }

            if (!string.IsNullOrEmpty(d.Message) && d.Message != d.Title)
            {
                AppendTaggedLine(sb, "  = ", "note", d.Message!, AnsiCyan);
            }

            if (!string.IsNullOrEmpty(d.Help))
            {
                AppendTaggedLine(sb, "  = ", "help", d.Help!, AnsiGreen);
            }

            if (d.Notes != null)
            {
                foreach (var note in d.Notes)
                {
                    if (string.IsNullOrEmpty(note)) continue;
                    AppendTaggedLine(sb, "  = ", "note", note, AnsiCyan);
                }
            }

            if (d.Traceback != null && d.Traceback.Count > 0)
            {
                sb.Append('\n');
                AppendColored(sb, "Traceback (most recent call last):", AnsiDim, bold: false);
                sb.Append('\n');
                for (int i = 0; i < d.Traceback.Count; i++)
                {
                    var frame = d.Traceback[i];
                    sb.Append("    ");
                    AppendColored(sb, "at ", AnsiDim, bold: false);
                    sb.Append(frame.DisplayName);
                    if (frame.Span.IsValid)
                    {
                        sb.Append("  ");
                        AppendColored(sb, "(" + frame.Span + ")", AnsiDim, bold: false);
                    }
                    sb.Append('\n');
                }
            }

            if (d.Cause != null)
            {
                sb.Append('\n');
                AppendColored(sb, "caused by:", AnsiBold, bold: true);
                sb.Append('\n');
                var inner = new StringBuilder(256);
                RenderInto(inner, d.Cause, depth + 1);
                AppendIndented(sb, inner.ToString(), "  ");
            }
        }

        private static void RenderHeader(StringBuilder sb, Diagnostic d)
        {
            string word = SeverityWord(d.Severity);
            string color = SeverityColor(d.Severity);

            AppendColored(sb, word, color, bold: true);
            if (!d.Code.IsEmpty)
            {
                AppendColored(sb, "[" + d.Code.Id + "]", color, bold: true);
            }

            sb.Append(": ");

            string title = d.Title ?? string.Empty;
            if (!string.IsNullOrEmpty(d.Category) && d.Code.IsEmpty)
            {
                AppendColored(sb, d.Category + ": ", AnsiBold, bold: true);
            }

            AppendColored(sb, title, AnsiBold, bold: true);
            sb.Append('\n');
        }

        private static void RenderSourceWindow(StringBuilder sb, Diagnostic d)
        {
            string text = d.PrimarySpan.Start.Ftxt;
            string fileName = string.IsNullOrEmpty(d.PrimarySpan.Start.Fn) ? "<input>" : d.PrimarySpan.Start.Fn;

            var lines = BuildLineIndex(text);
            if (lines.Count == 0) return;

            int startLine = Math.Max(0, Math.Min(d.PrimarySpan.Start.Ln, lines.Count - 1));
            int endLine = Math.Max(startLine, Math.Min(d.PrimarySpan.End.Ln, lines.Count - 1));

            int firstLine = Math.Max(0, startLine - ContextLinesBefore);
            int lastLine = Math.Min(lines.Count - 1, endLine + ContextLinesAfter);

            int maxDisplayed = lastLine + 1;
            int gutterWidth = CountDigits(maxDisplayed);
            string gutterPad = new string(' ', gutterWidth);

            AppendColored(sb, gutterPad + " --> ", AnsiCyan, bold: false);
            sb.Append(fileName).Append(':').Append(d.PrimarySpan.Start.Ln + 1).Append(':').Append(d.PrimarySpan.Start.Col + 1).Append('\n');

            AppendColored(sb, gutterPad + " |", AnsiCyan, bold: false);
            sb.Append('\n');

            string severityColor = SeverityColor(d.Severity);

            for (int ln = firstLine; ln <= lastLine; ln++)
            {
                var (lineStart, lineEnd) = lines[ln];
                string raw = text.Substring(lineStart, lineEnd - lineStart);
                string rendered = ExpandTabsAndTruncate(raw, out int[] colMap, out bool truncated);

                AppendColored(sb, PadLineNumber(ln + 1, gutterWidth), AnsiDim, bold: false);
                AppendColored(sb, " | ", AnsiCyan, bold: false);
                sb.Append(rendered);
                if (truncated) AppendColored(sb, " ...", AnsiDim, bold: false);
                sb.Append('\n');

                if (ln >= startLine && ln <= endLine)
                {
                    int srcLineLen = raw.Length;
                    int colStart = ln == startLine ? Clamp(d.PrimarySpan.Start.Col, 0, srcLineLen) : 0;
                    int colEnd = ln == endLine ? Clamp(d.PrimarySpan.End.Col, colStart, srcLineLen) : srcLineLen;
                    if (colEnd <= colStart) colEnd = Math.Min(srcLineLen, colStart + 1);

                    int renderedStart = MapCol(colMap, colStart);
                    int renderedEnd = MapCol(colMap, colEnd);
                    int caretCount = Math.Max(1, renderedEnd - renderedStart);

                    AppendColored(sb, gutterPad + " | ", AnsiCyan, bold: false);
                    sb.Append(' ', renderedStart);
                    AppendColored(sb, new string('^', caretCount), severityColor, bold: true);

                    string? label = (ln == endLine) ? (d.PrimaryLabel ?? string.Empty) : null;
                    if (!string.IsNullOrEmpty(label))
                    {
                        sb.Append(' ');
                        AppendColored(sb, label, severityColor, bold: false);
                    }
                    sb.Append('\n');
                }
            }

            AppendColored(sb, gutterPad + " |", AnsiCyan, bold: false);
            sb.Append('\n');

            if (d.SecondaryLabels != null)
            {
                foreach (var label in d.SecondaryLabels)
                {
                    if (!label.Span.IsValid) continue;
                    AppendColored(sb, gutterPad + " = ", AnsiCyan, bold: false);
                    AppendColored(sb, "note", AnsiCyan, bold: true);
                    sb.Append(": ");
                    if (!string.IsNullOrEmpty(label.Message))
                        sb.Append(label.Message).Append(' ');
                    sb.Append('(').Append(label.Span).Append(')').Append('\n');
                }
            }
        }

        private static void AppendTaggedLine(StringBuilder sb, string prefix, string tag, string body, string color)
        {
            AppendColored(sb, prefix, AnsiCyan, bold: false);
            AppendColored(sb, tag, color, bold: true);
            sb.Append(": ");
            sb.Append(body);
            sb.Append('\n');
        }

        private static void AppendIndented(StringBuilder sb, string block, string indent)
        {
            if (string.IsNullOrEmpty(block)) return;
            int i = 0;
            int len = block.Length;
            while (i < len)
            {
                sb.Append(indent);
                int nl = block.IndexOf('\n', i);
                if (nl < 0)
                {
                    sb.Append(block, i, len - i);
                    sb.Append('\n');
                    break;
                }
                sb.Append(block, i, nl - i + 1);
                i = nl + 1;
            }
        }

        private static List<(int Start, int End)> BuildLineIndex(string text)
        {
            var lines = new List<(int, int)>(Math.Max(8, text.Length / 32));
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    int end = i;
                    if (end > start && text[end - 1] == '\r') end--;
                    lines.Add((start, end));
                    start = i + 1;
                }
            }
            int lastEnd = text.Length;
            if (lastEnd > start && text[lastEnd - 1] == '\r') lastEnd--;
            if (start <= lastEnd) lines.Add((start, lastEnd));
            return lines;
        }

        private static string ExpandTabsAndTruncate(string line, out int[] colMap, out bool truncated)
        {
            colMap = new int[line.Length + 1];
            var sb = new StringBuilder(line.Length);
            int rendered = 0;
            truncated = false;
            for (int i = 0; i < line.Length; i++)
            {
                colMap[i] = rendered;
                char c = line[i];
                if (c == '\r') continue;
                if (c == '\t')
                {
                    int spaces = TabWidth - (rendered % TabWidth);
                    if (spaces <= 0) spaces = TabWidth;
                    sb.Append(' ', spaces);
                    rendered += spaces;
                }
                else if (c < 0x20)
                {
                    // Control characters: show as `\xNN` literal (length 4 visual width)
                    string repr = "\\x" + ((int)c).ToString("X2");
                    sb.Append(repr);
                    rendered += repr.Length;
                }
                else
                {
                    sb.Append(c);
                    rendered++;
                }

                if (rendered > MaxLineLength)
                {
                    truncated = true;
                    // Map remaining columns to the truncation point so carets stay sane
                    for (int j = i + 1; j <= line.Length; j++) colMap[j] = rendered;
                    return sb.ToString();
                }
            }
            colMap[line.Length] = rendered;
            return sb.ToString();
        }

        private static int MapCol(int[] colMap, int col)
        {
            if (colMap.Length == 0) return 0;
            if (col < 0) return 0;
            if (col >= colMap.Length) return colMap[colMap.Length - 1];
            return colMap[col];
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static int CountDigits(int n)
        {
            if (n < 10) return 1;
            if (n < 100) return 2;
            if (n < 1000) return 3;
            if (n < 10000) return 4;
            if (n < 100000) return 5;
            return n.ToString().Length;
        }

        private static string PadLineNumber(int n, int width)
        {
            string s = n.ToString();
            if (s.Length >= width) return s;
            return new string(' ', width - s.Length) + s;
        }

        private static string SeverityWord(DiagnosticSeverity s) => s switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Info => "note",
            DiagnosticSeverity.Note => "note",
            DiagnosticSeverity.Help => "help",
            _ => "diagnostic"
        };

        private static string SeverityColor(DiagnosticSeverity s) => s switch
        {
            DiagnosticSeverity.Error => AnsiRed,
            DiagnosticSeverity.Warning => AnsiYellow,
            DiagnosticSeverity.Info => AnsiCyan,
            DiagnosticSeverity.Note => AnsiCyan,
            DiagnosticSeverity.Help => AnsiGreen,
            _ => AnsiDim
        };

        // -------- ANSI helpers --------
        public const string AnsiReset = "\x1b[0m";
        public const string AnsiBold = "\x1b[1m";
        public const string AnsiDim = "\x1b[2m";
        public const string AnsiRed = "\x1b[31m";
        public const string AnsiYellow = "\x1b[33m";
        public const string AnsiCyan = "\x1b[36m";
        public const string AnsiGreen = "\x1b[32m";
        public const string AnsiMagenta = "\x1b[35m";

        private static void AppendColored(StringBuilder sb, string text, string color, bool bold)
        {
            if (string.IsNullOrEmpty(text)) { sb.Append(text); return; }
            if (!ColorEnabled) { sb.Append(text); return; }
            if (bold) sb.Append(AnsiBold);
            sb.Append(color);
            sb.Append(text);
            sb.Append(AnsiReset);
        }

        // -------- VT detection / enable (Windows) --------
        private static bool DetectColorSupport()
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return false;
                if (Console.IsOutputRedirected || Console.IsErrorRedirected) return false;
            }
            catch
            {
                return false;
            }
            return TryEnableVirtualTerminal();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        private static bool TryEnableVirtualTerminal()
        {
            if (!OperatingSystem.IsWindows()) return true;
            try
            {
                var handle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (handle == nint.Zero || handle == -1) return false;
                if (!GetConsoleMode(handle, out uint mode)) return false;
                if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0) return true;
                return SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
            }
            catch
            {
                return false;
            }
        }
    }
}

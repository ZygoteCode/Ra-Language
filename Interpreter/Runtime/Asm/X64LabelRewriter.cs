using System;
using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Rewrites NASM-style anonymous and numeric labels into unique identifiers
    /// before the main parser runs.
    ///
    /// `@@:` defines an anonymous label; `@f`/`@F` refers to the next one,
    /// `@b`/`@B` to the previous one.
    ///
    /// `1:` defines a numeric label (any non-negative decimal); `1f` and `1b`
    /// refer to the nearest forward / backward occurrence.
    /// </summary>
    public static class X64LabelRewriter
    {
        public static string Rewrite(string source)
        {
            var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var anonAt = new List<int>();
            var numericAt = new Dictionary<int, List<int>>();

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                int colon = FindLabelColon(trimmed);
                if (colon < 0) continue;
                var labelPart = trimmed.Substring(0, colon).Trim();
                if (labelPart == "@@") anonAt.Add(i);
                else if (IsAllDigits(labelPart))
                {
                    int n = int.Parse(labelPart);
                    if (!numericAt.TryGetValue(n, out var list)) numericAt[n] = list = new List<int>();
                    list.Add(i);
                }
            }

            var sb = new StringBuilder(source.Length);
            int anonCounter = 0;
            var numericCounters = new Dictionary<int, int>();
            string prevAnonName = "";

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var trimmed = raw.TrimStart();
                int colon = FindLabelColon(trimmed);

                string lineOut = raw;
                string? definedName = null;
                bool definedAnon = false;
                int definedNumeric = -1;

                if (colon >= 0)
                {
                    var labelPart = trimmed.Substring(0, colon).Trim();
                    if (labelPart == "@@")
                    {
                        string name = $"__asm_anon_{anonCounter}";
                        anonCounter++;
                        lineOut = raw.Replace(raw.TrimStart(), name + trimmed.Substring(colon));
                        definedName = name;
                        definedAnon = true;
                    }
                    else if (IsAllDigits(labelPart))
                    {
                        int n = int.Parse(labelPart);
                        int seq = numericCounters.TryGetValue(n, out var c) ? c : 0;
                        numericCounters[n] = seq + 1;
                        string name = $"__asm_num_{n}_{seq}";
                        lineOut = raw.Replace(raw.TrimStart(), name + trimmed.Substring(colon));
                        definedName = name;
                        definedNumeric = n;
                    }
                }

                lineOut = ReplaceLabelRefs(lineOut, i, anonAt, anonCounter, numericAt, numericCounters);

                if (definedAnon && definedName != null) prevAnonName = definedName;
                sb.Append(lineOut).Append('\n');
            }

            return sb.ToString();
        }

        private static string ReplaceLabelRefs(string line, int currentLineIdx, List<int> anonAt, int anonsBefore, Dictionary<int, List<int>> numericAt, Dictionary<int, int> numCountersBefore)
        {
            var sb = new StringBuilder(line.Length);
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];

                if (c == '@' && i + 1 < line.Length && (line[i + 1] == 'f' || line[i + 1] == 'F' || line[i + 1] == 'b' || line[i + 1] == 'B') && (i + 2 == line.Length || !IsIdentCont(line[i + 2])) && (i == 0 || !IsIdentCont(line[i - 1])))
                {
                    bool forward = line[i + 1] == 'f' || line[i + 1] == 'F';
                    string? name = ResolveAnon(currentLineIdx, anonAt, forward);
                    if (name != null)
                    {
                        sb.Append(name);
                        i += 2;
                        continue;
                    }
                }

                if (char.IsDigit(c) && i + 1 < line.Length && (line[i + 1] == 'f' || line[i + 1] == 'F' || line[i + 1] == 'b' || line[i + 1] == 'B') && (i + 2 == line.Length || !IsIdentCont(line[i + 2])) && (i == 0 || !IsIdentCont(line[i - 1])))
                {
                    int n = c - '0';
                    bool forward = line[i + 1] == 'f' || line[i + 1] == 'F';
                    string? name = ResolveNumeric(currentLineIdx, n, forward, numericAt);
                    if (name != null)
                    {
                        sb.Append(name);
                        i += 2;
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static string? ResolveAnon(int from, List<int> anonAt, bool forward)
        {
            if (forward)
            {
                for (int k = 0; k < anonAt.Count; k++)
                    if (anonAt[k] > from) return $"__asm_anon_{k}";
                return null;
            }
            for (int k = anonAt.Count - 1; k >= 0; k--)
                if (anonAt[k] < from) return $"__asm_anon_{k}";
            return null;
        }

        private static string? ResolveNumeric(int from, int n, bool forward, Dictionary<int, List<int>> numericAt)
        {
            if (!numericAt.TryGetValue(n, out var list)) return null;
            if (forward)
            {
                for (int k = 0; k < list.Count; k++)
                    if (list[k] > from) return $"__asm_num_{n}_{k}";
                return null;
            }
            for (int k = list.Count - 1; k >= 0; k--)
                if (list[k] < from) return $"__asm_num_{n}_{k}";
            return null;
        }

        private static int FindLabelColon(string s)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '[') depth++;
                else if (c == ']') depth = Math.Max(0, depth - 1);
                else if (c == ':' && depth == 0)
                {
                    if (i + 1 < s.Length && s[i + 1] == ':') return -1;
                    return i;
                }
                else if (c == ',' || c == ' ' || c == '\t') return -1;
            }
            return -1;
        }

        private static bool IsAllDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (var c in s) if (!char.IsDigit(c)) return false;
            return true;
        }

        private static bool IsIdentCont(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
    }
}

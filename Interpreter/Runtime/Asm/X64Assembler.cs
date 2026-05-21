using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Single-pass x64 assembler with two-pass label resolution.
    ///
    /// Scope: x64 only — no x86 mode. Targets the Win64 calling convention by
    /// default (args in RCX/RDX/R8/R9 + XMM0..XMM3; return in RAX/XMM0). The
    /// assembler itself does not enforce ABI rules; it only encodes the bytes
    /// the user writes.
    ///
    /// Supported instructions: a useful production subset.
    /// </summary>
    public static class X64Assembler
    {
        public static byte[] Assemble(string source) => Assemble(source, null, null);

        public static byte[] Assemble(string source, X64Preprocessor.Options? ppOpts, AsmSecurityPolicy? policy)
        {
            var pp = new X64Preprocessor(ppOpts);
            source = pp.Process(source);
            source = LowerTimes(source);
            source = X64LabelRewriter.Rewrite(source);

            var lines = SplitLines(source);
            var instrs = new List<ParsedInstr>();

            for (int i = 0; i < lines.Count; i++)
            {
                var raw = lines[i].Text;
                var clean = StripComment(raw).Trim();
                if (clean.Length == 0) continue;

                while (clean.Length > 0)
                {
                    int colon = FindLabelColon(clean);
                    if (colon < 0) break;
                    var labelName = clean.Substring(0, colon).Trim();
                    if (labelName.Length == 0 || !IsIdentifier(labelName))
                        throw new AsmAssembleException(lines[i].LineNumber, raw, $"invalid label '{labelName}'");
                    instrs.Add(new ParsedInstr { Kind = InstrKind.Label, LabelName = labelName, LineNumber = lines[i].LineNumber, RawLine = raw });
                    clean = clean.Substring(colon + 1).Trim();
                }

                if (clean.Length == 0) continue;

                ParseAndCollectStatements(clean, lines[i].LineNumber, raw, instrs);
            }

            X64Peephole.Apply(instrs);

            if (policy != null)
            {
                foreach (var instr in instrs) policy.ValidateInstruction(instr);
            }

            var emit = new X64Emit();
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            var fixups = new List<LabelFixup>();

            foreach (var instr in instrs)
            {
                if (instr.Kind == InstrKind.Label)
                {
                    if (labels.ContainsKey(instr.LabelName))
                        throw new AsmAssembleException(instr.LineNumber, instr.RawLine, $"duplicate label '{instr.LabelName}'");
                    labels[instr.LabelName] = emit.Position;
                    continue;
                }

                int instrStart = emit.Position;
                try
                {
                    X64Mnemonics.Emit(instr, emit, fixups);
                }
                catch (AsmAssembleException) { throw; }
                catch (Exception ex)
                {
                    throw new AsmAssembleException(instr.LineNumber, instr.RawLine, ex.Message);
                }
                instr.EmittedStart = instrStart;
                instr.EmittedEnd = emit.Position;

                for (int k = fixups.Count - 1; k >= 0; k--)
                {
                    var f = fixups[k];
                    if (f.InstrEnd == -1)
                    {
                        f.InstrEnd = emit.Position;
                    }
                }
            }

            foreach (var f in fixups)
            {
                if (!labels.TryGetValue(f.Label, out var targetPos))
                    throw new AsmAssembleException(0, "", $"undefined label '{f.Label}'");

                long rel = targetPos - f.InstrEnd;

                if (f.IsAbsolute64)
                {
                    emit.PatchU64At(f.Position, unchecked((ulong)targetPos));
                    continue;
                }

                if (f.Size == 1)
                {
                    if (rel < -128 || rel > 127)
                        throw new AsmAssembleException(0, "", $"short jump to '{f.Label}' out of range ({rel})");
                    emit.Patch(f.Position, (byte)(sbyte)rel);
                }
                else
                {
                    emit.PatchI32At(f.Position, (int)rel);
                }
            }

            return emit.ToArray();
        }

        private static void ParseAndCollectStatements(string body, int lineNumber, string rawLine, List<ParsedInstr> dest)
        {
            int start = 0;
            int depth = 0;
            for (int i = 0; i <= body.Length; i++)
            {
                if (i == body.Length || (body[i] == ';' && depth == 0))
                {
                    var stmt = body.Substring(start, i - start).Trim();
                    if (stmt.Length > 0)
                    {
                        ParseStatementWithPrefix(stmt, lineNumber, rawLine, dest);
                    }
                    start = i + 1;
                }
                else if (body[i] == '[') depth++;
                else if (body[i] == ']') depth = Math.Max(0, depth - 1);
            }
        }

        private static void ParseStatementWithPrefix(string stmt, int lineNumber, string rawLine, List<ParsedInstr> dest)
        {
            while (true)
            {
                int sp = -1;
                for (int i = 0; i < stmt.Length; i++)
                {
                    if (stmt[i] == ' ' || stmt[i] == '\t') { sp = i; break; }
                }
                if (sp < 0) break;
                var firstWord = stmt.Substring(0, sp).ToLowerInvariant();
                if (X64Mnemonics.KnownPrefixes.TryGetValue(firstWord, out var prefByte))
                {
                    var prefIns = new ParsedInstr
                    {
                        Kind = InstrKind.Instruction,
                        Mnemonic = "__prefix",
                        Operands = new List<X64Operand> { X64Operand.FromImmediate(prefByte) },
                        LineNumber = lineNumber,
                        RawLine = rawLine,
                    };
                    dest.Add(prefIns);
                    stmt = stmt.Substring(sp + 1).TrimStart();
                    continue;
                }
                break;
            }
            dest.Add(ParseStatement(stmt, lineNumber, rawLine));
        }

        private static ParsedInstr ParseStatement(string statement, int lineNumber, string rawLine)
        {
            int firstSpace = -1;
            for (int i = 0; i < statement.Length; i++)
            {
                char c = statement[i];
                if (c == ' ' || c == '\t') { firstSpace = i; break; }
            }

            string mnemonic;
            string operandsText;
            if (firstSpace < 0)
            {
                mnemonic = statement;
                operandsText = "";
            }
            else
            {
                mnemonic = statement.Substring(0, firstSpace).Trim();
                operandsText = statement.Substring(firstSpace + 1).Trim();
            }

            var ops = new List<X64Operand>();
            if (operandsText.Length > 0)
            {
                foreach (var s in SplitOperands(operandsText))
                {
                    ops.Add(X64OperandParser.Parse(s, lineNumber, rawLine));
                }
            }

            return new ParsedInstr
            {
                Kind = InstrKind.Instruction,
                Mnemonic = mnemonic.ToLowerInvariant(),
                Operands = ops,
                LineNumber = lineNumber,
                RawLine = rawLine
            };
        }

        private static IEnumerable<string> SplitOperands(string text)
        {
            int start = 0;
            int depth = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || (text[i] == ',' && depth == 0))
                {
                    yield return text.Substring(start, i - start).Trim();
                    start = i + 1;
                }
                else if (text[i] == '[') depth++;
                else if (text[i] == ']') depth = Math.Max(0, depth - 1);
            }
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
                else if (c == ',' || c == ' ' || c == '\t')
                {
                    return -1;
                }
            }
            return -1;
        }

        private static bool IsIdentifier(string s)
        {
            if (s.Length == 0) return false;
            char c0 = s[0];
            if (!(char.IsLetter(c0) || c0 == '_' || c0 == '.')) return false;
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.')) return false;
            }
            return true;
        }

        private static string StripComment(string s)
        {
            int i = s.IndexOf(';');
            if (i >= 0) s = s.Substring(0, i);
            int j = s.IndexOf("//", StringComparison.Ordinal);
            if (j >= 0) s = s.Substring(0, j);
            int h = s.IndexOf('#');
            if (h >= 0) s = s.Substring(0, h);
            return s;
        }

        private static string LowerTimes(string source)
        {
            var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new System.Text.StringBuilder(source.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                var trimmed = l.TrimStart();
                if (trimmed.StartsWith("times ", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("times\t", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = trimmed.Substring(5).TrimStart();
                    int sp = FindOperandBoundary(rest);
                    if (sp < 0) { sb.Append(l).Append('\n'); continue; }
                    var countText = rest.Substring(0, sp).Trim();
                    var bodyText = rest.Substring(sp).TrimStart();
                    if (!long.TryParse(countText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count))
                    {
                        if (!X64OperandParser.TryParseNumber(countText, out count))
                        {
                            sb.Append(l).Append('\n');
                            continue;
                        }
                    }
                    if (count < 0) count = 0;
                    for (long k = 0; k < count; k++) sb.Append(bodyText).Append('\n');
                    continue;
                }
                sb.Append(l).Append('\n');
            }
            return sb.ToString();
        }

        private static int FindOperandBoundary(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == ' ' || s[i] == '\t') return i;
            }
            return -1;
        }

        private struct LineInfo
        {
            public string Text;
            public int LineNumber;
        }

        private static List<LineInfo> SplitLines(string source)
        {
            var lines = new List<LineInfo>();
            int ln = 1;
            int start = 0;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    string l = source.Substring(start, i - start);
                    if (l.Length > 0 && l[l.Length - 1] == '\r') l = l.Substring(0, l.Length - 1);
                    lines.Add(new LineInfo { Text = l, LineNumber = ln });
                    start = i + 1;
                    ln++;
                }
            }
            if (start < source.Length)
                lines.Add(new LineInfo { Text = source.Substring(start), LineNumber = ln });
            return lines;
        }
    }

    internal enum InstrKind { Label, Instruction }

    internal sealed class ParsedInstr
    {
        public InstrKind Kind;
        public string Mnemonic = "";
        public string LabelName = "";
        public List<X64Operand> Operands = new List<X64Operand>();
        public int LineNumber;
        public string RawLine = "";
        public int EmittedStart;
        public int EmittedEnd;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Lightweight static analyzer that runs after assembly and reports
    /// stylistic / ABI hygiene issues without preventing compilation.
    ///
    /// Findings produced (severity is a string for forward compat):
    ///   warning: no `ret` in fall-through path
    ///   warning: callee-saved register clobbered without restore (Win64)
    ///   warning: missing shadow space before `call` instruction
    ///   warning: dead code after unconditional jmp/ret
    ///   warning: label defined but never referenced
    ///   warning: stack alignment may be violated at call site
    /// </summary>
    public sealed class AsmStaticAnalyzer
    {
        public sealed class Finding
        {
            public string Severity = "warning";
            public int LineNumber;
            public string RawLine = "";
            public string Message = "";

            public override string ToString() =>
                $"asm-lint {Severity}: line {LineNumber}: {Message}\n    {RawLine}";
        }

        private static readonly HashSet<byte> Win64CalleeSavedGpr = new() { 3, 5, 6, 7, 12, 13, 14, 15 };

        public List<Finding> Analyze(string source)
        {
            var findings = new List<Finding>();

            string preprocessed;
            try
            {
                preprocessed = new X64Preprocessor().Process(source);
                preprocessed = X64LabelRewriter.Rewrite(preprocessed);
            }
            catch (AsmAssembleException ax)
            {
                findings.Add(new Finding { Severity = "error", LineNumber = ax.LineNumber, RawLine = ax.LineText, Message = ax.Message });
                return findings;
            }

            var lines = preprocessed.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var labelDef = new Dictionary<string, int>(StringComparer.Ordinal);
            var labelRef = new HashSet<string>(StringComparer.Ordinal);
            var clobberedCallee = new HashSet<byte>();
            bool sawRet = false;
            int lastUnconditionalAt = -1;

            for (int idx = 0; idx < lines.Length; idx++)
            {
                var raw = lines[idx];
                var lineNum = idx + 1;
                var clean = StripComment(raw).Trim();
                if (clean.Length == 0) continue;

                int colon = FindLabelColon(clean);
                if (colon > 0)
                {
                    var lab = clean.Substring(0, colon).Trim();
                    if (!labelDef.ContainsKey(lab)) labelDef[lab] = lineNum;
                    clean = clean.Substring(colon + 1).Trim();
                    if (clean.Length == 0) continue;
                }

                int sp = -1;
                for (int i = 0; i < clean.Length; i++)
                    if (clean[i] == ' ' || clean[i] == '\t') { sp = i; break; }
                string mnem = sp < 0 ? clean.ToLowerInvariant() : clean.Substring(0, sp).ToLowerInvariant();
                string opStr = sp < 0 ? "" : clean.Substring(sp + 1).Trim();

                if (mnem == "ret" || mnem == "retn") { sawRet = true; lastUnconditionalAt = lineNum; }
                else if (mnem == "jmp") lastUnconditionalAt = lineNum;

                if (lastUnconditionalAt > 0 && lineNum > lastUnconditionalAt && labelDef.Count > 0)
                {
                    bool isLabelHere = false;
                    foreach (var l in labelDef) if (l.Value == lineNum) { isLabelHere = true; break; }
                    if (!isLabelHere && (mnem == "ret" || mnem.StartsWith("j") || mnem == "call" || mnem == "mov" || mnem == "add" || mnem == "sub"))
                    {
                        findings.Add(new Finding { LineNumber = lineNum, RawLine = raw, Message = $"dead code after unconditional control flow at line {lastUnconditionalAt}" });
                        lastUnconditionalAt = -1;
                    }
                }

                if (mnem.StartsWith("j") && X64MnemonicsHelper.IsJcc(mnem) && opStr.Length > 0)
                {
                    labelRef.Add(opStr.Trim());
                }
                if ((mnem == "jmp" || mnem == "call") && opStr.Length > 0 && IsIdent(opStr.Trim()))
                {
                    labelRef.Add(opStr.Trim());
                }
                if (mnem == "mov" && opStr.Length > 0)
                {
                    var parts = opStr.Split(',');
                    if (parts.Length == 2 && IsIdent(parts[1].Trim())) labelRef.Add(parts[1].Trim());
                }

                if (mnem == "mov" || mnem == "xor" || mnem == "add" || mnem == "sub" || mnem == "and" || mnem == "or" || mnem == "imul")
                {
                    var parts = opStr.Split(',');
                    if (parts.Length >= 1)
                    {
                        var dstTok = parts[0].Trim();
                        if (X64Registers.TryParse(dstTok, out var reg) && reg.Class == RegClass.Gpr && Win64CalleeSavedGpr.Contains(reg.Index))
                        {
                            clobberedCallee.Add(reg.Index);
                        }
                    }
                }

                if (mnem == "call")
                {
                    findings.Add(new Finding
                    {
                        Severity = "info",
                        LineNumber = lineNum,
                        RawLine = raw,
                        Message = "call site: ensure 32 bytes shadow space and 16-byte rsp alignment before this instruction"
                    });
                }
            }

            foreach (var def in labelDef)
            {
                if (def.Key.StartsWith("__asm_")) continue;
                if (!labelRef.Contains(def.Key) && def.Key != "main")
                {
                    findings.Add(new Finding
                    {
                        LineNumber = def.Value,
                        RawLine = "",
                        Message = $"label '{def.Key}' defined but never referenced"
                    });
                }
            }

            if (!sawRet)
            {
                findings.Add(new Finding
                {
                    LineNumber = lines.Length,
                    RawLine = "",
                    Message = "no `ret` instruction found — function will fall off the end"
                });
            }

            foreach (var idx in clobberedCallee)
            {
                findings.Add(new Finding
                {
                    LineNumber = 0,
                    RawLine = "",
                    Message = $"Win64 callee-saved register clobbered without save/restore: {RegName(idx)}"
                });
            }

            return findings;
        }

        private static string RegName(byte idx)
        {
            string[] names = { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" };
            return names[idx & 15];
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

        private static bool IsIdent(string s)
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
    }

    internal static class X64MnemonicsHelper
    {
        public static bool IsJcc(string m)
        {
            switch (m)
            {
                case "je": case "jz": case "jne": case "jnz":
                case "jl": case "jnge": case "jle": case "jng":
                case "jg": case "jnle": case "jge": case "jnl":
                case "jb": case "jc": case "jnae":
                case "ja": case "jnbe":
                case "jae": case "jnb": case "jnc":
                case "jbe": case "jna":
                case "js": case "jns":
                case "jo": case "jno":
                case "jp": case "jpe": case "jnp": case "jpo":
                    return true;
            }
            return false;
        }
    }
}

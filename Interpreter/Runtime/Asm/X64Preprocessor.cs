using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// NASM-style preprocessor that runs before the assembler.
    ///
    /// Supported directives (all start at column 0 after trim):
    ///   %define NAME VALUE
    ///   %undef  NAME
    ///   %macro NAME N
    ///     body
    ///   %endmacro
    ///   %include "path"
    ///   %if  expr
    ///   %elif expr
    ///   %else
    ///   %endif
    ///   %ifdef NAME / %ifndef NAME
    ///   %rep N
    ///     body
    ///   %endrep
    ///   NAME equ EXPR   (line form, like NASM)
    ///
    /// Inside macro bodies %1, %2, ... reference positional arguments and
    /// %0 is the count.
    ///
    /// Expression evaluator for %if / %rep / equ supports +-*/% bitwise &amp;|^~
    /// shifts, comparisons (treated as 0/1), and decimal/hex/binary literals.
    /// </summary>
    public sealed class X64Preprocessor
    {
        public sealed class Options
        {
            public Dictionary<string, string> InitialDefines { get; } = new(StringComparer.Ordinal);
            public List<string> IncludeRoots { get; } = new();
            public int MaxIncludeDepth { get; set; } = 32;
            public int MaxExpansionDepth { get; set; } = 256;
            public Func<string, string?>? FileResolver { get; set; }
        }

        private sealed class Macro
        {
            public string Name = "";
            public int ParamCount;
            public List<string> Body = new();
        }

        private readonly Options _opts;
        private readonly Dictionary<string, string> _defines;
        private readonly Dictionary<string, Macro> _macros = new(StringComparer.OrdinalIgnoreCase);
        private int _includeDepth;

        public X64Preprocessor(Options? opts = null)
        {
            _opts = opts ?? new Options();
            _defines = new Dictionary<string, string>(_opts.InitialDefines, StringComparer.Ordinal);
        }

        public string Process(string source)
        {
            var output = new List<string>(256);
            ProcessLines(SplitLines(source), output);
            return string.Join("\n", output);
        }

        private static List<string> SplitLines(string s)
        {
            return new List<string>(s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        }

        private void ProcessLines(List<string> lines, List<string> output)
        {
            int i = 0;
            var ifStack = new Stack<(bool active, bool everTaken)>();

            while (i < lines.Count)
            {
                string raw = lines[i];
                string trimmed = raw.TrimStart();

                bool active = ifStack.Count == 0 || AllActive(ifStack);

                if (trimmed.StartsWith("%if ", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("%if", StringComparison.OrdinalIgnoreCase))
                {
                    if (!active) { ifStack.Push((false, false)); i++; continue; }
                    var expr = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "0";
                    long v = EvalExpr(expr);
                    ifStack.Push((v != 0, v != 0));
                    i++; continue;
                }
                if (trimmed.StartsWith("%ifdef ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!active) { ifStack.Push((false, false)); i++; continue; }
                    var name = trimmed.Substring(6).Trim();
                    bool has = _defines.ContainsKey(name);
                    ifStack.Push((has, has));
                    i++; continue;
                }
                if (trimmed.StartsWith("%ifndef ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!active) { ifStack.Push((false, false)); i++; continue; }
                    var name = trimmed.Substring(7).Trim();
                    bool has = _defines.ContainsKey(name);
                    ifStack.Push((!has, !has));
                    i++; continue;
                }
                if (trimmed.Equals("%else", StringComparison.OrdinalIgnoreCase))
                {
                    if (ifStack.Count == 0) throw new AsmAssembleException(i + 1, raw, "stray %else");
                    var top = ifStack.Pop();
                    ifStack.Push((!top.everTaken, top.everTaken));
                    i++; continue;
                }
                if (trimmed.StartsWith("%elif ", StringComparison.OrdinalIgnoreCase))
                {
                    if (ifStack.Count == 0) throw new AsmAssembleException(i + 1, raw, "stray %elif");
                    var top = ifStack.Pop();
                    if (top.everTaken) { ifStack.Push((false, true)); }
                    else
                    {
                        var expr = trimmed.Substring(5).Trim();
                        long v = EvalExpr(expr);
                        ifStack.Push((v != 0, v != 0));
                    }
                    i++; continue;
                }
                if (trimmed.Equals("%endif", StringComparison.OrdinalIgnoreCase))
                {
                    if (ifStack.Count == 0) throw new AsmAssembleException(i + 1, raw, "stray %endif");
                    ifStack.Pop();
                    i++; continue;
                }

                if (!active) { i++; continue; }

                if (trimmed.StartsWith("%define ", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = trimmed.Substring(7).Trim();
                    int sp = FindFirstWhitespace(rest);
                    if (sp < 0) { _defines[rest] = "1"; }
                    else { _defines[rest.Substring(0, sp)] = rest.Substring(sp + 1).Trim(); }
                    i++; continue;
                }
                if (trimmed.StartsWith("%undef ", StringComparison.OrdinalIgnoreCase))
                {
                    _defines.Remove(trimmed.Substring(6).Trim());
                    i++; continue;
                }
                if (trimmed.StartsWith("%include ", StringComparison.OrdinalIgnoreCase))
                {
                    var arg = trimmed.Substring(8).Trim();
                    if (arg.StartsWith("\"") && arg.EndsWith("\""))
                        arg = arg.Substring(1, arg.Length - 2);
                    var loaded = ResolveAndLoad(arg, i + 1, raw);
                    if (_includeDepth >= _opts.MaxIncludeDepth)
                        throw new AsmAssembleException(i + 1, raw, "max include depth exceeded");
                    _includeDepth++;
                    try { ProcessLines(SplitLines(loaded), output); }
                    finally { _includeDepth--; }
                    i++; continue;
                }
                if (trimmed.StartsWith("%macro ", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = trimmed.Substring(6).Trim();
                    int sp = FindFirstWhitespace(rest);
                    string mname; int pcount;
                    if (sp < 0) { mname = rest; pcount = 0; }
                    else
                    {
                        mname = rest.Substring(0, sp);
                        var pstr = rest.Substring(sp + 1).Trim();
                        if (!int.TryParse(pstr, out pcount))
                            throw new AsmAssembleException(i + 1, raw, $"invalid %macro arity '{pstr}'");
                    }
                    var m = new Macro { Name = mname, ParamCount = pcount };
                    i++;
                    while (i < lines.Count && !lines[i].TrimStart().Equals("%endmacro", StringComparison.OrdinalIgnoreCase))
                    {
                        m.Body.Add(lines[i]); i++;
                    }
                    if (i >= lines.Count) throw new AsmAssembleException(i + 1, raw, "%macro without %endmacro");
                    i++;
                    _macros[mname] = m;
                    continue;
                }
                if (trimmed.StartsWith("%rep ", StringComparison.OrdinalIgnoreCase))
                {
                    var count = (int)EvalExpr(trimmed.Substring(4).Trim());
                    var body = new List<string>();
                    i++;
                    while (i < lines.Count && !lines[i].TrimStart().Equals("%endrep", StringComparison.OrdinalIgnoreCase))
                    {
                        body.Add(lines[i]); i++;
                    }
                    if (i >= lines.Count) throw new AsmAssembleException(i + 1, raw, "%rep without %endrep");
                    i++;
                    for (int k = 0; k < count; k++) ProcessLines(body, output);
                    continue;
                }

                int equIdx = FindEqu(trimmed);
                if (equIdx > 0)
                {
                    string name = trimmed.Substring(0, equIdx).Trim();
                    string val = trimmed.Substring(equIdx + 3).Trim();
                    _defines[name] = val;
                    i++; continue;
                }

                string expanded = ExpandDefines(raw, 0);

                string macroLine = expanded.TrimStart();
                int macroSp = FindFirstWhitespace(macroLine);
                string firstWord = macroSp < 0 ? macroLine : macroLine.Substring(0, macroSp);
                if (_macros.TryGetValue(firstWord, out var mac))
                {
                    string argsText = macroSp < 0 ? "" : macroLine.Substring(macroSp + 1).Trim();
                    var margs = SplitMacroArgs(argsText);
                    if (margs.Count != mac.ParamCount && mac.ParamCount != 0)
                        throw new AsmAssembleException(i + 1, raw, $"macro '{mac.Name}' expects {mac.ParamCount} arg(s), got {margs.Count}");
                    foreach (var bodyLine in mac.Body)
                    {
                        var substituted = ExpandMacroParams(bodyLine, margs);
                        output.Add(ExpandDefines(substituted, 0));
                    }
                    i++; continue;
                }

                output.Add(expanded);
                i++;
            }

            if (ifStack.Count != 0)
                throw new AsmAssembleException(0, "", "unterminated %if block");
        }

        private static bool AllActive(Stack<(bool active, bool everTaken)> st)
        {
            foreach (var f in st) if (!f.active) return false;
            return true;
        }

        private string ExpandDefines(string line, int depth)
        {
            if (depth > _opts.MaxExpansionDepth) return line;
            if (_defines.Count == 0) return line;

            var sb = new StringBuilder(line.Length);
            int i = 0;
            bool changed = false;
            while (i < line.Length)
            {
                char c = line[i];
                if (IsIdentStart(c))
                {
                    int j = i + 1;
                    while (j < line.Length && IsIdentCont(line[j])) j++;
                    var ident = line.Substring(i, j - i);
                    if (_defines.TryGetValue(ident, out var rep))
                    {
                        sb.Append(rep);
                        changed = true;
                    }
                    else sb.Append(ident);
                    i = j;
                }
                else { sb.Append(c); i++; }
            }
            if (!changed) return line;
            return ExpandDefines(sb.ToString(), depth + 1);
        }

        private static string ExpandMacroParams(string line, List<string> args)
        {
            var sb = new StringBuilder(line.Length);
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '%' && i + 1 < line.Length && char.IsDigit(line[i + 1]))
                {
                    int j = i + 1;
                    while (j < line.Length && char.IsDigit(line[j])) j++;
                    int idx = int.Parse(line.Substring(i + 1, j - i - 1));
                    if (idx == 0) sb.Append(args.Count.ToString());
                    else if (idx >= 1 && idx <= args.Count) sb.Append(args[idx - 1]);
                    i = j;
                    continue;
                }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        private static List<string> SplitMacroArgs(string text)
        {
            var list = new List<string>();
            if (text.Length == 0) return list;
            int depth = 0;
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || (text[i] == ',' && depth == 0))
                {
                    list.Add(text.Substring(start, i - start).Trim());
                    start = i + 1;
                }
                else if (text[i] == '[' || text[i] == '(') depth++;
                else if (text[i] == ']' || text[i] == ')') depth = Math.Max(0, depth - 1);
            }
            return list;
        }

        private static int FindEqu(string s)
        {
            int i = s.IndexOf(" equ ", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return i;
            i = s.IndexOf("\tequ\t", StringComparison.OrdinalIgnoreCase);
            return i;
        }

        private static int FindFirstWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] == ' ' || s[i] == '\t') return i;
            return -1;
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '.';
        private static bool IsIdentCont(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

        private string ResolveAndLoad(string path, int lineNumber, string rawLine)
        {
            if (_opts.FileResolver != null)
            {
                var r = _opts.FileResolver(path);
                if (r != null) return r;
            }

            foreach (var root in _opts.IncludeRoots)
            {
                var full = Path.Combine(root, path);
                if (File.Exists(full)) return File.ReadAllText(full);
            }
            if (File.Exists(path)) return File.ReadAllText(path);
            throw new AsmAssembleException(lineNumber, rawLine, $"%include: cannot find '{path}'");
        }

        public long EvalExpr(string expr)
        {
            var e = new ExprEval(expr, _defines);
            return e.Parse();
        }

        private sealed class ExprEval
        {
            private readonly string _src;
            private readonly Dictionary<string, string> _defs;
            private int _i;

            public ExprEval(string src, Dictionary<string, string> defs)
            {
                _src = src;
                _defs = defs;
            }

            public long Parse()
            {
                long v = ParseLogicalOr();
                return v;
            }

            private long ParseLogicalOr()
            {
                long a = ParseLogicalAnd();
                while (PeekStr("||"))
                {
                    Consume(2);
                    long b = ParseLogicalAnd();
                    a = (a != 0 || b != 0) ? 1 : 0;
                }
                return a;
            }

            private long ParseLogicalAnd()
            {
                long a = ParseBitOr();
                while (PeekStr("&&"))
                {
                    Consume(2);
                    long b = ParseBitOr();
                    a = (a != 0 && b != 0) ? 1 : 0;
                }
                return a;
            }

            private long ParseBitOr()
            {
                long a = ParseBitXor();
                while (PeekChar('|') && !PeekStr("||"))
                {
                    Consume(1);
                    long b = ParseBitXor();
                    a |= b;
                }
                return a;
            }

            private long ParseBitXor()
            {
                long a = ParseBitAnd();
                while (PeekChar('^'))
                {
                    Consume(1);
                    long b = ParseBitAnd();
                    a ^= b;
                }
                return a;
            }

            private long ParseBitAnd()
            {
                long a = ParseEq();
                while (PeekChar('&') && !PeekStr("&&"))
                {
                    Consume(1);
                    long b = ParseEq();
                    a &= b;
                }
                return a;
            }

            private long ParseEq()
            {
                long a = ParseRel();
                while (true)
                {
                    if (PeekStr("==")) { Consume(2); long b = ParseRel(); a = a == b ? 1 : 0; }
                    else if (PeekStr("!=")) { Consume(2); long b = ParseRel(); a = a != b ? 1 : 0; }
                    else break;
                }
                return a;
            }

            private long ParseRel()
            {
                long a = ParseShift();
                while (true)
                {
                    if (PeekStr("<=")) { Consume(2); long b = ParseShift(); a = a <= b ? 1 : 0; }
                    else if (PeekStr(">=")) { Consume(2); long b = ParseShift(); a = a >= b ? 1 : 0; }
                    else if (PeekChar('<') && !PeekStr("<<")) { Consume(1); long b = ParseShift(); a = a < b ? 1 : 0; }
                    else if (PeekChar('>') && !PeekStr(">>")) { Consume(1); long b = ParseShift(); a = a > b ? 1 : 0; }
                    else break;
                }
                return a;
            }

            private long ParseShift()
            {
                long a = ParseAdd();
                while (true)
                {
                    if (PeekStr("<<")) { Consume(2); long b = ParseAdd(); a <<= (int)(b & 63); }
                    else if (PeekStr(">>")) { Consume(2); long b = ParseAdd(); a >>= (int)(b & 63); }
                    else break;
                }
                return a;
            }

            private long ParseAdd()
            {
                long a = ParseMul();
                while (true)
                {
                    SkipWs();
                    if (PeekChar('+')) { Consume(1); long b = ParseMul(); a += b; }
                    else if (PeekChar('-')) { Consume(1); long b = ParseMul(); a -= b; }
                    else break;
                }
                return a;
            }

            private long ParseMul()
            {
                long a = ParseUnary();
                while (true)
                {
                    SkipWs();
                    if (PeekChar('*')) { Consume(1); long b = ParseUnary(); a *= b; }
                    else if (PeekChar('/')) { Consume(1); long b = ParseUnary(); a = b == 0 ? 0 : a / b; }
                    else if (PeekChar('%')) { Consume(1); long b = ParseUnary(); a = b == 0 ? 0 : a % b; }
                    else break;
                }
                return a;
            }

            private long ParseUnary()
            {
                SkipWs();
                if (PeekChar('-')) { Consume(1); return -ParseUnary(); }
                if (PeekChar('+')) { Consume(1); return ParseUnary(); }
                if (PeekChar('~')) { Consume(1); return ~ParseUnary(); }
                if (PeekChar('!')) { Consume(1); return ParseUnary() == 0 ? 1 : 0; }
                return ParsePrimary();
            }

            private long ParsePrimary()
            {
                SkipWs();
                if (PeekChar('('))
                {
                    Consume(1);
                    long v = ParseLogicalOr();
                    SkipWs();
                    if (PeekChar(')')) Consume(1);
                    return v;
                }

                if (_i < _src.Length && (char.IsDigit(_src[_i]) || (_src[_i] == '0' && _i + 1 < _src.Length && (_src[_i + 1] == 'x' || _src[_i + 1] == 'X' || _src[_i + 1] == 'b' || _src[_i + 1] == 'B'))))
                {
                    int s = _i;
                    while (_i < _src.Length && (char.IsLetterOrDigit(_src[_i]) || _src[_i] == '_')) _i++;
                    var tok = _src.Substring(s, _i - s);
                    if (X64OperandParser.TryParseNumber(tok, out long v)) return v;
                    return 0;
                }

                if (_i < _src.Length && (char.IsLetter(_src[_i]) || _src[_i] == '_'))
                {
                    int s = _i;
                    while (_i < _src.Length && (char.IsLetterOrDigit(_src[_i]) || _src[_i] == '_')) _i++;
                    var name = _src.Substring(s, _i - s);
                    if (_defs.TryGetValue(name, out var rep))
                    {
                        var inner = new ExprEval(rep, _defs);
                        return inner.Parse();
                    }
                    return 0;
                }

                return 0;
            }

            private bool PeekChar(char c) { SkipWs(); return _i < _src.Length && _src[_i] == c; }
            private bool PeekStr(string s) { SkipWs(); if (_i + s.Length > _src.Length) return false; for (int k = 0; k < s.Length; k++) if (_src[_i + k] != s[k]) return false; return true; }
            private void Consume(int n) { _i += n; }
            private void SkipWs() { while (_i < _src.Length && (_src[_i] == ' ' || _src[_i] == '\t')) _i++; }
        }
    }
}

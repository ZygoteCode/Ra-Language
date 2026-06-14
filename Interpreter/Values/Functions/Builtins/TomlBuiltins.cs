using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // std.prelude.serialize (continued) — TOML and a pragmatic YAML subset,
    // both hand-written (no reflection / no third-party parser → AOT-safe and
    // cross-platform-identical). Registered under the same `serialize` group.
    //
    // TOML: tables [a.b], arrays-of-tables [[a]], dotted keys, basic/literal
    // strings, int (dec/hex/oct/bin with `_`), float, bool, inline tables,
    // (multi-line) arrays. Datetimes are preserved as strings.
    //
    // YAML: block mappings + block sequences (indentation-driven), flow
    // collections [..]/{..}, and scalar inference (int/float/bool/null/string).
    // Out of scope by design: anchors/aliases, tags, multi-document streams,
    // block scalars (| and >), complex keys — documented, not silently wrong.
    public static class TomlBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("toml_parse", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("toml_parse", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(TomlParser.Parse(AsString(args[0])), ctx, p1, p2); }
                catch (FormatParseError fe) { return Fail(ctx, p1, p2, "toml_parse: " + fe.Message); }
            });
            BuiltInRegistry.Register("toml_stringify", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("toml_stringify", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not MapValue mv) return Fail(ctx, p1, p2, "toml_stringify: argument must be a map");
                var sb = new StringBuilder();
                try { TomlWriter.Write(mv, sb); }
                catch (FormatParseError fe) { return Fail(ctx, p1, p2, "toml_stringify: " + fe.Message); }
                return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
            });
            BuiltInRegistry.Register("yaml_parse", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("yaml_parse", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(YamlParser.Parse(AsString(args[0])), ctx, p1, p2); }
                catch (FormatParseError fe) { return Fail(ctx, p1, p2, "yaml_parse: " + fe.Message); }
            });
            BuiltInRegistry.Register("yaml_stringify", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("yaml_stringify", args, 1, ctx, p1, p2, out var err)) return err;
                var sb = new StringBuilder();
                YamlWriter.Write(args[0], sb, 0, true);
                return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
            });
            // yaml_parse_all(text) -> list of documents (split on '---').
            BuiltInRegistry.Register("yaml_parse_all", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("yaml_parse_all", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(new ListValue(YamlParser.ParseAll(AsString(args[0]))), ctx, p1, p2); }
                catch (FormatParseError fe) { return Fail(ctx, p1, p2, "yaml_parse_all: " + fe.Message); }
            });
        }

        internal sealed class FormatParseError : Exception
        {
            public FormatParseError(string m) : base(m) { }
        }

        // ===================== TOML =====================================

        private static class TomlParser
        {
            public static RuntimeValue Parse(string text)
            {
                var root = new MapValue(new List<(RuntimeValue, RuntimeValue)>());
                MapValue current = root;
                var lines = SplitLogicalLines(text);
                foreach (var raw in lines)
                {
                    string line = StripComment(raw).Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("[["))
                    {
                        if (!line.EndsWith("]]")) throw new FormatParseError("unterminated array-of-tables header");
                        var path = ParseKeyPath(line.Substring(2, line.Length - 4).Trim());
                        current = PushArrayTable(root, path);
                    }
                    else if (line.StartsWith("["))
                    {
                        if (!line.EndsWith("]")) throw new FormatParseError("unterminated table header");
                        var path = ParseKeyPath(line.Substring(1, line.Length - 2).Trim());
                        current = NavigateTable(root, path);
                    }
                    else
                    {
                        int eq = FindAssign(line);
                        if (eq < 0) throw new FormatParseError("expected 'key = value' in: " + line);
                        var keyPath = ParseKeyPath(line.Substring(0, eq).Trim());
                        var valStr = line.Substring(eq + 1).Trim();
                        var target = keyPath.Count == 1 ? current : NavigateTable(current, keyPath.GetRange(0, keyPath.Count - 1));
                        SetPair(target, keyPath[keyPath.Count - 1], ParseValue(valStr));
                    }
                }
                return root;
            }

            private static RuntimeValue? GetPair(MapValue m, string key)
            {
                foreach (var (k, v) in m.Pairs)
                    if (k is StringValue sk && sk.Value == key) return v;
                return null;
            }

            private static void SetPair(MapValue m, string key, RuntimeValue val)
            {
                for (int i = 0; i < m.Pairs.Count; i++)
                    if (m.Pairs[i].Key is StringValue sk && sk.Value == key) { m.Pairs[i] = (m.Pairs[i].Key, val); return; }
                m.Pairs.Add((new StringValue(key), val));
            }

            // Join lines while an array/inline-table spans multiple physical
            // lines (unbalanced [ or { outside of strings).
            private static List<string> SplitLogicalLines(string text)
            {
                var outLines = new List<string>();
                var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                int i = 0;
                while (i < raw.Length)
                {
                    string acc = raw[i];
                    while (Unbalanced(StripComment(acc)) && i + 1 < raw.Length)
                    {
                        i++;
                        acc += " " + raw[i];
                    }
                    outLines.Add(acc);
                    i++;
                }
                return outLines;
            }

            private static bool Unbalanced(string s)
            {
                int depth = 0; bool inStr = false; char q = '"';
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (inStr) { if (c == q) inStr = false; else if (c == '\\' && q == '"') i++; }
                    else if (c == '"' || c == '\'') { inStr = true; q = c; }
                    else if (c == '[' || c == '{') depth++;
                    else if (c == ']' || c == '}') depth--;
                }
                return depth > 0;
            }

            private static string StripComment(string s)
            {
                bool inStr = false; char q = '"';
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (inStr) { if (c == q) inStr = false; else if (c == '\\' && q == '"') i++; }
                    else if (c == '"' || c == '\'') { inStr = true; q = c; }
                    else if (c == '#') return s.Substring(0, i);
                }
                return s;
            }

            private static int FindAssign(string s)
            {
                bool inStr = false; char q = '"';
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (inStr) { if (c == q) inStr = false; else if (c == '\\' && q == '"') i++; }
                    else if (c == '"' || c == '\'') { inStr = true; q = c; }
                    else if (c == '=') return i;
                }
                return -1;
            }

            private static List<string> ParseKeyPath(string s)
            {
                var parts = new List<string>();
                int i = 0;
                while (i < s.Length)
                {
                    while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
                    if (i >= s.Length) break;
                    if (s[i] == '"' || s[i] == '\'')
                    {
                        char q = s[i]; i++;
                        var sb = new StringBuilder();
                        while (i < s.Length && s[i] != q) { if (s[i] == '\\' && q == '"' && i + 1 < s.Length) { sb.Append(Unescape(s[i + 1])); i += 2; } else sb.Append(s[i++]); }
                        i++; parts.Add(sb.ToString());
                    }
                    else
                    {
                        int start = i;
                        while (i < s.Length && s[i] != '.' && s[i] != ' ' && s[i] != '\t') i++;
                        parts.Add(s.Substring(start, i - start));
                    }
                    while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
                    if (i < s.Length && s[i] == '.') i++;
                }
                if (parts.Count == 0) throw new FormatParseError("empty key");
                return parts;
            }

            private static MapValue NavigateTable(MapValue root, List<string> path)
            {
                MapValue cur = root;
                foreach (var key in path)
                {
                    var existing = GetPair(cur, key);
                    if (existing is MapValue mv) cur = mv;
                    else if (existing is ListValue lv && lv.Elements.Count > 0 && lv.Elements[lv.Elements.Count - 1] is MapValue last) cur = last;
                    else if (existing == null) { var nm = new MapValue(new List<(RuntimeValue, RuntimeValue)>()); SetPair(cur, key, nm); cur = nm; }
                    else throw new FormatParseError($"key '{key}' is not a table");
                }
                return cur;
            }

            private static MapValue PushArrayTable(MapValue root, List<string> path)
            {
                MapValue parent = path.Count == 1 ? root : NavigateTable(root, path.GetRange(0, path.Count - 1));
                string key = path[path.Count - 1];
                var existing = GetPair(parent, key);
                ListValue arr;
                if (existing is ListValue lv) arr = lv;
                else if (existing == null) { arr = new ListValue(new List<RuntimeValue>()); SetPair(parent, key, arr); }
                else throw new FormatParseError($"key '{key}' is not an array of tables");
                var nm = new MapValue(new List<(RuntimeValue, RuntimeValue)>());
                arr.Elements.Add(nm);
                return nm;
            }

            private static RuntimeValue ParseValue(string s)
            {
                s = s.Trim();
                if (s.Length == 0) throw new FormatParseError("empty value");
                char c = s[0];
                if (c == '"' || c == '\'') return new StringValue(ParseQuoted(s, out _));
                if (c == '[') return ParseArray(s);
                if (c == '{') return ParseInlineTable(s);
                if (s == "true") return BooleanValue.Of(true);
                if (s == "false") return BooleanValue.Of(false);
                return ParseScalar(s);
            }

            private static string ParseQuoted(string s, out int consumed)
            {
                char q = s[0];
                var sb = new StringBuilder();
                int i = 1;
                while (i < s.Length && s[i] != q)
                {
                    if (s[i] == '\\' && q == '"' && i + 1 < s.Length)
                    {
                        char e = s[i + 1];
                        if (e == 'u' && i + 5 < s.Length) { sb.Append((char)int.Parse(s.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 6; continue; }
                        sb.Append(Unescape(e)); i += 2;
                    }
                    else sb.Append(s[i++]);
                }
                if (i >= s.Length) throw new FormatParseError("unterminated string");
                consumed = i + 1;
                return sb.ToString();
            }

            private static char Unescape(char e) => e switch
            {
                'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f',
                '"' => '"', '\\' => '\\', '/' => '/', _ => e
            };

            private static RuntimeValue ParseArray(string s)
            {
                var items = new List<RuntimeValue>();
                int i = 1;
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatParseError("unterminated array");
                    if (s[i] == ']') break;
                    int end = ScanValue(s, i);
                    items.Add(ParseValue(s.Substring(i, end - i)));
                    i = end;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') i++;
                    else if (i < s.Length && s[i] == ']') break;
                }
                return new ListValue(items);
            }

            private static RuntimeValue ParseInlineTable(string s)
            {
                var map = new MapValue(new List<(RuntimeValue, RuntimeValue)>());
                int i = 1;
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatParseError("unterminated inline table");
                    if (s[i] == '}') break;
                    int eq = i;
                    while (eq < s.Length && s[eq] != '=') eq++;
                    string key = ParseKeyPath(s.Substring(i, eq - i).Trim())[0];
                    int vstart = eq + 1;
                    SkipWs(s, ref vstart);
                    int vend = ScanValue(s, vstart);
                    SetPair(map, key, ParseValue(s.Substring(vstart, vend - vstart)));
                    i = vend;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') i++;
                    else if (i < s.Length && s[i] == '}') break;
                }
                return map;
            }

            // Return the index one-past the end of a value token starting at i.
            private static int ScanValue(string s, int i)
            {
                if (i >= s.Length) return i;
                char c = s[i];
                if (c == '"' || c == '\'')
                {
                    char q = c; i++;
                    while (i < s.Length && s[i] != q) { if (s[i] == '\\' && q == '"') i++; i++; }
                    return Math.Min(i + 1, s.Length);
                }
                if (c == '[' || c == '{')
                {
                    char open = c, close = c == '[' ? ']' : '}';
                    int depth = 0; bool inStr = false; char q = '"';
                    for (; i < s.Length; i++)
                    {
                        char ch = s[i];
                        if (inStr) { if (ch == q) inStr = false; else if (ch == '\\' && q == '"') i++; }
                        else if (ch == '"' || ch == '\'') { inStr = true; q = ch; }
                        else if (ch == open) depth++;
                        else if (ch == close) { depth--; if (depth == 0) return i + 1; }
                    }
                    throw new FormatParseError("unterminated " + open);
                }
                while (i < s.Length && s[i] != ',' && s[i] != ']' && s[i] != '}') i++;
                return i;
            }

            private static void SkipWs(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++; }

            private static RuntimeValue ParseScalar(string s)
            {
                string t = s.Replace("_", "");
                if (t.StartsWith("0x") || t.StartsWith("0X")) { if (long.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return NumberFor(h); }
                else if (t.StartsWith("0o") || t.StartsWith("0O")) { try { return NumberFor(Convert.ToInt64(t.Substring(2), 8)); } catch { } }
                else if (t.StartsWith("0b") || t.StartsWith("0B")) { try { return NumberFor(Convert.ToInt64(t.Substring(2), 2)); } catch { } }
                if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return NumberFor(l);
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return new DoubleValue(d);
                // An RFC 3339 OFFSET date-time is an unambiguous instant -> a real
                // value (Unix milliseconds, the same representation the time
                // module uses). Local date-times / dates / times have no instant,
                // so they are preserved verbatim as strings.
                if (IsOffsetDateTime(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                    return NumberFor(dto.ToUnixTimeMilliseconds());
                return new StringValue(s);
            }

            private static bool IsOffsetDateTime(string s)
            {
                int t = s.IndexOf('T');
                if (t < 0) return false;
                string time = s.Substring(t + 1);
                if (time.EndsWith("Z")) return true;
                // trailing offset like +01:00 / -05:00
                return time.Length >= 6 && (time[time.Length - 6] == '+' || time[time.Length - 6] == '-') && time[time.Length - 3] == ':';
            }
        }

        private static class TomlWriter
        {
            public static void Write(MapValue root, StringBuilder sb)
            {
                WriteTable(root, "", sb);
            }

            private static void WriteTable(MapValue map, string prefix, StringBuilder sb)
            {
                // Scalars + arrays first, then sub-tables / arrays-of-tables.
                var subTables = new List<(string Key, MapValue Map)>();
                var arrTables = new List<(string Key, ListValue List)>();
                foreach (var (k, v) in map.Pairs)
                {
                    string key = k is StringValue sk ? sk.Value : AsString(k);
                    if (v is MapValue mv) subTables.Add((key, mv));
                    else if (v is ListValue lv && lv.Elements.Count > 0 && AllMaps(lv)) arrTables.Add((key, lv));
                    else { sb.Append(KeyText(key)).Append(" = ").Append(ValueText(v)).Append('\n'); }
                }
                foreach (var (key, mv) in subTables)
                {
                    string path = prefix.Length == 0 ? KeyText(key) : prefix + "." + KeyText(key);
                    sb.Append('[').Append(path).Append("]\n");
                    WriteTable(mv, path, sb);
                }
                foreach (var (key, lv) in arrTables)
                {
                    string path = prefix.Length == 0 ? KeyText(key) : prefix + "." + KeyText(key);
                    foreach (var el in lv.Elements)
                    {
                        sb.Append("[[").Append(path).Append("]]\n");
                        WriteTable((MapValue)el, path, sb);
                    }
                }
            }

            private static bool AllMaps(ListValue lv)
            {
                foreach (var e in lv.Elements) if (e is not MapValue) return false;
                return true;
            }

            private static string KeyText(string k)
            {
                bool bare = k.Length > 0;
                foreach (char c in k) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) { bare = false; break; }
                return bare ? k : "\"" + k.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }

            private static string ValueText(RuntimeValue v)
            {
                switch (v.Type)
                {
                    case RuntimeValueType.Boolean: return ((BooleanValue)v).Value ? "true" : "false";
                    case RuntimeValueType.String: return "\"" + ((StringValue)v).Value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
                    case RuntimeValueType.List:
                    {
                        var sb = new StringBuilder("[");
                        var els = ((ListValue)v).Elements;
                        for (int i = 0; i < els.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(ValueText(els[i])); }
                        return sb.Append(']').ToString();
                    }
                    case RuntimeValueType.Null: return "\"\"";
                    default:
                        if (v is DoubleValue or FloatValue or DecimalValue) return AsDouble(v).ToString("R", CultureInfo.InvariantCulture);
                        return AsLong(v).ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        // ===================== YAML (subset) ============================

        private sealed class YamlParser
        {
            private struct YLine { public int Indent; public string Raw; public string Content; public bool Blank; }
            private readonly List<YLine> _l = new();
            private readonly Dictionary<string, RuntimeValue> _anchors = new(StringComparer.Ordinal);

            // Single-document parse (a leading/trailing '---'/'...' is ignored).
            public static RuntimeValue Parse(string text)
            {
                var p = new YamlParser();
                p.Tokenize(text);
                int i = 0;
                p.SkipBlank(ref i);
                if (i >= p._l.Count) return NullValue.Null;
                return p.ParseNode(ref i, p._l[i].Indent);
            }

            // Multi-document stream split on '---' lines; each doc parsed fresh.
            public static List<RuntimeValue> ParseAll(string text)
            {
                var docs = new List<RuntimeValue>();
                foreach (var chunk in SplitDocuments(text)) docs.Add(Parse(chunk));
                return docs;
            }

            private static List<string> SplitDocuments(string text)
            {
                var docs = new List<string>();
                var cur = new StringBuilder();
                bool hasContent = false;
                foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    string t = line.Trim();
                    // A '---' flushes the accumulated document (a leading '---'
                    // with nothing before it is just a document-start marker).
                    if (t == "---") { if (hasContent) { docs.Add(cur.ToString()); cur.Clear(); hasContent = false; } continue; }
                    if (t == "...") continue;
                    cur.Append(line).Append('\n');
                    if (t.Length > 0) hasContent = true;
                }
                if (hasContent) docs.Add(cur.ToString());
                if (docs.Count == 0) docs.Add("");
                return docs;
            }

            private void Tokenize(string text)
            {
                foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    if (raw.Trim() == "---" || raw.Trim() == "...") continue;
                    int indent = 0; while (indent < raw.Length && raw[indent] == ' ') indent++;
                    string after = raw.Substring(indent);
                    _l.Add(new YLine { Indent = indent, Raw = after, Content = StripComment(after), Blank = after.Trim().Length == 0 });
                }
            }

            private void SkipBlank(ref int i)
            {
                while (i < _l.Count && (_l[i].Blank || _l[i].Content.Trim().Length == 0)) i++;
            }

            private static string StripComment(string s)
            {
                bool inStr = false; char q = '"';
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (inStr) { if (c == q) inStr = false; }
                    else if (c == '"' || c == '\'') { inStr = true; q = c; }
                    else if (c == '#' && (i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t')) return s.Substring(0, i);
                }
                return s;
            }

            private RuntimeValue ParseNode(ref int i, int indent)
            {
                SkipBlank(ref i);
                if (i >= _l.Count) return NullValue.Null;
                string c = _l[i].Content.TrimStart();
                if (c.StartsWith("- ") || c == "-") return ParseSeq(ref i, indent);
                return ParseMap(ref i, indent);
            }

            private RuntimeValue ParseSeq(ref int i, int indent)
            {
                var items = new List<RuntimeValue>();
                while (true)
                {
                    SkipBlank(ref i);
                    if (i >= _l.Count || _l[i].Indent != indent) break;
                    string content = _l[i].Content.TrimStart();
                    if (!(content.StartsWith("- ") || content == "-")) break;
                    string rest = content == "-" ? "" : content.Substring(2).Trim();
                    i++;
                    if (rest.Length > 0 && IsMapEntry(rest))
                    {
                        // "- key: v" — a mapping item whose first key is inline.
                        items.Add(ParseInlineSeqMap(rest, ref i, indent));
                    }
                    else
                    {
                        items.Add(ResolveValue(rest, ref i, indent));
                    }
                }
                return new ListValue(items);
            }

            // A sequence element that is itself a mapping: the first key sits on
            // the '-' line; subsequent keys are indented past the dash column.
            private RuntimeValue ParseInlineSeqMap(string firstEntry, ref int i, int seqIndent)
            {
                var pairs = new List<(RuntimeValue, RuntimeValue)>();
                int childIndent = seqIndent + 2;
                AddMapEntry(pairs, firstEntry, ref i, childIndent);
                while (true)
                {
                    SkipBlank(ref i);
                    if (i >= _l.Count || _l[i].Indent <= seqIndent) break;
                    if (!IsMapEntry(_l[i].Content.TrimStart())) break;
                    int entryIndent = _l[i].Indent;
                    string entry = _l[i].Content.TrimStart();
                    i++;
                    AddMapEntry(pairs, entry, ref i, entryIndent);
                }
                return new MapValue(pairs);
            }

            private RuntimeValue ParseMap(ref int i, int indent)
            {
                var pairs = new List<(RuntimeValue, RuntimeValue)>();
                while (true)
                {
                    SkipBlank(ref i);
                    if (i >= _l.Count || _l[i].Indent != indent) break;
                    string content = _l[i].Content.TrimStart();
                    if (!IsMapEntry(content)) break;
                    i++;
                    AddMapEntry(pairs, content, ref i, indent);
                }
                return new MapValue(pairs);
            }

            private void AddMapEntry(List<(RuntimeValue, RuntimeValue)> pairs, string entry, ref int i, int keyIndent)
            {
                var (key, val) = SplitMapEntry(entry);
                pairs.Add((new StringValue(key), ResolveValue(val, ref i, keyIndent)));
            }

            // Resolve the value part of a "key: <val>" (or "- <val>"), honouring
            // anchors (&a), aliases (*a), tags (!!t), flow [..]/{..}, block
            // scalars (| and >), and indentation-nested blocks.
            private RuntimeValue ResolveValue(string val, ref int i, int parentIndent)
            {
                string? anchor = null, tag = null;
                val = val.Trim();
                while (val.Length > 0 && (val[0] == '&' || val[0] == '!'))
                {
                    int sp = val.IndexOf(' ');
                    string token = sp < 0 ? val : val.Substring(0, sp);
                    if (token[0] == '&') anchor = token.Substring(1);
                    else if (token.StartsWith("!!")) tag = token.Substring(2);
                    else tag = token.Substring(1);
                    val = sp < 0 ? "" : val.Substring(sp + 1).Trim();
                }

                RuntimeValue value;
                if (val.Length == 0)
                {
                    SkipBlank(ref i);
                    if (i < _l.Count && _l[i].Indent > parentIndent)
                        value = ParseNode(ref i, _l[i].Indent);
                    else if (i < _l.Count && _l[i].Indent == parentIndent && IsSeqLine(_l[i].Content))
                        // a block sequence value may sit at the SAME indent as its key
                        value = ParseSeq(ref i, _l[i].Indent);
                    else value = NullValue.Null;
                }
                else if (val == "|" || val == "|-" || val == "|+" || val == ">" || val == ">-" || val == ">+")
                    value = new StringValue(ReadBlockScalar(ref i, parentIndent, val));
                else if (val[0] == '*')
                    value = _anchors.TryGetValue(val.Substring(1).Trim(), out var av) ? av : NullValue.Null;
                else if (val[0] == '[' || val[0] == '{')
                    value = ParseFlow(val, out _);
                else
                    value = CoerceScalar(val, tag);

                if (anchor != null) _anchors[anchor] = value;
                return value;
            }

            private string ReadBlockScalar(ref int i, int parentIndent, string marker)
            {
                char style = marker[0];
                char chomp = marker.Length > 1 ? marker[1] : ' ';
                var raw = new List<string>();
                int blockIndent = -1;
                while (i < _l.Count)
                {
                    var ln = _l[i];
                    if (!ln.Blank && ln.Indent <= parentIndent) break;
                    if (ln.Blank) { raw.Add(""); i++; continue; }
                    if (blockIndent < 0) blockIndent = ln.Indent;
                    raw.Add(new string(' ', Math.Max(0, ln.Indent - blockIndent)) + ln.Raw);
                    i++;
                }
                while (raw.Count > 0 && raw[raw.Count - 1] == "") raw.RemoveAt(raw.Count - 1);
                string result;
                if (style == '|') result = string.Join("\n", raw);
                else
                {
                    var sb = new StringBuilder();
                    for (int k = 0; k < raw.Count; k++)
                    {
                        if (k > 0) sb.Append(raw[k] == "" || raw[k - 1] == "" ? "\n" : " ");
                        sb.Append(raw[k]);
                    }
                    result = sb.ToString();
                }
                if (chomp == '+') result += "\n";
                return result;
            }

            private static RuntimeValue CoerceScalar(string val, string? tag)
            {
                if (tag != null)
                {
                    string body = Unquote(val);
                    switch (tag)
                    {
                        case "str": return new StringValue(body);
                        case "int": return long.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var li) ? NumberFor(li) : new StringValue(body);
                        case "float": return double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var df) ? new DoubleValue(df) : new StringValue(body);
                        case "bool": return BooleanValue.Of(body == "true" || body == "yes" || body == "on" || body == "True");
                        case "null": return NullValue.Null;
                    }
                }
                return InferScalar(val);
            }

            private static string Unquote(string s)
            {
                if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                    return s.Substring(1, s.Length - 2);
                return s;
            }

            private static bool IsMapEntry(string s) => FindColon(s) >= 0;

            private static bool IsSeqLine(string content)
            {
                string c = content.TrimStart();
                return c.StartsWith("- ") || c == "-";
            }

            private static (string Key, string Val) SplitMapEntry(string s)
            {
                int c = FindColon(s);
                string key = s.Substring(0, c).Trim();
                if ((key.StartsWith("\"") && key.EndsWith("\"")) || (key.StartsWith("'") && key.EndsWith("'"))) key = key.Substring(1, key.Length - 2);
                string val = s.Substring(c + 1).Trim();
                return (key, val);
            }

            private static int FindColon(string s)
            {
                bool inStr = false; char q = '"'; int depth = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (inStr) { if (c == q) inStr = false; }
                    else if (c == '"' || c == '\'') { inStr = true; q = c; }
                    else if (c == '[' || c == '{') depth++;
                    else if (c == ']' || c == '}') depth--;
                    else if (c == ':' && depth == 0 && (i + 1 == s.Length || s[i + 1] == ' ')) return i;
                }
                return -1;
            }

            private static RuntimeValue InferScalar(string s)
            {
                s = s.Trim();
                if (s.Length == 0) return NullValue.Null;
                if (s[0] == '[' || s[0] == '{') return ParseFlow(s, out _);
                if ((s[0] == '"' && s.EndsWith("\"")) || (s[0] == '\'' && s.EndsWith("'"))) return new StringValue(s.Substring(1, s.Length - 2));
                if (s == "null" || s == "~" || s == "Null" || s == "NULL") return NullValue.Null;
                if (s == "true" || s == "True" || s == "TRUE" || s == "yes" || s == "on") return BooleanValue.Of(true);
                if (s == "false" || s == "False" || s == "FALSE" || s == "no" || s == "off") return BooleanValue.Of(false);
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return NumberFor(l);
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return new DoubleValue(d);
                return new StringValue(s);
            }

            private static RuntimeValue ParseFlow(string s, out int consumed)
            {
                consumed = s.Length;
                char open = s[0];
                if (open == '[')
                {
                    var items = new List<RuntimeValue>();
                    int i = 1;
                    while (i < s.Length && s[i] != ']')
                    {
                        while (i < s.Length && (s[i] == ' ' || s[i] == ',')) i++;
                        if (i >= s.Length || s[i] == ']') break;
                        int end = ScanFlow(s, i, ']');
                        items.Add(InferScalar(s.Substring(i, end - i)));
                        i = end;
                        while (i < s.Length && s[i] == ' ') i++;
                        if (i < s.Length && s[i] == ',') i++;
                    }
                    return new ListValue(items);
                }
                else
                {
                    var pairs = new List<(RuntimeValue, RuntimeValue)>();
                    int i = 1;
                    while (i < s.Length && s[i] != '}')
                    {
                        while (i < s.Length && (s[i] == ' ' || s[i] == ',')) i++;
                        if (i >= s.Length || s[i] == '}') break;
                        int end = ScanFlow(s, i, '}');
                        string entry = s.Substring(i, end - i);
                        int colon = FindColon(entry);
                        if (colon < 0) colon = entry.IndexOf(':');
                        if (colon >= 0) pairs.Add((new StringValue(entry.Substring(0, colon).Trim().Trim('"', '\'')), InferScalar(entry.Substring(colon + 1))));
                        i = end;
                        while (i < s.Length && s[i] == ' ') i++;
                        if (i < s.Length && s[i] == ',') i++;
                    }
                    return new MapValue(pairs);
                }
            }

            private static int ScanFlow(string s, int i, char terminator)
            {
                int depth = 0; bool inStr = false; char q = '"';
                for (; i < s.Length; i++)
                {
                    char c = s[i];
                    if (inStr) { if (c == q) inStr = false; }
                    else if (c == '"' || c == '\'') { inStr = true; q = c; }
                    else if (c == '[' || c == '{') depth++;
                    else if (c == ']' || c == '}') { if (depth == 0) return i; depth--; }
                    else if (c == ',' && depth == 0) return i;
                }
                return i;
            }
        }

        private static class YamlWriter
        {
            public static void Write(RuntimeValue v, StringBuilder sb, int indent, bool topLevel)
            {
                string pad = new string(' ', indent);
                switch (v)
                {
                    case MapValue mv:
                        if (mv.Pairs.Count == 0) { sb.Append(pad).Append("{}\n"); return; }
                        foreach (var (k, val) in mv.Pairs)
                        {
                            string key = k is StringValue sk ? sk.Value : AsString(k);
                            if (val is MapValue cm && cm.Pairs.Count > 0) { sb.Append(pad).Append(key).Append(":\n"); Write(val, sb, indent + 2, false); }
                            else if (val is ListValue cl && cl.Elements.Count > 0) { sb.Append(pad).Append(key).Append(":\n"); WriteSeq(cl, sb, indent); }
                            else { sb.Append(pad).Append(key).Append(": ").Append(Scalar(val)).Append('\n'); }
                        }
                        return;
                    case ListValue lv:
                        if (lv.Elements.Count == 0) { sb.Append(pad).Append("[]\n"); return; }
                        WriteSeq(lv, sb, indent == 0 ? 0 : indent - 2);
                        return;
                    default:
                        sb.Append(pad).Append(Scalar(v)).Append('\n');
                        return;
                }
            }

            private static void WriteSeq(ListValue lv, StringBuilder sb, int indent)
            {
                string pad = new string(' ', indent);
                foreach (var el in lv.Elements)
                {
                    if (el is MapValue em && em.Pairs.Count > 0)
                    {
                        bool first = true;
                        foreach (var (k, val) in em.Pairs)
                        {
                            string key = k is StringValue sk ? sk.Value : AsString(k);
                            string lead = first ? pad + "- " : pad + "  ";
                            if (val is MapValue or ListValue) { sb.Append(lead).Append(key).Append(":\n"); Write(val, sb, indent + 4, false); }
                            else sb.Append(lead).Append(key).Append(": ").Append(Scalar(val)).Append('\n');
                            first = false;
                        }
                    }
                    else sb.Append(pad).Append("- ").Append(Scalar(el)).Append('\n');
                }
            }

            private static string Scalar(RuntimeValue v)
            {
                switch (v.Type)
                {
                    case RuntimeValueType.Null: return "null";
                    case RuntimeValueType.Boolean: return ((BooleanValue)v).Value ? "true" : "false";
                    case RuntimeValueType.String:
                    {
                        string s = ((StringValue)v).Value;
                        bool needQuote = s.Length == 0 || s == "null" || s == "true" || s == "false"
                            || s.IndexOfAny(new[] { ':', '#', '\n', '[', ']', '{', '}', ',', '"', '\'' }) >= 0
                            || s[0] == ' ' || s[s.Length - 1] == ' '
                            || (s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '-'));
                        if (!needQuote) return s;
                        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
                    }
                    default:
                        if (v is DoubleValue or FloatValue or DecimalValue) return AsDouble(v).ToString("R", CultureInfo.InvariantCulture);
                        return AsLong(v).ToString(CultureInfo.InvariantCulture);
                }
            }
        }
    }
}

using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class StringBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("str", Str);
            BuiltInRegistry.Register("repr", Repr);
            BuiltInRegistry.Register("str_len", StrLen);
            BuiltInRegistry.Register("str_upper", StrUpper);
            BuiltInRegistry.Register("str_lower", StrLower);
            BuiltInRegistry.Register("str_capitalize", StrCapitalize);
            BuiltInRegistry.Register("str_title", StrTitle);
            BuiltInRegistry.Register("str_trim", StrTrim);
            BuiltInRegistry.Register("str_trim_start", StrTrimStart);
            BuiltInRegistry.Register("str_trim_end", StrTrimEnd);
            BuiltInRegistry.Register("str_split", StrSplit);
            BuiltInRegistry.Register("str_join", StrJoin);
            BuiltInRegistry.Register("str_replace", StrReplace);
            BuiltInRegistry.Register("str_starts_with", StrStartsWith);
            BuiltInRegistry.Register("str_ends_with", StrEndsWith);
            BuiltInRegistry.Register("str_contains", StrContains);
            BuiltInRegistry.Register("str_index_of", StrIndexOf);
            // `str_index` is a short alias for `str_index_of`.
            BuiltInRegistry.Register("str_index", StrIndexOf);
            BuiltInRegistry.Register("str_last_index_of", StrLastIndexOf);
            BuiltInRegistry.Register("str_substring", StrSubstring);
            BuiltInRegistry.Register("str_repeat", StrRepeat);
            BuiltInRegistry.Register("str_pad_left", StrPadLeft);
            BuiltInRegistry.Register("str_pad_right", StrPadRight);
            BuiltInRegistry.Register("str_reverse", StrReverse);
            BuiltInRegistry.Register("str_chars", StrChars);
            BuiltInRegistry.Register("str_bytes", StrBytes);
            BuiltInRegistry.Register("str_from_bytes", StrFromBytes);
            BuiltInRegistry.Register("str_to_int", StrToInt);
            BuiltInRegistry.Register("str_to_float", StrToFloat);
            BuiltInRegistry.Register("str_is_digit", StrIsDigit);
            BuiltInRegistry.Register("str_is_alpha", StrIsAlpha);
            BuiltInRegistry.Register("str_is_alnum", StrIsAlnum);
            BuiltInRegistry.Register("str_is_whitespace", StrIsWhitespace);
            BuiltInRegistry.Register("str_is_upper", StrIsUpper);
            BuiltInRegistry.Register("str_is_lower", StrIsLower);
            BuiltInRegistry.Register("str_code_at", StrCodeAt);
            BuiltInRegistry.Register("str_from_code", StrFromCode);
            BuiltInRegistry.Register("str_count", StrCount);
            BuiltInRegistry.Register("str_concat", StrConcat);
            BuiltInRegistry.Register("str_format", StrFormat);
            BuiltInRegistry.Register("regex_match", RegexMatchFn);
            BuiltInRegistry.Register("regex_test", RegexTestFn);
            BuiltInRegistry.Register("regex_replace", RegexReplaceFn);
            BuiltInRegistry.Register("regex_split", RegexSplitFn);
            BuiltInRegistry.Register("regex_find_all", RegexFindAllFn);
            BuiltInRegistry.Register("str_lines", StrLines);
            BuiltInRegistry.Register("str_encode_utf8", StrBytes);
            BuiltInRegistry.Register("str_decode_utf8", StrFromBytes);
            BuiltInRegistry.Register("str_hex_encode", StrHexEncode);
            BuiltInRegistry.Register("str_hex_decode", StrHexDecode);
            BuiltInRegistry.Register("str_base64_encode", StrBase64Encode);
            BuiltInRegistry.Register("str_base64_decode", StrBase64Decode);
        }

        private static string Get(RuntimeValue v) => v is StringValue sv ? sv.Value : v?.ToString() ?? "";

        private static RuntimeResult Str(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(args[0]?.ToString() ?? ""), ctx, p1, p2);
        }

        private static RuntimeResult Repr(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("repr", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(ReprOf(args[0])), ctx, p1, p2);
        }

        private static string ReprOf(RuntimeValue v)
        {
            switch (v)
            {
                case null: return "null";
                case StringValue sv: return "\"" + sv.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                case NullValue: return "null";
                case BooleanValue bv: return bv.Value ? "true" : "false";
                case ListValue lv: return "[" + string.Join(", ", lv.Elements.Select(ReprOf)) + "]";
                case TupleValue tv: return "(" + string.Join(", ", tv.Elements.Select(ReprOf)) + ")";
                case SetValue setv: return "{" + string.Join(", ", setv.Elements.Select(ReprOf)) + "}";
                case MapValue mv: return "{" + string.Join(", ", mv.Pairs.Select(p => ReprOf(p.Key) + ": " + ReprOf(p.Value))) + "}";
                default: return v?.ToString() ?? "null";
            }
        }

        private static RuntimeResult StrLen(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_len", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            var info = StringInfo.ParseCombiningCharacters(s);
            return Ok(new IntegerValue(info.Length), ctx, p1, p2);
        }

        private static RuntimeResult StrUpper(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_upper", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(Get(args[0]).ToUpperInvariant()), ctx, p1, p2);
        }

        private static RuntimeResult StrLower(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_lower", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(Get(args[0]).ToLowerInvariant()), ctx, p1, p2);
        }

        private static RuntimeResult StrCapitalize(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_capitalize", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            if (s.Length == 0) return Ok(new StringValue(""), ctx, p1, p2);
            return Ok(new StringValue(char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant()), ctx, p1, p2);
        }

        private static RuntimeResult StrTitle(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_title", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            var sb = new StringBuilder(s.Length);
            bool atWord = true;
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c)) { atWord = true; sb.Append(c); }
                else if (atWord) { sb.Append(char.ToUpperInvariant(c)); atWord = false; }
                else sb.Append(char.ToLowerInvariant(c));
            }
            return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
        }

        private static RuntimeResult StrTrim(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_trim", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(Get(args[0]).Trim()), ctx, p1, p2);
        }

        private static RuntimeResult StrTrimStart(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_trim_start", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(Get(args[0]).TrimStart()), ctx, p1, p2);
        }

        private static RuntimeResult StrTrimEnd(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_trim_end", args, 1, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(Get(args[0]).TrimEnd()), ctx, p1, p2);
        }

        private static RuntimeResult StrSplit(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_split", args, 2, ctx, p1, p2, out var err)) return err;
            var parts = Get(args[0]).Split(new[] { Get(args[1]) }, StringSplitOptions.None);
            return Ok(new ListValue(Strings(parts)), ctx, p1, p2);
        }

        private static RuntimeResult StrJoin(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_join", args, 2, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ListValue lv) return Fail(ctx, p1, p2, "str_join: first arg must be a list");
            var sep = Get(args[1]);
            return Ok(new StringValue(string.Join(sep, lv.Elements.Select(e => e is StringValue sv ? sv.Value : e?.ToString() ?? ""))), ctx, p1, p2);
        }

        private static RuntimeResult StrReplace(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_replace", args, 3, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(Get(args[0]).Replace(Get(args[1]), Get(args[2]))), ctx, p1, p2);
        }

        private static RuntimeResult StrStartsWith(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_starts_with", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool(Get(args[0]).StartsWith(Get(args[1]), StringComparison.Ordinal)), ctx, p1, p2);
        }

        private static RuntimeResult StrEndsWith(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_ends_with", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool(Get(args[0]).EndsWith(Get(args[1]), StringComparison.Ordinal)), ctx, p1, p2);
        }

        private static RuntimeResult StrContains(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_contains", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool(Get(args[0]).Contains(Get(args[1]), StringComparison.Ordinal)), ctx, p1, p2);
        }

        private static RuntimeResult StrIndexOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("str_index_of", args, 2, 3, ctx, p1, p2, out var err)) return err;
            int start = args.Count == 3 ? AsInt(args[2]) : 0;
            var s = Get(args[0]); start = Math.Clamp(start, 0, s.Length);
            return Ok(new IntegerValue(s.IndexOf(Get(args[1]), start, StringComparison.Ordinal)), ctx, p1, p2);
        }

        private static RuntimeResult StrLastIndexOf(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_last_index_of", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(new IntegerValue(Get(args[0]).LastIndexOf(Get(args[1]), StringComparison.Ordinal)), ctx, p1, p2);
        }

        private static RuntimeResult StrSubstring(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("str_substring", args, 2, 3, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            int start = AsInt(args[1]);
            int end = args.Count == 3 ? AsInt(args[2]) : s.Length;
            if (start < 0) start = s.Length + start;
            if (end < 0) end = s.Length + end;
            start = Math.Clamp(start, 0, s.Length);
            end = Math.Clamp(end, 0, s.Length);
            if (end < start) end = start;
            return Ok(new StringValue(s.Substring(start, end - start)), ctx, p1, p2);
        }

        private static RuntimeResult StrRepeat(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_repeat", args, 2, ctx, p1, p2, out var err)) return err;
            int n = Math.Max(0, AsInt(args[1]));
            var sb = new StringBuilder();
            var s = Get(args[0]);
            for (int i = 0; i < n; i++) sb.Append(s);
            return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
        }

        private static RuntimeResult StrPadLeft(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("str_pad_left", args, 2, 3, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            int width = AsInt(args[1]);
            char ch = args.Count == 3 ? (Get(args[2]).Length > 0 ? Get(args[2])[0] : ' ') : ' ';
            return Ok(new StringValue(s.PadLeft(width, ch)), ctx, p1, p2);
        }

        private static RuntimeResult StrPadRight(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("str_pad_right", args, 2, 3, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            int width = AsInt(args[1]);
            char ch = args.Count == 3 ? (Get(args[2]).Length > 0 ? Get(args[2])[0] : ' ') : ' ';
            return Ok(new StringValue(s.PadRight(width, ch)), ctx, p1, p2);
        }

        private static RuntimeResult StrReverse(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_reverse", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            var arr = s.ToCharArray();
            Array.Reverse(arr);
            return Ok(new StringValue(new string(arr)), ctx, p1, p2);
        }

        private static RuntimeResult StrChars(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_chars", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            var indices = StringInfo.ParseCombiningCharacters(s);
            var list = new List<RuntimeValue>();
            for (int i = 0; i < indices.Length; i++)
            {
                int start = indices[i];
                int len = (i + 1 < indices.Length) ? indices[i + 1] - start : s.Length - start;
                list.Add(new StringValue(s.Substring(start, len)));
            }
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult StrBytes(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_bytes", args, 1, ctx, p1, p2, out var err)) return err;
            var bytes = Encoding.UTF8.GetBytes(Get(args[0]));
            var list = new List<RuntimeValue>(bytes.Length);
            foreach (var b in bytes) list.Add(new ByteValue(b));
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult StrFromBytes(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_from_bytes", args, 1, ctx, p1, p2, out var err)) return err;
            if (args[0] is not ListValue lv) return Fail(ctx, p1, p2, "str_from_bytes: arg must be a list of bytes");
            var bytes = new byte[lv.Elements.Count];
            for (int i = 0; i < lv.Elements.Count; i++) bytes[i] = (byte)(AsInt(lv.Elements[i]) & 0xff);
            return Ok(new StringValue(Encoding.UTF8.GetString(bytes)), ctx, p1, p2);
        }

        private static RuntimeResult StrToInt(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectRangeArgs("str_to_int", args, 1, 2, ctx, p1, p2, out var err)) return err;
            int radix = args.Count == 2 ? AsInt(args[1]) : 10;
            if (radix < 2 || radix > 36) return Fail(ctx, p1, p2, "str_to_int: radix must be in 2..36");
            try { return Ok(new LongValue(Convert.ToInt64(Get(args[0]).Trim(), radix)), ctx, p1, p2); }
            catch { return OkNull(ctx, p1, p2); }
        }

        private static RuntimeResult StrToFloat(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_to_float", args, 1, ctx, p1, p2, out var err)) return err;
            if (double.TryParse(Get(args[0]).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return Ok(new DoubleValue(d), ctx, p1, p2);
            return OkNull(ctx, p1, p2);
        }

        private static RuntimeResult StrIsDigit(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_is_digit", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            return Ok(MakeBool(s.Length > 0 && s.All(char.IsDigit)), ctx, p1, p2);
        }

        private static RuntimeResult StrIsAlpha(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_is_alpha", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            return Ok(MakeBool(s.Length > 0 && s.All(char.IsLetter)), ctx, p1, p2);
        }

        private static RuntimeResult StrIsAlnum(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_is_alnum", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            return Ok(MakeBool(s.Length > 0 && s.All(char.IsLetterOrDigit)), ctx, p1, p2);
        }

        private static RuntimeResult StrIsWhitespace(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_is_whitespace", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            return Ok(MakeBool(s.Length > 0 && s.All(char.IsWhiteSpace)), ctx, p1, p2);
        }

        private static RuntimeResult StrIsUpper(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_is_upper", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            return Ok(MakeBool(s.Length > 0 && s.All(c => !char.IsLetter(c) || char.IsUpper(c)) && s.Any(char.IsUpper)), ctx, p1, p2);
        }

        private static RuntimeResult StrIsLower(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_is_lower", args, 1, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            return Ok(MakeBool(s.Length > 0 && s.All(c => !char.IsLetter(c) || char.IsLower(c)) && s.Any(char.IsLower)), ctx, p1, p2);
        }

        private static RuntimeResult StrCodeAt(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_code_at", args, 2, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]);
            int idx = AsInt(args[1]);
            if (idx < 0) idx = s.Length + idx;
            if (idx < 0 || idx >= s.Length) return OkNull(ctx, p1, p2);
            return Ok(new IntegerValue(s[idx]), ctx, p1, p2);
        }

        private static RuntimeResult StrFromCode(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("str_from_code", args, 1, ctx, p1, p2, out var err)) return err;
            var sb = new StringBuilder();
            foreach (var a in args) sb.Append((char)AsInt(a));
            return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
        }

        private static RuntimeResult StrCount(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_count", args, 2, ctx, p1, p2, out var err)) return err;
            var s = Get(args[0]); var n = Get(args[1]);
            if (n.Length == 0) return Ok(new IntegerValue(0), ctx, p1, p2);
            int count = 0, idx = 0;
            while ((idx = s.IndexOf(n, idx, StringComparison.Ordinal)) != -1) { count++; idx += n.Length; }
            return Ok(new IntegerValue(count), ctx, p1, p2);
        }

        private static RuntimeResult StrConcat(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            var sb = new StringBuilder();
            foreach (var a in args) sb.Append(a?.ToString() ?? "");
            return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
        }

        private static RuntimeResult StrFormat(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectMinArgs("str_format", args, 1, ctx, p1, p2, out var err)) return err;
            var fmt = Get(args[0]);
            var sb = new StringBuilder();
            int argIdx = 1;
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                if (c == '{' && i + 1 < fmt.Length && fmt[i + 1] == '}')
                {
                    sb.Append(argIdx < args.Count ? args[argIdx++].ToString() : "");
                    i++;
                }
                else if (c == '{' && i + 1 < fmt.Length && fmt[i + 1] == '{') { sb.Append('{'); i++; }
                else if (c == '}' && i + 1 < fmt.Length && fmt[i + 1] == '}') { sb.Append('}'); i++; }
                else sb.Append(c);
            }
            return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
        }

        private static RuntimeResult RegexMatchFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("regex_match", args, 2, ctx, p1, p2, out var err)) return err;
            var m = Regex.Match(Get(args[0]), Get(args[1]));
            if (!m.Success) return OkNull(ctx, p1, p2);
            var caps = new List<RuntimeValue>();
            foreach (Group g in m.Groups) caps.Add(new StringValue(g.Value));
            return Ok(new ListValue(caps), ctx, p1, p2);
        }

        private static RuntimeResult RegexTestFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("regex_test", args, 2, ctx, p1, p2, out var err)) return err;
            return Ok(MakeBool(Regex.IsMatch(Get(args[0]), Get(args[1]))), ctx, p1, p2);
        }

        private static RuntimeResult RegexReplaceFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("regex_replace", args, 3, ctx, p1, p2, out var err)) return err;
            return Ok(new StringValue(Regex.Replace(Get(args[0]), Get(args[1]), Get(args[2]))), ctx, p1, p2);
        }

        private static RuntimeResult RegexSplitFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("regex_split", args, 2, ctx, p1, p2, out var err)) return err;
            var parts = Regex.Split(Get(args[0]), Get(args[1]));
            return Ok(new ListValue(Strings(parts)), ctx, p1, p2);
        }

        private static RuntimeResult RegexFindAllFn(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("regex_find_all", args, 2, ctx, p1, p2, out var err)) return err;
            var matches = Regex.Matches(Get(args[0]), Get(args[1]));
            var list = new List<RuntimeValue>();
            foreach (Match m in matches) list.Add(new StringValue(m.Value));
            return Ok(new ListValue(list), ctx, p1, p2);
        }

        private static RuntimeResult StrLines(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_lines", args, 1, ctx, p1, p2, out var err)) return err;
            var parts = Get(args[0]).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            return Ok(new ListValue(Strings(parts)), ctx, p1, p2);
        }

        private static RuntimeResult StrHexEncode(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_hex_encode", args, 1, ctx, p1, p2, out var err)) return err;
            byte[] bytes;
            if (args[0] is ListValue lv)
            {
                bytes = new byte[lv.Elements.Count];
                for (int i = 0; i < lv.Elements.Count; i++) bytes[i] = (byte)(AsInt(lv.Elements[i]) & 0xff);
            }
            else bytes = Encoding.UTF8.GetBytes(Get(args[0]));
            return Ok(new StringValue(Convert.ToHexString(bytes).ToLowerInvariant()), ctx, p1, p2);
        }

        private static RuntimeResult StrHexDecode(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_hex_decode", args, 1, ctx, p1, p2, out var err)) return err;
            try
            {
                var bytes = Convert.FromHexString(Get(args[0]));
                var list = new List<RuntimeValue>(bytes.Length);
                foreach (var b in bytes) list.Add(new ByteValue(b));
                return Ok(new ListValue(list), ctx, p1, p2);
            }
            catch { return Fail(ctx, p1, p2, "str_hex_decode: invalid hex"); }
        }

        private static RuntimeResult StrBase64Encode(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_base64_encode", args, 1, ctx, p1, p2, out var err)) return err;
            byte[] bytes;
            if (args[0] is ListValue lv)
            {
                bytes = new byte[lv.Elements.Count];
                for (int i = 0; i < lv.Elements.Count; i++) bytes[i] = (byte)(AsInt(lv.Elements[i]) & 0xff);
            }
            else bytes = Encoding.UTF8.GetBytes(Get(args[0]));
            return Ok(new StringValue(Convert.ToBase64String(bytes)), ctx, p1, p2);
        }

        private static RuntimeResult StrBase64Decode(Context ctx, List<RuntimeValue> args, Position p1, Position p2)
        {
            if (!ExpectArgs("str_base64_decode", args, 1, ctx, p1, p2, out var err)) return err;
            try
            {
                var bytes = Convert.FromBase64String(Get(args[0]));
                var list = new List<RuntimeValue>(bytes.Length);
                foreach (var b in bytes) list.Add(new ByteValue(b));
                return Ok(new ListValue(list), ctx, p1, p2);
            }
            catch { return Fail(ctx, p1, p2, "str_base64_decode: invalid base64"); }
        }
    }
}

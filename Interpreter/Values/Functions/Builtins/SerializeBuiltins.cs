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
    // std.prelude.serialize — JSON encode/decode over native Ra values.
    //
    // Hand-written recursive writer + recursive-descent parser: NO reflection
    // and NO System.Text.Json, so it is fully AOT-safe and behaves identically
    // on every platform. Maps <-> objects, lists/sets/tuples <-> arrays,
    // strings/numbers/bools/null map to their JSON equivalents.
    public static class SerializeBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("json_stringify", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("json_stringify", args, 1, ctx, p1, p2, out var err)) return err;
                var sb = new StringBuilder();
                if (!Write(args[0], sb, out var werr)) return Fail(ctx, p1, p2, werr!);
                return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
            });

            BuiltInRegistry.Register("json_pretty", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("json_pretty", args, 1, ctx, p1, p2, out var err)) return err;
                var sb = new StringBuilder();
                if (!WritePretty(args[0], sb, 0, out var werr)) return Fail(ctx, p1, p2, werr!);
                return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
            });

            BuiltInRegistry.Register("json_parse", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("json_parse", args, 1, ctx, p1, p2, out var err)) return err;
                var parser = new JsonParser(AsString(args[0]));
                try
                {
                    var v = parser.ParseDocument();
                    return Ok(v, ctx, p1, p2);
                }
                catch (JsonError je) { return Fail(ctx, p1, p2, "json_parse: " + je.Message); }
            });
        }

        // ---- writer ------------------------------------------------------

        private static bool Write(RuntimeValue v, StringBuilder sb, out string? err)
        {
            err = null;
            switch (v.Type)
            {
                case RuntimeValueType.Null: sb.Append("null"); return true;
                case RuntimeValueType.Boolean: sb.Append(((BooleanValue)v).Value ? "true" : "false"); return true;
                case RuntimeValueType.String: WriteString(((StringValue)v).Value, sb); return true;
                case RuntimeValueType.List: return WriteArray(((ListValue)v).Elements, sb, out err);
                case RuntimeValueType.Set: return WriteArray(new List<RuntimeValue>(((SetValue)v).Elements), sb, out err);
                case RuntimeValueType.Tuple: return WriteArray(((TupleValue)v).Elements, sb, out err);
                case RuntimeValueType.Map: return WriteObject(((MapValue)v).Pairs, sb, out err);
                default:
                    if (IsNumber(v.Type)) { sb.Append(NumberJson(v)); return true; }
                    err = $"json_stringify: cannot serialize a value of type '{TypeKind(v)}'";
                    return false;
            }
        }

        private static bool WriteArray(List<RuntimeValue> items, StringBuilder sb, out string? err)
        {
            err = null;
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                if (!Write(items[i], sb, out err)) return false;
            }
            sb.Append(']');
            return true;
        }

        private static bool WriteObject(List<(RuntimeValue Key, RuntimeValue Value)> pairs, StringBuilder sb, out string? err)
        {
            err = null;
            sb.Append('{');
            for (int i = 0; i < pairs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteString(pairs[i].Key is StringValue sk ? sk.Value : AsString(pairs[i].Key), sb);
                sb.Append(':');
                if (!Write(pairs[i].Value, sb, out err)) return false;
            }
            sb.Append('}');
            return true;
        }

        private static bool WritePretty(RuntimeValue v, StringBuilder sb, int depth, out string? err)
        {
            err = null;
            string pad = new string(' ', (depth + 1) * 2);
            string padEnd = new string(' ', depth * 2);
            switch (v.Type)
            {
                case RuntimeValueType.List:
                {
                    var items = ((ListValue)v).Elements;
                    if (items.Count == 0) { sb.Append("[]"); return true; }
                    sb.Append("[\n");
                    for (int i = 0; i < items.Count; i++)
                    {
                        sb.Append(pad);
                        if (!WritePretty(items[i], sb, depth + 1, out err)) return false;
                        if (i < items.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(padEnd).Append(']');
                    return true;
                }
                case RuntimeValueType.Map:
                {
                    var pairs = ((MapValue)v).Pairs;
                    if (pairs.Count == 0) { sb.Append("{}"); return true; }
                    sb.Append("{\n");
                    for (int i = 0; i < pairs.Count; i++)
                    {
                        sb.Append(pad);
                        WriteString(pairs[i].Key is StringValue sk ? sk.Value : AsString(pairs[i].Key), sb);
                        sb.Append(": ");
                        if (!WritePretty(pairs[i].Value, sb, depth + 1, out err)) return false;
                        if (i < pairs.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(padEnd).Append('}');
                    return true;
                }
                default:
                    return Write(v, sb, out err);
            }
        }

        private static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static bool IsNumber(RuntimeValueType t) => t is
            RuntimeValueType.Number or RuntimeValueType.Integer or RuntimeValueType.Long or
            RuntimeValueType.Short or RuntimeValueType.Byte or RuntimeValueType.UnsignedInteger or
            RuntimeValueType.UnsignedLong or RuntimeValueType.UnsignedShort or RuntimeValueType.Int128 or
            RuntimeValueType.UnsignedInt128 or RuntimeValueType.Float or RuntimeValueType.Double or
            RuntimeValueType.Decimal;

        private static string NumberJson(RuntimeValue v)
        {
            switch (v.Type)
            {
                case RuntimeValueType.Float:
                case RuntimeValueType.Double:
                case RuntimeValueType.Decimal:
                case RuntimeValueType.Number:
                    double d = AsDouble(v);
                    if (double.IsNaN(d) || double.IsInfinity(d)) return "null"; // JSON has no NaN/Inf
                    return d.ToString("R", CultureInfo.InvariantCulture);
                default:
                    return AsLong(v).ToString(CultureInfo.InvariantCulture);
            }
        }

        // ---- parser ------------------------------------------------------

        private sealed class JsonError : Exception
        {
            public JsonError(string m) : base(m) { }
        }

        private sealed class JsonParser
        {
            private readonly string _s;
            private int _i;
            public JsonParser(string s) { _s = s; _i = 0; }

            public RuntimeValue ParseDocument()
            {
                SkipWs();
                var v = ParseValue();
                SkipWs();
                if (_i != _s.Length) throw new JsonError($"trailing characters at offset {_i}");
                return v;
            }

            private RuntimeValue ParseValue()
            {
                SkipWs();
                if (_i >= _s.Length) throw new JsonError("unexpected end of input");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return new StringValue(ParseString());
                    case 't': Expect("true"); return BooleanValue.Of(true);
                    case 'f': Expect("false"); return BooleanValue.Of(false);
                    case 'n': Expect("null"); return NullValue.Null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw new JsonError($"unexpected character '{c}' at offset {_i}");
                }
            }

            private RuntimeValue ParseObject()
            {
                _i++; // {
                var pairs = new List<(RuntimeValue, RuntimeValue)>();
                SkipWs();
                if (Peek() == '}') { _i++; return new MapValue(pairs); }
                while (true)
                {
                    SkipWs();
                    if (Peek() != '"') throw new JsonError($"expected string key at offset {_i}");
                    string key = ParseString();
                    SkipWs();
                    if (Peek() != ':') throw new JsonError($"expected ':' at offset {_i}");
                    _i++;
                    var val = ParseValue();
                    pairs.Add((new StringValue(key), val));
                    SkipWs();
                    char n = Peek();
                    if (n == ',') { _i++; continue; }
                    if (n == '}') { _i++; break; }
                    throw new JsonError($"expected ',' or '}}' at offset {_i}");
                }
                return new MapValue(pairs);
            }

            private RuntimeValue ParseArray()
            {
                _i++; // [
                var items = new List<RuntimeValue>();
                SkipWs();
                if (Peek() == ']') { _i++; return new ListValue(items); }
                while (true)
                {
                    items.Add(ParseValue());
                    SkipWs();
                    char n = Peek();
                    if (n == ',') { _i++; continue; }
                    if (n == ']') { _i++; break; }
                    throw new JsonError($"expected ',' or ']' at offset {_i}");
                }
                return new ListValue(items);
            }

            private string ParseString()
            {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new JsonError("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') break;
                    if (c == '\\')
                    {
                        if (_i >= _s.Length) throw new JsonError("unterminated escape");
                        char e = _s[_i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (_i + 4 > _s.Length) throw new JsonError("truncated \\u escape");
                                int code = int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                sb.Append((char)code);
                                _i += 4;
                                break;
                            default: throw new JsonError($"invalid escape '\\{e}'");
                        }
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }

            private RuntimeValue ParseNumber()
            {
                int start = _i;
                bool isFloat = false;
                if (Peek() == '-') _i++;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c >= '0' && c <= '9') { _i++; }
                    else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = isFloat || c == '.' || c == 'e' || c == 'E'; _i++; }
                    else break;
                }
                string num = _s.Substring(start, _i - start);
                if (!isFloat && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    return NumberFor(l);
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return new DoubleValue(d);
                throw new JsonError($"invalid number '{num}'");
            }

            private void Expect(string word)
            {
                if (_i + word.Length > _s.Length || _s.Substring(_i, word.Length) != word)
                    throw new JsonError($"expected '{word}' at offset {_i}");
                _i += word.Length;
            }

            private char Peek() => _i < _s.Length ? _s[_i] : '\0';

            private void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                    else break;
                }
            }
        }
    }
}

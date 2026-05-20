using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Runtime.Csharp
{
    /// <summary>
    /// Bridge between Ra <see cref="RuntimeValue"/> and ordinary CLR <c>object</c>.
    ///
    /// Two directions:
    /// 1. <see cref="FormatLiteral"/> renders a Ra value as a fragment of C# source so it can
    ///    be spliced inline via the <c>%{ ... }</c> interpolation hooks at compile time.
    /// 2. <see cref="ToRuntimeValue"/> wraps the value returned by the compiled script as the
    ///    matching <see cref="RuntimeValue"/> subclass, so the rest of the interpreter never
    ///    sees a raw <c>object</c>.
    /// </summary>
    public static class CsharpInteropMarshaller
    {
        public static bool TryFormatLiteral(RuntimeValue value, string? typeHint, out string formatted, out string? error)
        {
            error = null;

            if (typeHint != null)
            {
                string lower = typeHint.ToLowerInvariant();
                switch (lower)
                {
                    case "raw":
                    case "verbatim":
                        formatted = value is StringValue rs ? rs.Value : Stringify(value);
                        return true;

                    case "str":
                    case "string":
                        formatted = EncodeCsharpString(value is StringValue sv ? sv.Value : Stringify(value));
                        return true;

                    case "char":
                        if (value is StringValue csv && csv.Value.Length == 1)
                        {
                            formatted = EncodeCsharpChar(csv.Value[0]);
                            return true;
                        }
                        if (TryCoerceLong(value, out long charBits))
                        {
                            formatted = EncodeCsharpChar((char)charBits);
                            return true;
                        }
                        error = "value cannot be interpolated as a char literal";
                        formatted = "";
                        return false;

                    case "i32":
                    case "int":
                        if (TryCoerceLong(value, out long i32)) { formatted = ((int)i32).ToString(CultureInfo.InvariantCulture); return true; }
                        error = "value cannot be interpolated as an int literal";
                        formatted = "";
                        return false;

                    case "u32":
                    case "uint":
                        if (TryCoerceLong(value, out long u32)) { formatted = ((uint)u32).ToString(CultureInfo.InvariantCulture) + "u"; return true; }
                        error = "value cannot be interpolated as a uint literal";
                        formatted = "";
                        return false;

                    case "i64":
                    case "long":
                        if (TryCoerceLong(value, out long i64)) { formatted = i64.ToString(CultureInfo.InvariantCulture) + "L"; return true; }
                        error = "value cannot be interpolated as a long literal";
                        formatted = "";
                        return false;

                    case "u64":
                    case "ulong":
                        if (TryCoerceLong(value, out long u64)) { formatted = ((ulong)u64).ToString(CultureInfo.InvariantCulture) + "UL"; return true; }
                        error = "value cannot be interpolated as a ulong literal";
                        formatted = "";
                        return false;

                    case "f32":
                    case "float":
                        if (TryCoerceDouble(value, out double f32)) { formatted = ((float)f32).ToString("R", CultureInfo.InvariantCulture) + "f"; return true; }
                        error = "value cannot be interpolated as a float literal";
                        formatted = "";
                        return false;

                    case "f64":
                    case "double":
                        if (TryCoerceDouble(value, out double f64)) { formatted = f64.ToString("R", CultureInfo.InvariantCulture) + "d"; return true; }
                        error = "value cannot be interpolated as a double literal";
                        formatted = "";
                        return false;

                    case "decimal":
                    case "m":
                        if (TryCoerceDouble(value, out double dec)) { formatted = ((decimal)dec).ToString(CultureInfo.InvariantCulture) + "m"; return true; }
                        error = "value cannot be interpolated as a decimal literal";
                        formatted = "";
                        return false;

                    case "bool":
                        if (value is BooleanValue bb) { formatted = bb.Value ? "true" : "false"; return true; }
                        if (TryCoerceLong(value, out long lb)) { formatted = (lb != 0) ? "true" : "false"; return true; }
                        error = "value cannot be interpolated as a bool literal";
                        formatted = "";
                        return false;

                    default:
                        error = $"unknown csharp interpolation type hint '{typeHint}'. expected one of: raw, str, char, int, uint, long, ulong, float, double, decimal, bool";
                        formatted = "";
                        return false;
                }
            }

            return TryFormatDefault(value, out formatted, out error);
        }

        private static bool TryFormatDefault(RuntimeValue value, out string formatted, out string? error)
        {
            error = null;

            switch (value)
            {
                case NullValue _:
                    formatted = "null";
                    return true;
                case BooleanValue bv:
                    formatted = bv.Value ? "true" : "false";
                    return true;
                case StringValue sv:
                    formatted = EncodeCsharpString(sv.Value);
                    return true;
                case ByteValue byv:
                    formatted = $"((byte){byv.Value.ToString(CultureInfo.InvariantCulture)})";
                    return true;
                case ShortValue shv:
                    formatted = $"((short){shv.Value.ToString(CultureInfo.InvariantCulture)})";
                    return true;
                case UnsignedShortValue ushv:
                    formatted = $"((ushort){ushv.Value.ToString(CultureInfo.InvariantCulture)})";
                    return true;
                case IntegerValue iv:
                    formatted = iv.Value.ToString(CultureInfo.InvariantCulture);
                    return true;
                case UnsignedIntegerValue uiv:
                    formatted = uiv.Value.ToString(CultureInfo.InvariantCulture) + "u";
                    return true;
                case LongValue lv:
                    formatted = lv.Value.ToString(CultureInfo.InvariantCulture) + "L";
                    return true;
                case UnsignedLongValue ulv:
                    formatted = ulv.Value.ToString(CultureInfo.InvariantCulture) + "UL";
                    return true;
                case FloatValue fv:
                    formatted = fv.Value.ToString("R", CultureInfo.InvariantCulture) + "f";
                    return true;
                case DoubleValue dv:
                    formatted = dv.Value.ToString("R", CultureInfo.InvariantCulture) + "d";
                    return true;
                case DecimalValue dec:
                    formatted = dec.Value.ToString(CultureInfo.InvariantCulture) + "m";
                    return true;
                case NumberValue nv:
                    formatted = nv.Value.ToString();
                    return true;
                case NativeHandleValue nh:
                    formatted = $"((System.IntPtr){nh.Handle.ToInt64().ToString(CultureInfo.InvariantCulture)}L)";
                    return true;
                case ListValue list:
                {
                    var sb = new StringBuilder();
                    sb.Append("new object?[] { ");
                    for (int i = 0; i < list.Elements.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        if (!TryFormatDefault(list.Elements[i], out var inner, out var innerErr))
                        {
                            error = $"list element {i}: {innerErr}";
                            formatted = "";
                            return false;
                        }
                        sb.Append(inner);
                    }
                    sb.Append(" }");
                    formatted = sb.ToString();
                    return true;
                }
                default:
                    error = $"cannot interpolate Ra value of kind '{value.Type}' into csharp. use a type hint like %{{x:raw}} or %{{x:str}} to coerce it explicitly";
                    formatted = "";
                    return false;
            }
        }

        public static string EncodeCsharpString(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\0': sb.Append("\\0"); break;
                    case '\a': sb.Append("\\a"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\v': sb.Append("\\v"); break;
                    default:
                        if (c < 0x20 || c == 0x7F)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string EncodeCsharpChar(char c)
        {
            switch (c)
            {
                case '\\': return "'\\\\'";
                case '\'': return "'\\''";
                case '\0': return "'\\0'";
                case '\n': return "'\\n'";
                case '\r': return "'\\r'";
                case '\t': return "'\\t'";
                case '\b': return "'\\b'";
                case '\f': return "'\\f'";
                case '\v': return "'\\v'";
                default:
                    if (c < 0x20 || c == 0x7F)
                        return "'\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + "'";
                    return $"'{c}'";
            }
        }

        public static RuntimeValue ToRuntimeValue(object? value, string? declaredReturnType)
        {
            if (declaredReturnType != null)
                return CoerceToDeclaredType(value, declaredReturnType);

            return ConvertFreely(value);
        }

        private static RuntimeValue ConvertFreely(object? value)
        {
            switch (value)
            {
                case null: return new NullValue();
                case bool b: return BooleanValue.Of(b);
                case string s: return new StringValue(s);
                case char ch: return new StringValue(ch.ToString());
                case sbyte sb: return new ShortValue((short)sb);
                case byte by: return new ByteValue(by);
                case short sh: return new ShortValue(sh);
                case ushort ush: return new UnsignedShortValue(ush);
                case int i: return new IntegerValue(i);
                case uint ui: return new UnsignedIntegerValue(ui);
                case long l: return new LongValue(l);
                case ulong ul: return new UnsignedLongValue(ul);
                case float f: return new FloatValue(f);
                case double d: return new DoubleValue(d);
                case decimal dec: return new DecimalValue(dec);
                case IntPtr ip: return new NativeHandleValue(ip, NativeHandleKind.Pointer);
                case UIntPtr uip: return new NativeHandleValue((IntPtr)(long)(ulong)uip, NativeHandleKind.Pointer);
                case IDictionary dict:
                {
                    var pairs = new List<(RuntimeValue Key, RuntimeValue Value)>(dict.Count);
                    foreach (DictionaryEntry kv in dict)
                    {
                        pairs.Add((ConvertFreely(kv.Key), ConvertFreely(kv.Value)));
                    }
                    return new MapValue(pairs);
                }
                case IEnumerable enumerable:
                {
                    var list = new List<RuntimeValue>();
                    foreach (var elt in enumerable) list.Add(ConvertFreely(elt));
                    return new ListValue(list);
                }
                default:
                    return new StringValue(value.ToString() ?? "");
            }
        }

        private static RuntimeValue CoerceToDeclaredType(object? value, string declaredReturnType)
        {
            string t = declaredReturnType.Trim().ToLowerInvariant();

            switch (t)
            {
                case "void":
                case "unit":
                case "null":
                    return new NullValue();
                case "string":
                case "str":
                    return new StringValue(value switch { null => "", string s => s, _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" });
                case "bool":
                case "boolean":
                    return BooleanValue.Of(value switch { null => false, bool b => b, _ => System.Convert.ToBoolean(value, CultureInfo.InvariantCulture) });
                case "byte": return new ByteValue(System.Convert.ToByte(value, CultureInfo.InvariantCulture));
                case "i8":
                case "sbyte": return new ShortValue(System.Convert.ToSByte(value, CultureInfo.InvariantCulture));
                case "i16":
                case "short": return new ShortValue(System.Convert.ToInt16(value, CultureInfo.InvariantCulture));
                case "u16":
                case "ushort": return new UnsignedShortValue(System.Convert.ToUInt16(value, CultureInfo.InvariantCulture));
                case "i32":
                case "int": return new IntegerValue(System.Convert.ToInt32(value, CultureInfo.InvariantCulture));
                case "u32":
                case "uint": return new UnsignedIntegerValue(System.Convert.ToUInt32(value, CultureInfo.InvariantCulture));
                case "i64":
                case "long": return new LongValue(System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
                case "u64":
                case "ulong": return new UnsignedLongValue(System.Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                case "f32":
                case "float": return new FloatValue(System.Convert.ToSingle(value, CultureInfo.InvariantCulture));
                case "f64":
                case "double": return new DoubleValue(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
                case "decimal":
                case "m": return new DecimalValue(System.Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                case "number": return new NumberValue(BigNumber.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0"));
                case "ptr":
                case "handle": return new NativeHandleValue(value is IntPtr ip ? ip : (IntPtr)System.Convert.ToInt64(value, CultureInfo.InvariantCulture), NativeHandleKind.Pointer);
                case "list":
                case "array":
                    if (value is IEnumerable ie && !(value is string))
                    {
                        var list = new List<RuntimeValue>();
                        foreach (var elt in ie) list.Add(ConvertFreely(elt));
                        return new ListValue(list);
                    }
                    return new ListValue(new List<RuntimeValue> { ConvertFreely(value) });
                case "map":
                case "dict":
                    if (value is IDictionary id)
                    {
                        var pairs = new List<(RuntimeValue Key, RuntimeValue Value)>(id.Count);
                        foreach (DictionaryEntry kv in id) pairs.Add((ConvertFreely(kv.Key), ConvertFreely(kv.Value)));
                        return new MapValue(pairs);
                    }
                    return new MapValue(new List<(RuntimeValue, RuntimeValue)>());
                case "any":
                case "object":
                    return ConvertFreely(value);
                default:
                    return ConvertFreely(value);
            }
        }

        private static bool TryCoerceLong(RuntimeValue value, out long result)
        {
            switch (value)
            {
                case IntegerValue iv: result = iv.Value; return true;
                case LongValue lv: result = lv.Value; return true;
                case ShortValue sv: result = sv.Value; return true;
                case ByteValue bv: result = bv.Value; return true;
                case UnsignedIntegerValue ui: result = ui.Value; return true;
                case UnsignedLongValue ul: result = unchecked((long)ul.Value); return true;
                case UnsignedShortValue us: result = us.Value; return true;
                case BooleanValue bo: result = bo.Value ? 1 : 0; return true;
                case NativeHandleValue nh: result = nh.Handle.ToInt64(); return true;
                case FloatValue fv: result = (long)fv.Value; return true;
                case DoubleValue dv: result = (long)dv.Value; return true;
                case NumberValue nv:
                    try { result = (long)nv.Value; return true; }
                    catch { result = 0; return false; }
                default: result = 0; return false;
            }
        }

        private static bool TryCoerceDouble(RuntimeValue value, out double result)
        {
            switch (value)
            {
                case FloatValue fv: result = fv.Value; return true;
                case DoubleValue dv: result = dv.Value; return true;
                case DecimalValue de: result = (double)de.Value; return true;
                case IntegerValue iv: result = iv.Value; return true;
                case LongValue lv: result = lv.Value; return true;
                case ShortValue sv: result = sv.Value; return true;
                case ByteValue bv: result = bv.Value; return true;
                case UnsignedIntegerValue ui: result = ui.Value; return true;
                case UnsignedLongValue ul: result = ul.Value; return true;
                case UnsignedShortValue us: result = us.Value; return true;
                case NumberValue nv:
                    try { result = (double)nv.Value; return true; }
                    catch { result = 0; return false; }
                default: result = 0; return false;
            }
        }

        private static string Stringify(RuntimeValue value)
        {
            return value switch
            {
                NullValue => "",
                StringValue s => s.Value,
                BooleanValue b => b.Value ? "true" : "false",
                IntegerValue iv => iv.Value.ToString(CultureInfo.InvariantCulture),
                LongValue lv => lv.Value.ToString(CultureInfo.InvariantCulture),
                FloatValue fv => fv.Value.ToString("R", CultureInfo.InvariantCulture),
                DoubleValue dv => dv.Value.ToString("R", CultureInfo.InvariantCulture),
                _ => value.ToString() ?? ""
            };
        }
    }
}

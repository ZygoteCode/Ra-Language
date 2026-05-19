using Microsoft.VisualBasic;
using RaLanguage.Errors;
using System.Globalization;
using System.Text;
using RaLanguage.Utilities;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class StringValue : RuntimeValue
    {
        public string Value { get; private set; }
        public StringValue(string value) { Value = value; }
        public sealed override RuntimeValueType Type => RuntimeValueType.String;
        public sealed override bool IsCopy => false;

        private string NormalizeNFC(string s) => s?.Normalize(NormalizationForm.FormC) ?? s;

        private string[] Graphemes()
        {
            var indices = StringInfo.ParseCombiningCharacters(Value);
            var list = new List<string>(indices.Length);
            for (int i = 0; i < indices.Length; i++)
            {
                int start = indices[i];
                int len = (i + 1 < indices.Length) ? indices[i + 1] - start : Value.Length - start;
                list.Add(Value.Substring(start, len));
            }
            return list.ToArray();
        }

        private string JoinGraphemes(IEnumerable<string> graphemes)
        {
            var sb = new StringBuilder();
            foreach (var g in graphemes) sb.Append(g);
            return sb.ToString();
        }

        private int ToIntSafe(RuntimeValue n)
        {
            if (n.Type != RuntimeValueType.Number) throw new InvalidOperationException("Expected number");
            var nv = (NumberValue)n;
            return (int)nv.Value;
        }

        public sealed override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (new StringValue(Value + s.Value).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            
            var converted = StringConversionUtility.ConvertToString(other);
            return (new StringValue(Value + converted).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
        }

        public sealed override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                var baseStr = NormalizeNFC(Value);
                var rem = NormalizeNFC(s.Value);
                var res = baseStr.Replace(rem, "");
                return (new StringValue(res).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Number)
            {
                var idx = ToIntSafe(other);
                var g = Graphemes();
                if (idx < 0) idx = g.Length + idx;
                if (idx < 0 || idx >= g.Length) return (null, IllegalOperation(other));
                var newList = g.Where((val, i) => i != idx).ToArray();
                return (new StringValue(JoinGraphemes(newList)).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.SubbedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                var times = (int)n.Value;
                if (times < 0) return (null, IllegalOperation(other));
                var sb = new StringBuilder();
                for (int i = 0; i < times; i++) sb.Append(Value);
                return (new StringValue(sb.ToString()).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.MultedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var sep = (StringValue)other;
                var parts = Value.Split(new string[] { sep.Value }, StringSplitOptions.None);
                var res = parts.Select(p => (RuntimeValue)new StringValue(p).SetContext(Context)).ToList();
                var list = new ListValue(res).SetContext(Context).SetPos(PositionStart, PositionEnd);
                return (list, null);
            }
            else if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                int partsCount = (int)n.Value;
                if (partsCount <= 0) return (null, IllegalOperation(other));
                var g = Graphemes();
                int total = g.Length;
                var result = new List<RuntimeValue>();
                int baseSize = total / partsCount;
                int extra = total % partsCount;
                int idx = 0;
                for (int p = 0; p < partsCount; p++)
                {
                    int thisSize = baseSize + (p < extra ? 1 : 0);
                    if (thisSize == 0)
                    {
                        result.Add(new StringValue("").SetContext(Context));
                    }
                    else
                    {
                        var slice = g.Skip(idx).Take(thisSize);
                        result.Add(new StringValue(JoinGraphemes(slice)).SetContext(Context));
                    }
                    idx += thisSize;
                }
                var list = new ListValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd);
                return (list, null);
            }
            return base.DivedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                return MultedBy(other);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                var a = Graphemes();
                var b = s.Graphemes();
                int max = Math.Max(a.Length, b.Length);
                var outList = new List<string>(a.Length + b.Length);
                for (int i = 0; i < max; i++)
                {
                    if (i < a.Length) outList.Add(a[i]);
                    if (i < b.Length) outList.Add(b[i]);
                }
                return (new StringValue(JoinGraphemes(outList)).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.PowedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                var idx = ToIntSafe(other);
                var g = Graphemes();
                if (g.Length == 0) return (null, IllegalOperation(other));
                int wrapped = ((idx % g.Length) + g.Length) % g.Length;
                return (new StringValue(g[wrapped]).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                var baseStr = NormalizeNFC(Value);
                var needle = NormalizeNFC(s.Value);
                return (BooleanValue.Of(baseStr.Contains(needle)), null);
            }
            return base.ModuledBy(other);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = ToIntSafe(other);
                var g = Graphemes();
                if (n < 0) return (null, IllegalOperation(other));
                if (n >= g.Length) return (new StringValue("").SetPos(PositionStart, PositionEnd).SetContext(Context), null);
                var sliced = g.Skip(n);
                return (new StringValue(JoinGraphemes(sliced)).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseLeftShiftedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = ToIntSafe(other);
                var g = Graphemes();
                if (n < 0) return (null, IllegalOperation(other));
                if (n >= g.Length) return (new StringValue("").SetPos(PositionStart, PositionEnd).SetContext(Context), null);
                var sliced = g.Take(g.Length - n);
                return (new StringValue(JoinGraphemes(sliced)).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseRightShiftedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                var left = Graphemes();
                var right = new HashSet<string>(s.Graphemes());
                var seen = new HashSet<string>();
                var outList = new List<string>();
                foreach (var g in left)
                {
                    if (right.Contains(g) && !seen.Contains(g))
                    {
                        outList.Add(g);
                        seen.Add(g);
                    }
                }
                return (new StringValue(JoinGraphemes(outList)).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Set)
            {
                SetValue set = (SetValue)other;
                var left = Graphemes();
                var outList = new List<string>();
                foreach (var g in left)
                {
                    foreach (var el in set.Elements)
                    {
                        if (el is StringValue sv && sv.Value == g)
                        {
                            outList.Add(g);
                            break;
                        }
                    }
                }
                return (new StringValue(JoinGraphemes(outList)).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseAndedBy(other);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                var left = Graphemes();
                var right = s.Graphemes();
                var seen = new HashSet<string>();
                var outList = new List<string>();
                foreach (var g in left)
                {
                    if (!seen.Contains(g)) { outList.Add(g); seen.Add(g); }
                }
                foreach (var g in right)
                {
                    if (!seen.Contains(g)) { outList.Add(g); seen.Add(g); }
                }
                return (new StringValue(JoinGraphemes(outList)).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Set)
            {
                var left = Graphemes();
                var seen = new HashSet<string>(left);
                var outList = new List<string>(left);
                var set = (SetValue)other;
                foreach (var el in set.Elements)
                {
                    if (el is StringValue sv && !seen.Contains(sv.Value))
                    {
                        outList.Add(sv.Value);
                        seen.Add(sv.Value);
                    }
                }
                return (new StringValue(JoinGraphemes(outList)).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseOredBy(other);
        }

        public sealed override (RuntimeValue?, Error?) ListAccess(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                var idx = ToIntSafe(other);
                var g = Graphemes();
                int i = idx;
                if (i < 0) i = g.Length + i;
                if (i < 0 || i >= g.Length) return (null, IllegalOperation(other));
                return (new StringValue(g[i]).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.ListAccess(other);
        }

        public sealed override (RuntimeValue?, Error?) ListSet(RuntimeValue index, RuntimeValue value)
        {
            if (index.Type == RuntimeValueType.Number && value.Type == RuntimeValueType.String)
            {
                var idx = ToIntSafe(index);
                var g = Graphemes();
                int i = idx;
                if (i < 0) i = g.Length + i;
                if (i < 0 || i >= g.Length) return (null, IllegalOperation(index));

                string result = "";

                for (int j = 0; j < g.Length; j++)
                {
                    if (j == i)
                    {
                        result += ((StringValue)value).Value;
                        continue;
                    }

                    result += g[j];
                }

                Value = result;
                return (new StringValue(result).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (index.Type == RuntimeValueType.List && value.Type == RuntimeValueType.String)
            {
                var g = Graphemes();
                ListValue list = (ListValue)index;
                List<int> indexes = new List<int>();

                foreach (var element in list.Elements)
                {
                    if (element.Type != RuntimeValueType.Number)
                    {
                        return (null, IllegalOperation(index));
                    }

                    var idx = ToIntSafe(((NumberValue)element));
                    if (idx < 0) idx = g.Length + idx;
                    if (idx < 0 || idx >= g.Length) return (null, IllegalOperation(index));
                    indexes.Add(idx);
                }

                StringValue v = (StringValue)value;
                string result = "";

                for (int j = 0; j < g.Length; j++)
                {
                    bool exists = false;

                    foreach (int k in indexes)
                    {
                        if (k == j)
                        {
                            result += ((StringValue)value).Value;
                            exists = true;
                            break;
                        }
                    }

                    if (exists)
                    {
                        continue;
                    }

                    result += g[j];
                }

                Value = result;
                return (new StringValue(result).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }

            return base.ListSet(index, value);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                var a = NormalizeNFC(Value);
                var b = NormalizeNFC(s.Value);
                int cmp = String.CompareOrdinal(a, b);
                return (BooleanValue.Of(cmp < 0).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.GetComparisonLt(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                var a = NormalizeNFC(Value);
                var b = NormalizeNFC(s.Value);
                int cmp = String.CompareOrdinal(a, b);
                return (BooleanValue.Of(cmp > 0).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.GetComparisonGt(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                var a = NormalizeNFC(Value);
                var b = NormalizeNFC(s.Value);
                int cmp = String.CompareOrdinal(a, b);
                return (BooleanValue.Of(cmp <= 0).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.GetComparisonLte(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                var a = NormalizeNFC(Value);
                var b = NormalizeNFC(s.Value);
                int cmp = String.CompareOrdinal(a, b);
                return (BooleanValue.Of(cmp >= 0).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.GetComparisonGte(other);
        }

        public sealed override (RuntimeValue?, Error?) Notted()
        {
            return (BooleanValue.Of(!IsTrue()).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
        }

        public sealed override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new StringValue(Strings.StrReverse(Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public sealed override bool IsTrue() => Value.Length > 0 && (Value == "true" || Value == "1");

        public sealed override RuntimeValue Copy()
        {
            // StringValue wraps an immutable string. Sharing the same instance is safe and
            // removes a per-read allocation for any string-typed variable access.
            return this;
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                var s = (StringValue)other;
                return (BooleanValue.Of(NormalizeNFC(s.Value) == NormalizeNFC(Value)).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                return (BooleanValue.Of(n.Value.ToString() == Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)other;
                return (BooleanValue.Of(b.Value.ToString() == Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Null)
            {
                return (BooleanValue.Of(Value == "null").SetContext(Context), null);
            }
            return base.GetComparisonEq(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            var eqRes = GetComparisonEq(other).Item1;
            if (eqRes != null && eqRes.Type == RuntimeValueType.Boolean)
            {
                var b = (BooleanValue)eqRes;
                return (BooleanValue.Of(!b.Value).SetContext(Context), null);
            }
            return base.GetComparisonNe(other);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other)
        {
            if (other.Type != RuntimeValueType.String)
            {
                return (BooleanValue.Of(false).SetContext(Context), null);
            }

            StringValue s = (StringValue)other;
            return (BooleanValue.Of(s.Value == Value).SetContext(Context), null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other)
        {
            if (other.Type != RuntimeValueType.String)
            {
                return (BooleanValue.Of(true).SetContext(Context), null);
            }

            StringValue s = (StringValue)other;
            return (BooleanValue.Of(s.Value != Value).SetContext(Context), null);
        }

        public sealed override string ToString() => Value;
        public string ToRepr() => $"\"{Value}\"";
    }
}
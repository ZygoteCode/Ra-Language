using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class SetValue : RuntimeValue
    {
        public HashSet<RuntimeValue> Elements { get; }
        public sealed override bool IsCopy => false;

        public SetValue(HashSet<RuntimeValue> elements)
        {
            Elements = elements ?? new HashSet<RuntimeValue>();
        }

        public sealed override RuntimeValueType Type => RuntimeValueType.Set;

        private RuntimeValue? FindEqual(RuntimeValue v)
        {
            foreach (var el in Elements)
            {
                if (el.Equals(v)) return el;
            }
            return null;
        }

        public bool ContainsEqual(RuntimeValue v) => FindEqual(v) != null;

        private void RemoveEqual(RuntimeValue v)
        {
            var existing = FindEqual(v);
            if (existing != null) Elements.Remove(existing);
        }

        private HashSet<RuntimeValue> DeepCopySet()
        {
            var s = new HashSet<RuntimeValue>();
            foreach (var e in Elements) s.Add(e.Copy());
            return s;
        }

        public sealed override ValueResult AddedTo(RuntimeValue other)
        {
            var newSet = (SetValue) new SetValue(DeepCopySet()).SetPos(PositionStart, PositionEnd).SetContext(Context);
            if (other.Type == RuntimeValueType.Set)
            {
                var os = (SetValue)other;
                foreach (var o in os.Elements)
                {
                    if (!newSet.ContainsEqual(o)) newSet.Elements.Add(o.Copy());
                }
                return (newSet, null);
            }
            if (!newSet.ContainsEqual(other))
            {
                newSet.Elements.Add(other);
            }
            return (newSet, null);
        }

        public sealed override ValueResult SubbedBy(RuntimeValue other)
        {
            var newSet = (SetValue) new SetValue(DeepCopySet()).SetPos(PositionStart, PositionEnd).SetContext(Context);

            if (other.Type == RuntimeValueType.Set)
            {
                var os = (SetValue)other;
                foreach (var o in os.Elements) newSet.RemoveEqual(o);
                return (newSet, null);
            }

            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                foreach (var o in l.Elements) newSet.RemoveEqual(o);
                return (newSet, null);
            }

            newSet.RemoveEqual(other);
            return (newSet, null);
        }

        public sealed override ValueResult MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Set || other.Type == RuntimeValueType.List)
            {
                IEnumerable<RuntimeValue> rightElements = other.Type == RuntimeValueType.Set
                    ? ((SetValue)other).Elements
                    : ((ListValue)other).Elements;

                var outSet = new HashSet<RuntimeValue>();
                foreach (var a in Elements)
                {
                    foreach (var b in rightElements)
                    {
                        var pairList = new List<RuntimeValue> { a.Copy(), b.Copy() };
                        var pair = new ListValue(pairList).SetContext(Context);
                        outSet.Add(pair);
                    }
                }
                return (new SetValue(outSet).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }

            return base.MultedBy(other);
        }

        public sealed override ValueResult DivedBy(RuntimeValue other)
        {
            try
            {
                if (other.Type == RuntimeValueType.Number)
                {
                    int idx = (int)((NumberValue)other).Value;
                    var ordered = Elements.ToList();
                    if (idx < 0) idx = ordered.Count + idx;
                    if (idx < 0 || idx >= ordered.Count)
                        return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    return (ordered[idx], null);
                }
            }
            catch { }
            return base.DivedBy(other);
        }

        public sealed override ValueResult PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Set)
            {
                var result = new HashSet<RuntimeValue>();
                foreach (var a in Elements)
                    if (!((SetValue)other).Elements.Any(b => a.Equals(b)))
                        result.Add(a.Copy());
                foreach (var b in ((SetValue)other).Elements)
                    if (!Elements.Any(a => a.Equals(b)))
                        result.Add(b.Copy());
                return (new SetValue(result).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Number)
            {
                int k = (int)((NumberValue)other).Value;
                var items = Elements.ToList();
                var n = items.Count;
                if (k < 0 || k > n) return (new SetValue(new HashSet<RuntimeValue>()).SetPos(PositionStart, PositionEnd).SetContext(Context), null);

                var combos = new HashSet<RuntimeValue>();
                void Recurse(int start, List<RuntimeValue> acc)
                {
                    if (acc.Count == k)
                    {
                        var comboSet = new HashSet<RuntimeValue>();
                        foreach (var el in acc) comboSet.Add(el.Copy());
                        combos.Add(new SetValue(comboSet).SetContext(Context));
                        return;
                    }
                    for (int i = start; i < n; i++)
                    {
                        acc.Add(items[i]);
                        Recurse(i + 1, acc);
                        acc.RemoveAt(acc.Count - 1);
                    }
                }
                Recurse(0, new List<RuntimeValue>());
                return (new SetValue(combos).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.PowedBy(other);
        }

        public sealed override ValueResult ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int idx = (int)((NumberValue)other).Value;
                var ordered = Elements.ToList();
                if (ordered.Count == 0) return (null, IllegalOperation(other));
                int wrapped = ((idx % ordered.Count) + ordered.Count) % ordered.Count;
                return (ordered[wrapped], null);
            }
            if (ContainsEqual(other)) return (BooleanValue.Of(true).SetContext(Context), null);
            return (BooleanValue.Of(false).SetContext(Context), null);
        }

        public sealed override ValueResult BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = (int)((NumberValue)other).Value;
                if (n < 0) return (null, IllegalOperation(other));
                var ordered = Elements.ToList();
                if (n >= ordered.Count) return (new SetValue(new HashSet<RuntimeValue>()).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
                var newSet = new HashSet<RuntimeValue>();
                for (int i = n; i < ordered.Count; i++) newSet.Add(ordered[i].Copy());
                return (new SetValue(newSet).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseLeftShiftedBy(other);
        }

        public sealed override ValueResult BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = (int)((NumberValue)other).Value;
                if (n < 0) return (null, IllegalOperation(other));
                var ordered = Elements.ToList();
                if (n >= ordered.Count) return (new SetValue(new HashSet<RuntimeValue>()).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
                var newSet = new HashSet<RuntimeValue>();
                for (int i = 0; i < ordered.Count - n; i++) newSet.Add(ordered[i].Copy());
                return (new SetValue(newSet).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseRightShiftedBy(other);
        }

        public sealed override ValueResult BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Set)
            {
                var right = (SetValue)other;
                var outSet = new HashSet<RuntimeValue>();
                foreach (var a in Elements)
                {
                    if (right.Elements.Any(b => a.Equals(b))) outSet.Add(a.Copy());
                }
                return (new SetValue(outSet).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            if (other.Type == RuntimeValueType.List)
            {
                var list = (ListValue)other;
                var outSet = new HashSet<RuntimeValue>();
                foreach (var a in Elements)
                {
                    if (list.Elements.Any(b => a.Equals(b))) outSet.Add(a.Copy());
                }
                return (new SetValue(outSet).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseAndedBy(other);
        }

        public sealed override ValueResult BitwiseOredBy(RuntimeValue other)
        {
            var outSet = new HashSet<RuntimeValue>();
            foreach (var a in Elements) outSet.Add(a.Copy());

            if (other.Type == RuntimeValueType.Set)
            {
                foreach (var b in ((SetValue)other).Elements)
                {
                    if (!outSet.Any(x => x.Equals(b))) outSet.Add(b.Copy());
                }
                return (new SetValue(outSet).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            if (other.Type == RuntimeValueType.List)
            {
                foreach (var b in ((ListValue)other).Elements)
                {
                    if (!outSet.Any(x => x.Equals(b))) outSet.Add(b.Copy());
                }
                return (new SetValue(outSet).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            if (!outSet.Any(x => x.Equals(other))) outSet.Add(other.Copy());
            return (new SetValue(outSet).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
        }

        private bool SetEqualsByComparison(SetValue other, TokenType token)
        {
            if (other.Elements.Count != Elements.Count) return false;
            foreach (var a in Elements)
            {
                bool matched = false;
                foreach (var b in other.Elements)
                {
                    RuntimeValue? cmp = token switch
                    {
                        TokenType.EE => a.GetComparisonEq(b).Item1,
                        TokenType.NE => a.GetComparisonNe(b).Item1,
                        TokenType.STRICT_EE => a.GetComparisonStrictEq(b).Item1,
                        TokenType.STRICT_NE => a.GetComparisonStrictNe(b).Item1,
                        _ => null
                    };
                    if (cmp != null && cmp.IsTrue()) { matched = true; break; }
                }
                if (!matched) return false;
            }
            return true;
        }

        private ValueResult EvaluateComparison(RuntimeValue other, TokenType token)
        {
            if (other.Type == RuntimeValueType.Set)
            {
                var sv = (SetValue)other;
                bool eq = SetEqualsByComparison(sv, token);
                return (BooleanValue.Of(eq).SetContext(Context), null);
            }
            if ((other.Type == RuntimeValueType.List || other.Type == RuntimeValueType.Set) &&
                (other.Type == RuntimeValueType.List ? ((ListValue)other).Elements.Count == 1 : ((SetValue)other).Elements.Count == 1))
            {
                RuntimeValue single = other.Type == RuntimeValueType.List ? ((ListValue)other).Elements[0] : ((SetValue)other).Elements.ToList()[0];

                foreach (var a in Elements)
                {
                    var cmp = token switch
                    {
                        TokenType.EE => a.GetComparisonEq(single).Item1,
                        TokenType.NE => a.GetComparisonNe(single).Item1,
                        TokenType.STRICT_EE => a.GetComparisonStrictEq(single).Item1,
                        TokenType.STRICT_NE => a.GetComparisonStrictNe(single).Item1,
                        _ => null
                    };
                    if (cmp == null) return (BooleanValue.Of(false).SetContext(Context), null);
                    if (cmp.IsTrue()) return (BooleanValue.Of(true).SetContext(Context), null);
                }
                return (BooleanValue.Of(false).SetContext(Context), null);
            }

            if ((other.Type == RuntimeValueType.Number || other.Type == RuntimeValueType.String ||
                 other.Type == RuntimeValueType.Boolean || other.Type == RuntimeValueType.Null) && Elements.Count == 1)
            {
                var single = Elements.First();
                var cmp = token switch
                {
                    TokenType.EE => single.GetComparisonEq(other).Item1,
                    TokenType.NE => single.GetComparisonNe(other).Item1,
                    TokenType.STRICT_EE => single.GetComparisonStrictEq(other).Item1,
                    TokenType.STRICT_NE => single.GetComparisonStrictNe(other).Item1,
                    _ => null
                };
                if (cmp == null) return (BooleanValue.Of(false).SetContext(Context), null);
                return (BooleanValue.Of(cmp.IsTrue()).SetContext(Context), null);
            }

            return (BooleanValue.Of(false).SetContext(Context), null);
        }

        public sealed override ValueResult GetComparisonEq(RuntimeValue other) => EvaluateComparison(other, TokenType.EE);
        public sealed override ValueResult GetComparisonNe(RuntimeValue other) => EvaluateComparison(other, TokenType.NE);
        public sealed override ValueResult GetComparisonStrictEq(RuntimeValue other) => EvaluateComparison(other, TokenType.STRICT_EE);
        public sealed override ValueResult GetComparisonStrictNe(RuntimeValue other) => EvaluateComparison(other, TokenType.STRICT_NE);

        public sealed override ValueResult BitwiseNotted()
        {
            return (this, null);
        }

        public sealed override ValueResult ListAccess(RuntimeValue other)
        {
            try
            {
                if (other.Type == RuntimeValueType.Number)
                {
                    int index = (int)((NumberValue)other).Value;
                    var ordered = Elements.ToList();
                    if (index < 0) index = ordered.Count + index;
                    if (index < 0 || index >= ordered.Count)
                    {
                        return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    }
                    return (ordered[index], null);
                }
            }
            catch { }
            return base.ListAccess(other);
        }

        public sealed override ValueResult ListSet(RuntimeValue indexValue, RuntimeValue value)
        {
            if (indexValue.Type == RuntimeValueType.Number)
            {
                int index = (int)((NumberValue)indexValue).Value;
                var ordered = Elements.ToList();
                if (index < 0) index = ordered.Count + index;
                if (index < 0 || index >= ordered.Count)
                    return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, "Index out of bounds", Context));
                var toReplace = ordered[index];
                Elements.Remove(toReplace);
                Elements.Add(value);
                return (value.SetContext(Context), null);
            }
            return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, "Index must be a number", Context));
        }

        public sealed override RuntimeValue Copy()
        {
            return new SetValue(DeepCopySet()).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public sealed override bool IsTrue() => Elements.Count > 0;

        public sealed override string ToString()
        {
            return "{" + string.Join(", ", Elements.Select(e => e is StringValue s ? s.ToRepr() : e.ToString())) + "}";
        }
    }
}
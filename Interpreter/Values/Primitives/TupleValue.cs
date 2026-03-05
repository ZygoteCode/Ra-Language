using RaLanguage.Errors;
using RaLanguage.Errors.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class TupleValue : RuntimeValue
    {
        public List<RuntimeValue> Elements { get; }

        public TupleValue(List<RuntimeValue> elements)
        {
            Elements = elements ?? new List<RuntimeValue>();
        }

        public override RuntimeValueType Type => RuntimeValueType.Tuple;

        private int ToIntSafe(RuntimeValue v)
        {
            if (v.Type != RuntimeValueType.Number) throw new InvalidOperationException("Expected number");
            return (int)((NumberValue)v).Value;
        }

        private List<RuntimeValue> DeepCopyElements()
        {
            return Elements.Select(e => e.Copy()).ToList();
        }

        private TupleValue NewTupleFrom(List<RuntimeValue> elems)
        {
            return (TupleValue) new TupleValue(elems).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                var newElems = DeepCopyElements();
                newElems.AddRange(t.Elements.Select(e => e.Copy()));
                return (NewTupleFrom(newElems), null);
            }
            else if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                var newElems = DeepCopyElements();
                newElems.AddRange(l.Elements.Select(e => e.Copy()));
                return (NewTupleFrom(newElems), null);
            }
            else
            {
                var newElems = DeepCopyElements();
                newElems.Add(other.Copy());
                return (NewTupleFrom(newElems), null);
            }
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                try
                {
                    int idx = ToIntSafe(other);
                    var copy = DeepCopyElements();
                    if (idx < 0) idx = copy.Count + idx;
                    if (idx < 0 || idx >= copy.Count) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    copy.RemoveAt(idx);
                    return (NewTupleFrom(copy), null);
                }
                catch
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Invalid index", Context));
                }
            }
            else
            {
                var newList = DeepCopyElements().Where(e =>
                {
                    var cmp = e.GetComparisonEq(other).Item1;
                    return !(cmp != null && cmp.IsTrue());
                }).ToList();
                return (NewTupleFrom(newList), null);
            }
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int times = ToIntSafe(other);
                if (times < 0) return (null, IllegalOperation(other));
                var result = new List<RuntimeValue>();
                for (int i = 0; i < times; i++) result.AddRange(DeepCopyElements().Select(e => e.Copy()));
                return (NewTupleFrom(result), null);
            }
            else if (other.Type == RuntimeValueType.Tuple)
            {
                return AddedTo(other);
            }
            else if (other.Type == RuntimeValueType.List)
            {
                return AddedTo(other);
            }
            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                try
                {
                    int idx = ToIntSafe(other);
                    int i = idx;
                    if (i < 0) i = Elements.Count + i;
                    if (i < 0 || i >= Elements.Count) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    return (Elements[i], null);
                }
                catch
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Invalid index", Context));
                }
            }
            else if (other.Type == RuntimeValueType.List || other.Type == RuntimeValueType.Set)
            {
                var indices = new List<RuntimeValue>();
                if (other.Type == RuntimeValueType.List) indices = ((ListValue)other).Elements;
                else indices = ((SetValue)other).Elements.ToList();

                var outElems = new List<RuntimeValue>();
                for (int i = 0; i < indices.Count; i++)
                {
                    var v = indices[i];
                    if (v.Type != RuntimeValueType.Number) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, $"Value at index {i} should be a number", Context));
                    int idx = (int)((NumberValue)v).Value;
                    if (idx < 0) idx = Elements.Count + idx;
                    if (idx < 0 || idx >= Elements.Count) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    outElems.Add(Elements[idx]);
                }
                return (new ListValue(outElems).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                var outList = new List<RuntimeValue>();
                foreach (var e in Elements)
                {
                    if (e.Type == RuntimeValueType.Number)
                    {
                        double res = Math.Pow((double)((NumberValue)e).Value, (double)n.Value);
                        outList.Add(new NumberValue((BigNumber)res).SetContext(Context));
                    }
                    else outList.Add(e.Copy());
                }
                return (NewTupleFrom(outList), null);
            }
            else if (other.Type == RuntimeValueType.Tuple || other.Type == RuntimeValueType.List)
            {
                var right = other.Type == RuntimeValueType.Tuple ? ((TupleValue)other).Elements : ((ListValue)other).Elements;
                if (right.Count != Elements.Count && right.Count != 1) return (null, IllegalOperation(other));
                var outList = new List<RuntimeValue>();
                for (int i = 0; i < Elements.Count; i++)
                {
                    var a = Elements[i];
                    var b = right.Count == 1 ? right[0] : right[i];
                    if (a.Type == RuntimeValueType.Number && b.Type == RuntimeValueType.Number)
                    {
                        double res = Math.Pow((double)((NumberValue)a).Value, (double)((NumberValue)b).Value);
                        outList.Add(new NumberValue((BigNumber)res).SetContext(Context));
                    }
                    else return (null, IllegalOperation(other));
                }
                return (NewTupleFrom(outList), null);
            }
            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int idx = ToIntSafe(other);
                if (Elements.Count == 0) return (null, IllegalOperation(other));
                int wrapped = ((idx % Elements.Count) + Elements.Count) % Elements.Count;
                return (Elements[wrapped], null);
            }
            var cmp = Elements.Any(e =>
            {
                var r = e.GetComparisonEq(other).Item1;
                return r != null && r.IsTrue();
            });
            return (new BooleanValue(cmp).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = ToIntSafe(other);
                if (n < 0) return (null, IllegalOperation(other));
                if (n >= Elements.Count) return (NewTupleFrom(new List<RuntimeValue>()), null);
                var newElems = DeepCopyElements().Skip(n).ToList();
                return (NewTupleFrom(newElems), null);
            }
            return base.BitwiseLeftShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = ToIntSafe(other);
                if (n < 0) return (null, IllegalOperation(other));
                if (n >= Elements.Count) return (NewTupleFrom(new List<RuntimeValue>()), null);
                var newElems = DeepCopyElements().Take(Elements.Count - n).ToList();
                return (NewTupleFrom(newElems), null);
            }
            return base.BitwiseRightShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                if (t.Elements.Count == Elements.Count && Elements.All(x => x.Type == RuntimeValueType.Number) && t.Elements.All(x => x.Type == RuntimeValueType.Number))
                {
                    var outList = new List<RuntimeValue>();
                    for (int i = 0; i < Elements.Count; i++)
                    {
                        int a = (int)((NumberValue)Elements[i]).Value;
                        int b = (int)((NumberValue)t.Elements[i]).Value;
                        outList.Add(new NumberValue((BigNumber)(a & b)).SetContext(Context));
                    }
                    return (NewTupleFrom(outList), null);
                }
            }

            if (other.Type == RuntimeValueType.Tuple || other.Type == RuntimeValueType.List || other.Type == RuntimeValueType.Set)
            {
                IEnumerable<RuntimeValue> rightElems = other.Type == RuntimeValueType.Tuple ? ((TupleValue)other).Elements
                    : other.Type == RuntimeValueType.List ? ((ListValue)other).Elements : ((SetValue)other).Elements.ToList();

                var outList = new List<RuntimeValue>();
                var usedRight = new bool[rightElems.Count()];
                var rightArray = rightElems.ToArray();
                for (int i = 0; i < Elements.Count; i++)
                {
                    for (int j = 0; j < rightArray.Length; j++)
                    {
                        if (usedRight[j]) continue;
                        var cmp = Elements[i].GetComparisonEq(rightArray[j]).Item1;
                        if (cmp != null && cmp.IsTrue())
                        {
                            outList.Add(Elements[i].Copy());
                            usedRight[j] = true;
                            break;
                        }
                    }
                }
                return (NewTupleFrom(outList), null);
            }

            return base.BitwiseAndedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                if (t.Elements.Count == Elements.Count && Elements.All(x => x.Type == RuntimeValueType.Number) && t.Elements.All(x => x.Type == RuntimeValueType.Number))
                {
                    var outList = new List<RuntimeValue>();
                    for (int i = 0; i < Elements.Count; i++)
                    {
                        int a = (int)((NumberValue)Elements[i]).Value;
                        int b = (int)((NumberValue)t.Elements[i]).Value;
                        outList.Add(new NumberValue((BigNumber)(a | b)).SetContext(Context));
                    }
                    return (NewTupleFrom(outList), null);
                }
            }

            if (other.Type == RuntimeValueType.Tuple || other.Type == RuntimeValueType.List || other.Type == RuntimeValueType.Set)
            {
                IEnumerable<RuntimeValue> rightElems = other.Type == RuntimeValueType.Tuple ? ((TupleValue)other).Elements
                    : other.Type == RuntimeValueType.List ? ((ListValue)other).Elements : ((SetValue)other).Elements.ToList();

                var outList = DeepCopyElements();
                foreach (var r in rightElems)
                {
                    bool found = outList.Any(x =>
                    {
                        var cmp = x.GetComparisonEq(r).Item1;
                        return cmp != null && cmp.IsTrue();
                    });
                    if (!found) outList.Add(r.Copy());
                }
                return (NewTupleFrom(outList), null);
            }

            return base.BitwiseOredBy(other);
        }

        public override (RuntimeValue?, Error?) ListAccess(RuntimeValue other)
        {
            try
            {
                if (other.Type == RuntimeValueType.Number)
                {
                    NumberValue n = (NumberValue)other;
                    int index = (int)n.Value;
                    if (index < 0) index = Elements.Count + index;
                    if (index < 0 || index >= Elements.Count) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    return (Elements[index], null);
                }
                else if (other.Type == RuntimeValueType.List || other.Type == RuntimeValueType.Set)
                {
                    return DivedBy(other);
                }
            }
            catch { }

            return base.ListAccess(other);
        }

        public override (RuntimeValue?, Error?) ListSet(RuntimeValue indexValue, RuntimeValue value)
        {
            return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, "Tuples are immutable; cannot assign", Context));
        }

        private int LexicographicCompareTo(TupleValue other)
        {
            int n = Math.Min(this.Elements.Count, other.Elements.Count);
            for (int i = 0; i < n; i++)
            {
                var a = this.Elements[i];
                var b = other.Elements[i];

                var eq = a.GetComparisonEq(b).Item1;
                if (eq != null && eq.Type == RuntimeValueType.Boolean && eq.IsTrue()) continue;

                var lt = a.GetComparisonLt(b).Item1;
                if (lt != null && lt.Type == RuntimeValueType.Boolean && lt.IsTrue()) return -1;

                var gt = a.GetComparisonGt(b).Item1;
                if (gt != null && gt.Type == RuntimeValueType.Boolean && gt.IsTrue()) return 1;

                int cmp = String.CompareOrdinal(a.ToString(), b.ToString());
                if (cmp != 0) return cmp;
            }

            if (this.Elements.Count < other.Elements.Count) return -1;
            if (this.Elements.Count > other.Elements.Count) return 1;
            return 0;
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                if (t.Elements.Count != Elements.Count) return (new BooleanValue(false).SetContext(Context), null);
                for (int i = 0; i < Elements.Count; i++)
                {
                    var cmp = Elements[i].GetComparisonEq(t.Elements[i]).Item1;
                    if (cmp == null || !cmp.IsTrue()) return (new BooleanValue(false).SetContext(Context), null);
                }
                return (new BooleanValue(true).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            var eq = GetComparisonEq(other).Item1;
            if (eq == null) return (new BooleanValue(false).SetContext(Context), null);
            return (new BooleanValue(!eq.IsTrue()).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other)
        {
            return GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other)
        {
            var eq = GetComparisonStrictEq(other).Item1;
            if (eq == null) return (new BooleanValue(false).SetContext(Context), null);
            return (new BooleanValue(!eq.IsTrue()).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                int cmp = LexicographicCompareTo(t);
                return (new BooleanValue(cmp < 0).SetContext(Context), null);
            }
            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                int cmp = LexicographicCompareTo(t);
                return (new BooleanValue(cmp > 0).SetContext(Context), null);
            }
            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                int cmp = LexicographicCompareTo(t);
                return (new BooleanValue(cmp <= 0).SetContext(Context), null);
            }
            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                int cmp = LexicographicCompareTo(t);
                return (new BooleanValue(cmp >= 0).SetContext(Context), null);
            }
            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) AndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                if (t.Elements.Count != Elements.Count) return base.AndedBy(other);
                var outList = new List<RuntimeValue>();
                for (int i = 0; i < Elements.Count; i++)
                {
                    var res = Elements[i].AndedBy(t.Elements[i]).Item1;
                    if (res == null) return (null, IllegalOperation(other));
                    outList.Add(res);
                }
                return (NewTupleFrom(outList), null);
            }
            if (other.Type == RuntimeValueType.Boolean || other.Type == RuntimeValueType.Number)
            {
                var outList = Elements.Select(e => e.AndedBy(other).Item1 ?? (RuntimeValue)new BooleanValue(false)).ToList();
                return (NewTupleFrom(outList), null);
            }
            return base.AndedBy(other);
        }

        public override (RuntimeValue?, Error?) OredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Tuple)
            {
                var t = (TupleValue)other;
                if (t.Elements.Count != Elements.Count) return base.OredBy(other);
                var outList = new List<RuntimeValue>();
                for (int i = 0; i < Elements.Count; i++)
                {
                    var res = Elements[i].OredBy(t.Elements[i]).Item1;
                    if (res == null) return (null, IllegalOperation(other));
                    outList.Add(res);
                }
                return (NewTupleFrom(outList), null);
            }
            if (other.Type == RuntimeValueType.Boolean || other.Type == RuntimeValueType.Number)
            {
                var outList = Elements.Select(e => e.OredBy(other).Item1 ?? (RuntimeValue)new BooleanValue(false)).ToList();
                return (NewTupleFrom(outList), null);
            }
            return base.OredBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            var rev = DeepCopyElements();
            rev.Reverse();
            return (NewTupleFrom(rev), null);
        }

        public override RuntimeValue Copy()
        {
            return NewTupleFrom(DeepCopyElements());
        }

        public override bool IsTrue()
        {
            foreach (var v in Elements)
            {
                if (!v.IsTrue()) return false;
            }
            return true;
        }

        public override string ToString()
        {
            return "(" + string.Join(", ", Elements.Select(e => e is StringValue s ? s.ToRepr() : e.ToString())) + ")";
        }
    }
}
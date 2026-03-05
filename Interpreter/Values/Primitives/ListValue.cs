using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ListValue : RuntimeValue
    {
        public List<RuntimeValue> Elements { get; set;  }
        public ListValue(List<RuntimeValue> elements) { Elements = elements; }
        public override RuntimeValueType Type => RuntimeValueType.List;

        private List<RuntimeValue> DeepCopyElements()
        {
            return Elements.Select(e => e.Copy()).ToList();
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            var newList = (ListValue)Copy();
            newList.Elements.Add(other);
            return (newList.SetPos(PositionStart, PositionEnd).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                var newList = (ListValue) new ListValue(DeepCopyElements()).SetPos(PositionStart, PositionEnd).SetContext(Context);
                try
                {
                    int idx = (int)n.Value;
                    if (idx < 0) idx = newList.Elements.Count + idx;
                    if (idx < 0 || idx >= newList.Elements.Count) throw new IndexOutOfRangeException();
                    newList.Elements.RemoveAt(idx);
                    return (newList, null);
                }
                catch
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Element at this index could not be removed from list because index is out of bounds", Context));
                }
            }
            else if (other.Type == RuntimeValueType.List)
            {
                var rem = (ListValue)other;
                var newList = (ListValue) new ListValue(DeepCopyElements()).SetPos(PositionStart, PositionEnd).SetContext(Context);
                newList.Elements = newList.Elements.Where(e =>
                    !rem.Elements.Any(r => {
                        var cmp = e.GetComparisonEq(r).Item1;
                        return cmp != null && cmp.IsTrue();
                    })
                ).ToList();
                return (newList, null);
            }
            else if (other.Type == RuntimeValueType.Set)
            {
                SetValue s = (SetValue)other;
                var newList = (ListValue) new ListValue(DeepCopyElements()).SetPos(PositionStart, PositionEnd).SetContext(Context);
                newList.Elements = newList.Elements.Where(e =>
                    !s.Elements.Any(se => {
                        var cmp = e.GetComparisonEq(se).Item1;
                        return cmp != null && cmp.IsTrue();
                    })
                ).ToList();
                return (newList, null);
            }

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.List)
            {
                ListValue l = (ListValue)other;
                var newList = new ListValue(DeepCopyElements());
                newList.Elements.AddRange(l.Elements.Select(e => e.Copy()));
                return (newList.SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                int times = (int)n.Value;
                if (times < 0) return (null, IllegalOperation(other));
                var result = new List<RuntimeValue>();
                for (int i = 0; i < times; i++) result.AddRange(Elements.Select(e => e.Copy()));
                return (new ListValue(result).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                try
                {
                    int idx = (int)n.Value;
                    if (idx < 0) idx = Elements.Count + idx;
                    if (idx < 0 || idx >= Elements.Count) throw new IndexOutOfRangeException();
                    return (Elements[idx], null);
                }
                catch
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Element at this index could not be retrieved from list because index is out of bounds", Context));
                }
            }
            else if (other.Type == RuntimeValueType.List || other.Type == RuntimeValueType.Set)
            {
                List<RuntimeValue> actualIndices = new List<RuntimeValue>();
                if (other.Type == RuntimeValueType.List) actualIndices = ((ListValue)other).Elements;
                else actualIndices = ((SetValue)other).Elements.ToList();

                var outElements = new List<RuntimeValue>();
                for (int i = 0; i < actualIndices.Count; i++)
                {
                    var v = actualIndices[i];
                    if (v.Type != RuntimeValueType.Number)
                    {
                        return (null, new RuntimeError(other.PositionStart, other.PositionEnd, $"Value at index {i} should be a number", Context));
                    }
                    int idx = (int)((NumberValue)v).Value;
                    if (idx < 0) idx = Elements.Count + idx;
                    if (idx < 0 || idx >= Elements.Count)
                    {
                        return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    }
                    outElements.Add(Elements[idx]);
                }
                return (new ListValue(outElements).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }

            return base.DivedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                var outList = Elements.Select(e =>
                {
                    if (e.Type == RuntimeValueType.Number)
                    {
                        var en = (NumberValue)e;
                        double res = Math.Pow((double)en.Value, (double)n.Value);
                        return (RuntimeValue)new NumberValue((BigNumber)res).SetContext(Context);
                    }
                    return e.Copy();
                }).ToList();
                return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                if (l.Elements.Count != Elements.Count && l.Elements.Count != 1) return (null, IllegalOperation(other));
                var outList = new List<RuntimeValue>();
                for (int i = 0; i < Elements.Count; i++)
                {
                    var a = Elements[i];
                    var b = l.Elements.Count == 1 ? l.Elements[0] : l.Elements[i];
                    if (a.Type == RuntimeValueType.Number && b.Type == RuntimeValueType.Number)
                    {
                        double res = Math.Pow((double)((NumberValue)a).Value, (double)((NumberValue)b).Value);
                        outList.Add(new NumberValue((BigNumber)res).SetContext(Context));
                    }
                    else
                    {
                        return (null, IllegalOperation(other));
                    }
                }
                return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }

            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int idx = (int)((NumberValue)other).Value;
                if (Elements.Count == 0) return (null, IllegalOperation(other));
                int wrapped = ((idx % Elements.Count) + Elements.Count) % Elements.Count;
                return (Elements[wrapped], null);
            }
            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                var outList = new List<RuntimeValue>();
                foreach (var v in l.Elements)
                {
                    if (v.Type != RuntimeValueType.Number) return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index must be a number", Context));
                    int idx = (int)((NumberValue)v).Value;
                    if (idx < 0) idx = Elements.Count + idx;
                    if (Elements.Count == 0) return (null, IllegalOperation(other));
                    int wrapped = ((idx % Elements.Count) + Elements.Count) % Elements.Count;
                    outList.Add(Elements[wrapped]);
                }
                return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.ModuledBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = (int)((NumberValue)other).Value;
                if (n < 0) return (null, IllegalOperation(other));
                if (n >= Elements.Count) return (new ListValue(new List<RuntimeValue>()).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
                var newElems = DeepCopyElements().Skip(n).ToList();
                return (new ListValue(newElems).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseLeftShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = (int)((NumberValue)other).Value;
                if (n < 0) return (null, IllegalOperation(other));
                if (n >= Elements.Count) return (new ListValue(new List<RuntimeValue>()).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
                var newElems = DeepCopyElements().Take(Elements.Count - n).ToList();
                return (new ListValue(newElems).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseRightShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int rhs = (int)((NumberValue)other).Value;
                var outList = Elements.Select(e =>
                {
                    if (e.Type == RuntimeValueType.Number)
                    {
                        int val = (int)((NumberValue)e).Value;
                        return (RuntimeValue)new NumberValue((BigNumber)(val & rhs)).SetContext(Context);
                    }
                    return e.Copy();
                }).ToList();
                return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                if (Elements.Count == l.Elements.Count && Elements.All(x => x.Type == RuntimeValueType.Number)
                    && l.Elements.All(x => x.Type == RuntimeValueType.Number))
                {
                    var outList = new List<RuntimeValue>();
                    for (int i = 0; i < Elements.Count; i++)
                    {
                        int a = (int)((NumberValue)Elements[i]).Value;
                        int b = (int)((NumberValue)l.Elements[i]).Value;
                        outList.Add(new NumberValue((BigNumber)(a & b)).SetContext(Context));
                    }
                    return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
                }
                var out2 = new List<RuntimeValue>();
                var seen = new List<int>();
                for (int i = 0; i < Elements.Count; i++)
                {
                    var a = Elements[i];
                    for (int j = 0; j < l.Elements.Count; j++)
                    {
                        if (seen.Contains(j)) continue;
                        var cmp = a.GetComparisonEq(l.Elements[j]).Item1;
                        if (cmp != null && cmp.IsTrue())
                        {
                            out2.Add(a.Copy());
                            seen.Add(j);
                            break;
                        }
                    }
                }
                return (new ListValue(out2).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Set)
            {
                SetValue s = (SetValue)other;
                var out2 = new List<RuntimeValue>();
                foreach (var a in Elements)
                {
                    foreach (var se in s.Elements)
                    {
                        var cmp = a.GetComparisonEq(se).Item1;
                        if (cmp != null && cmp.IsTrue())
                        {
                            out2.Add(a.Copy());
                            break;
                        }
                    }
                }
                return (new ListValue(out2).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseAndedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int rhs = (int)((NumberValue)other).Value;
                var outList = Elements.Select(e =>
                {
                    if (e.Type == RuntimeValueType.Number)
                    {
                        int val = (int)((NumberValue)e).Value;
                        return (RuntimeValue)new NumberValue((BigNumber)(val | rhs)).SetContext(Context);
                    }
                    return e.Copy();
                }).ToList();
                return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                if (Elements.Count == l.Elements.Count && Elements.All(x => x.Type == RuntimeValueType.Number)
                    && l.Elements.All(x => x.Type == RuntimeValueType.Number))
                {
                    var outList = new List<RuntimeValue>();
                    for (int i = 0; i < Elements.Count; i++)
                    {
                        int a = (int)((NumberValue)Elements[i]).Value;
                        int b = (int)((NumberValue)l.Elements[i]).Value;
                        outList.Add(new NumberValue((BigNumber)(a | b)).SetContext(Context));
                    }
                    return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
                }
                var out2 = new List<RuntimeValue>();
                var seen = new List<RuntimeValue>();
                foreach (var a in Elements) { out2.Add(a.Copy()); seen.Add(a); }
                foreach (var b in l.Elements)
                {
                    bool found = false;
                    foreach (var s in seen)
                    {
                        var cmp = s.GetComparisonEq(b).Item1;
                        if (cmp != null && cmp.IsTrue()) { found = true; break; }
                    }
                    if (!found) out2.Add(b.Copy());
                }
                return (new ListValue(out2).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Set)
            {
                SetValue s = (SetValue)other;
                var out2 = new List<RuntimeValue>();
                var seen = new List<RuntimeValue>();
                foreach (var a in Elements) { out2.Add(a.Copy()); seen.Add(a); }
                foreach (var se in s.Elements)
                {
                    bool found = false;
                    foreach (var sseen in seen)
                    {
                        var cmp = sseen.GetComparisonEq(se).Item1;
                        if (cmp != null && cmp.IsTrue()) { found = true; break; }
                    }
                    if (!found) out2.Add(se.Copy());
                }
                return (new ListValue(out2).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.BitwiseOredBy(other);
        }

        public override (RuntimeValue?, Error?) ListSet(RuntimeValue indexValue, RuntimeValue value)
        {
            if (indexValue.Type == RuntimeValueType.Number)
            {
                NumberValue number = (NumberValue)indexValue;
                int index = (int)number.Value;
                if (index < 0) index = Elements.Count + index;
                if (index < 0 || index >= Elements.Count)
                {
                    return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, "Index out of bounds", Context));
                }
                Elements[index] = value;
                return (value.SetContext(Context), null);
            }
            else if (indexValue.Type == RuntimeValueType.List)
            {
                ListValue list = (ListValue)indexValue;
                for (int i = 0; i < list.Elements.Count; i++)
                {
                    RuntimeValue v = list.Elements[i];
                    if (v.Type != RuntimeValueType.Number)
                    {
                        return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, $"Value at index {i} should be a number", Context));
                    }
                    int index = (int)((NumberValue)v).Value;
                    if (index < 0) index = Elements.Count + index;
                    if (index < 0 || index >= Elements.Count)
                    {
                        return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, "Index out of bounds", Context));
                    }
                    Elements[index] = value;
                }
                return (this, null);
            }

            return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, "Index must be a number or a list", Context));
        }

        private int LexicographicCompareTo(ListValue other)
        {
            int n = Math.Min(this.Elements.Count, other.Elements.Count);
            for (int i = 0; i < n; i++)
            {
                var a = this.Elements[i];
                var b = other.Elements[i];

                var eq = a.GetComparisonEq(b).Item1;
                if (eq != null && eq.Type == RuntimeValueType.Boolean && eq.IsTrue()) continue;

                var lt = a.GetComparisonLt(b).Item1;
                if (lt != null && lt.Type == RuntimeValueType.Boolean)
                {
                    if (lt.IsTrue()) return -1;
                }
                var gt = a.GetComparisonGt(b).Item1;
                if (gt != null && gt.Type == RuntimeValueType.Boolean)
                {
                    if (gt.IsTrue()) return 1;
                }

                int cmp = String.CompareOrdinal(a.ToString(), b.ToString());
                if (cmp != 0) return cmp;
            }
            if (this.Elements.Count < other.Elements.Count) return -1;
            if (this.Elements.Count > other.Elements.Count) return 1;
            return 0;
        }

        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                int cmp = LexicographicCompareTo(l);
                return (new BooleanValue(cmp < 0).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.GetComparisonLt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                int cmp = LexicographicCompareTo(l);
                return (new BooleanValue(cmp > 0).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.GetComparisonGt(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                int cmp = LexicographicCompareTo(l);
                return (new BooleanValue(cmp <= 0).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.GetComparisonLte(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                int cmp = LexicographicCompareTo(l);
                return (new BooleanValue(cmp >= 0).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.GetComparisonGte(other);
        }

        public override (RuntimeValue?, Error?) AndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                if (l.Elements.Count != Elements.Count) return base.AndedBy(other);
                var outList = new List<RuntimeValue>();
                for (int i = 0; i < Elements.Count; i++)
                {
                    var res = Elements[i].AndedBy(l.Elements[i]).Item1;
                    if (res == null) return (null, IllegalOperation(other));
                    outList.Add(res);
                }
                return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            if (other.Type == RuntimeValueType.Boolean || other.Type == RuntimeValueType.Number)
            {
                var outList = Elements.Select(e => e.AndedBy(other).Item1 ?? (RuntimeValue)new BooleanValue(false)).ToList();
                return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.AndedBy(other);
        }

        public override (RuntimeValue?, Error?) OredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                if (l.Elements.Count != Elements.Count) return base.OredBy(other);
                var outList = new List<RuntimeValue>();
                for (int i = 0; i < Elements.Count; i++)
                {
                    var res = Elements[i].OredBy(l.Elements[i]).Item1;
                    if (res == null) return (null, IllegalOperation(other));
                    outList.Add(res);
                }
                return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            if (other.Type == RuntimeValueType.Boolean || other.Type == RuntimeValueType.Number)
            {
                var outList = Elements.Select(e => e.OredBy(other).Item1 ?? (RuntimeValue)new BooleanValue(false)).ToList();
                return (new ListValue(outList).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }
            return base.OredBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            Elements.Reverse();
            return (this, null);
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
                    if (index > Elements.Count - 1)
                    {
                        return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    }
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

        public override RuntimeValue Copy()
        {
            return new ListValue(DeepCopyElements()).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }


        private (RuntimeValue?, Error?) EvaluateComparison(RuntimeValue other, TokenType tokenType)
        {
            if (other.Type == RuntimeValueType.List)
            {
                ListValue l = (ListValue)other;
                int elementsCount = Elements.Count;

                if (l.Elements.Count != elementsCount)
                {
                    return (new BooleanValue(false).SetContext(Context), null);
                }

                for (var i = 0; i < elementsCount; i++)
                {
                    RuntimeValue v1 = Elements[i], v2 = l.Elements[i];
                    RuntimeValue? comparisonResult = null;

                    switch (tokenType)
                    {
                        case TokenType.EE: comparisonResult = v1.GetComparisonEq(v2).Item1; break;
                        case TokenType.NE: comparisonResult = v1.GetComparisonNe(v2).Item1; break;
                        case TokenType.STRICT_EE: comparisonResult = v1.GetComparisonStrictEq(v2).Item1; break;
                        case TokenType.STRICT_NE: comparisonResult = v1.GetComparisonStrictNe(v2).Item1; break;
                    }

                    if (comparisonResult == null)
                    {
                        return (new BooleanValue(false).SetContext(Context), null);
                    }

                    if (comparisonResult.Type != RuntimeValueType.Boolean)
                    {
                        return (new BooleanValue(false).SetContext(Context), null);
                    }

                    BooleanValue b = (BooleanValue)comparisonResult;

                    if (!b.Value)
                    {
                        return (new BooleanValue(false).SetContext(Context), null);
                    }
                }

                return (new BooleanValue(true).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Set)
            {
                SetValue s = (SetValue)other;
                int elementsCount = Elements.Count;

                if (s.Elements.Count != elementsCount)
                {
                    return (new BooleanValue(false).SetContext(Context), null);
                }

                List<RuntimeValue> theList = s.Elements.ToList();

                for (var i = 0; i < elementsCount; i++)
                {
                    RuntimeValue v1 = Elements[i], v2 = theList[i];
                    RuntimeValue? comparisonResult = null;

                    switch (tokenType)
                    {
                        case TokenType.EE: comparisonResult = v1.GetComparisonEq(v2).Item1; break;
                        case TokenType.NE: comparisonResult = v1.GetComparisonNe(v2).Item1; break;
                        case TokenType.STRICT_EE: comparisonResult = v1.GetComparisonStrictEq(v2).Item1; break;
                        case TokenType.STRICT_NE: comparisonResult = v1.GetComparisonStrictNe(v2).Item1; break;
                    }

                    if (comparisonResult == null)
                    {
                        return (new BooleanValue(false).SetContext(Context), null);
                    }

                    if (comparisonResult.Type != RuntimeValueType.Boolean)
                    {
                        return (new BooleanValue(false).SetContext(Context), null);
                    }

                    BooleanValue b = (BooleanValue)comparisonResult;

                    if (!b.Value)
                    {
                        return (new BooleanValue(false).SetContext(Context), null);
                    }
                }

                return (new BooleanValue(true).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Number
                && (tokenType == TokenType.EE || tokenType == TokenType.NE)
                && Elements.Count == 1)
            {
                NumberValue n = (NumberValue)other;
                RuntimeValue? comparisonResult = null;

                switch (tokenType)
                {
                    case TokenType.EE: comparisonResult = Elements[0].GetComparisonEq(n).Item1; break;
                    case TokenType.NE: comparisonResult = Elements[0].GetComparisonNe(n).Item1; break;
                }

                if (comparisonResult == null)
                {
                    return (new BooleanValue(false).SetContext(Context), null);
                }

                return (new BooleanValue(comparisonResult.IsTrue()).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String
                && (tokenType == TokenType.EE || tokenType == TokenType.NE)
                && Elements.Count == 1)
            {
                StringValue s = (StringValue)other;
                RuntimeValue? comparisonResult = null;

                switch (tokenType)
                {
                    case TokenType.EE: comparisonResult = Elements[0].GetComparisonEq(s).Item1; break;
                    case TokenType.NE: comparisonResult = Elements[0].GetComparisonNe(s).Item1; break;
                }

                if (comparisonResult == null)
                {
                    return (new BooleanValue(false).SetContext(Context), null);
                }

                return (new BooleanValue(comparisonResult.IsTrue()).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean
                && (tokenType == TokenType.EE || tokenType == TokenType.NE)
                && Elements.Count == 1)
            {
                BooleanValue b = (BooleanValue)other;
                RuntimeValue? comparisonResult = null;

                switch (tokenType)
                {
                    case TokenType.EE: comparisonResult = Elements[0].GetComparisonEq(b).Item1; break;
                    case TokenType.NE: comparisonResult = Elements[0].GetComparisonNe(b).Item1; break;
                }

                if (comparisonResult == null)
                {
                    return (new BooleanValue(false).SetContext(Context), null);
                }

                return (new BooleanValue(comparisonResult.IsTrue()).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Null
                && (tokenType == TokenType.EE || tokenType == TokenType.NE)
                && Elements.Count == 1)
            {
                NullValue n = (NullValue)other;
                RuntimeValue? comparisonResult = null;

                switch (tokenType)
                {
                    case TokenType.EE: comparisonResult = Elements[0].GetComparisonEq(n).Item1; break;
                    case TokenType.NE: comparisonResult = Elements[0].GetComparisonNe(n).Item1; break;
                }

                if (comparisonResult == null)
                {
                    return (new BooleanValue(false).SetContext(Context), null);
                }

                return (new BooleanValue(comparisonResult.IsTrue()).SetContext(Context), null);
            }

            return (new BooleanValue(false).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            return EvaluateComparison(other, TokenType.EE);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            return EvaluateComparison(other, TokenType.NE);
        }

        public override (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other)
        {
            return EvaluateComparison(other, TokenType.STRICT_EE);
        }

        public override (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other)
        {
            return EvaluateComparison(other, TokenType.STRICT_NE);
        }

        public override bool IsTrue()
        {
            foreach (RuntimeValue v in Elements)
            {
                if (!v.IsTrue()) return false;
            }
            return true;
        }

        public override string ToString() => "[" + string.Join(", ", Elements.Select(e => e is StringValue s ? s.ToRepr() : e.ToString())) + "]";
    }
}
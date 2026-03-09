using RaLanguage.Errors;
using RaLanguage.Errors.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class MapValue : RuntimeValue
    {
        public List<(RuntimeValue Key, RuntimeValue Value)> Pairs { get; }
        public override bool IsCopy => false;

        public MapValue()
        {
            Pairs = new List<(RuntimeValue, RuntimeValue)>();
        }

        public MapValue(List<(RuntimeValue, RuntimeValue)> pairs)
        {
            Pairs = pairs ?? new List<(RuntimeValue, RuntimeValue)>();
        }

        public override RuntimeValueType Type => RuntimeValueType.Map;

        private static int IndexOfKey(List<(RuntimeValue Key, RuntimeValue Value)> pairs, RuntimeValue keyToFind)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                var candidateKey = pairs[i].Key;

                var cmpTuple = candidateKey.GetComparisonStrictEq(keyToFind);
                var cmpVal = cmpTuple.Item1;
                var error = cmpTuple.Item2;
                if (error == null && cmpVal != null && cmpVal.IsTrue()) return i;

                if (candidateKey.Equals(keyToFind)) return i;
            }

            return -1;
        }

        private List<(RuntimeValue, RuntimeValue)> DeepCopyPairs()
        {
            var np = new List<(RuntimeValue, RuntimeValue)>();
            foreach (var (k, v) in Pairs)
            {
                np.Add((k.Copy(), v.Copy()));
            }
            return np;
        }

        private List<(RuntimeValue, RuntimeValue)> PairsToListDeterministic()
        {
            return Pairs;
        }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Map)
            {
                var otherMap = (MapValue)other;
                var newPairs = DeepCopyPairs();

                foreach (var (ok, ov) in otherMap.Pairs)
                {
                    int idx = IndexOfKey(newPairs, ok);
                    if (idx >= 0)
                        newPairs[idx] = (newPairs[idx].Item1, ov.Copy());
                    else
                        newPairs.Add((ok.Copy(), ov.Copy()));
                }

                var result = new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd);
                return (result, null);
            }

            if (other.Type == RuntimeValueType.List)
            {
                var lv = (ListValue)other;
                if (lv.Elements.Count == 2)
                {
                    var k = lv.Elements[0];
                    var v = lv.Elements[1];
                    var newPairs = DeepCopyPairs();
                    int idx = IndexOfKey(newPairs, k);
                    if (idx >= 0) newPairs[idx] = (newPairs[idx].Item1, v.Copy());
                    else newPairs.Add((k.Copy(), v.Copy()));
                    return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            var newPairs = DeepCopyPairs();

            if (other.Type == RuntimeValueType.Number || other.Type == RuntimeValueType.String || other.Type == RuntimeValueType.Boolean || other.Type == RuntimeValueType.Null)
            {
                int idx = IndexOfKey(newPairs, other);
                if (idx >= 0)
                {
                    newPairs.RemoveAt(idx);
                    return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
                return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Key not found in map", Context));
            }

            if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                foreach (var key in l.Elements)
                {
                    int idx = IndexOfKey(newPairs, key);
                    if (idx >= 0) newPairs.RemoveAt(idx);
                }
                return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Set)
            {
                var s = (SetValue)other;
                foreach (var key in s.Elements)
                {
                    int idx = IndexOfKey(newPairs, key);
                    if (idx >= 0) newPairs.RemoveAt(idx);
                }
                return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (other.Type == RuntimeValueType.Map)
            {
                var m = (MapValue)other;
                foreach (var (k, _) in m.Pairs)
                {
                    int idx = IndexOfKey(newPairs, k);
                    if (idx >= 0) newPairs.RemoveAt(idx);
                }
                return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) ListAccess(RuntimeValue other)
        {
            try
            {
                if (other.Type == RuntimeValueType.Number)
                {
                    int idx = (int)((NumberValue)other).Value;
                    var ordered = PairsToListDeterministic();
                    if (idx < 0) idx = ordered.Count + idx;
                    if (idx < 0 || idx >= ordered.Count)
                        return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    return (ordered[idx].Item2, null);
                }

                int keyIdx = IndexOfKey(Pairs, other);
                if (keyIdx >= 0) return (Pairs[keyIdx].Value, null);

                if (other.Type == RuntimeValueType.List)
                {
                    var l = (ListValue)other;
                    var newPairs = new List<(RuntimeValue, RuntimeValue)>();
                    foreach (var key in l.Elements)
                    {
                        int i = IndexOfKey(Pairs, key);
                        if (i >= 0) newPairs.Add((Pairs[i].Key.Copy(), Pairs[i].Value.Copy()));
                    }
                    return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
            }
            catch { }

            return base.ListAccess(other);
        }

        public override (RuntimeValue?, Error?) ListSet(RuntimeValue indexValue, RuntimeValue value)
        {
            try
            {
                int idx = IndexOfKey(Pairs, indexValue);
                if (idx >= 0)
                {
                    Pairs[idx] = (Pairs[idx].Key, value);
                    return (value.SetContext(Context), null);
                }
                else
                {
                    indexValue.SetContext(Context);
                    value.SetContext(Context);
                    Pairs.Add((indexValue, value));
                    return (value.SetContext(Context), null);
                }
            }
            catch (Exception)
            {
                return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, "Unable to set map value", Context));
            }
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Map)
            {
                var m = (MapValue)other;
                var newPairs = new List<(RuntimeValue, RuntimeValue)>();

                var allKeys = new List<RuntimeValue>(Pairs.Select(p => p.Key.Copy()));
                foreach (var (ok, _) in m.Pairs) if (!allKeys.Any(k => (IndexOfKey(Pairs, ok) >= 0) || k.Equals(ok))) allKeys.Add(ok.Copy());

                foreach (var k in allKeys)
                {
                    int i1 = IndexOfKey(Pairs, k);
                    int i2 = IndexOfKey(m.Pairs, k);
                    if (i1 >= 0 && i2 >= 0)
                    {
                        var v1 = Pairs[i1].Value;
                        var v2 = m.Pairs[i2].Value;
                        if (v1.Type == RuntimeValueType.Number && v2.Type == RuntimeValueType.Number)
                        {
                            double r = Math.Pow((double)((NumberValue)v1).Value, 0);
                            var val = (BigNumber)((double)((NumberValue)v1).Value * (double)((NumberValue)v2).Value);
                            newPairs.Add((k.Copy(), new NumberValue(val).SetContext(Context)));
                        }
                        else
                        {
                            newPairs.Add((k.Copy(), v1.Copy()));
                        }
                    }
                    else if (i1 >= 0)
                        newPairs.Add((Pairs[i1].Key.Copy(), Pairs[i1].Value.Copy()));
                    else if (i2 >= 0)
                        newPairs.Add((m.Pairs[i2].Key.Copy(), m.Pairs[i2].Value.Copy()));
                }
                return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            else if (other.Type == RuntimeValueType.List)
            {
                var l = (ListValue)other;
                var newPairs = new List<(RuntimeValue, RuntimeValue)>();
                foreach (var key in l.Elements)
                {
                    int idx = IndexOfKey(Pairs, key);
                    if (idx >= 0) newPairs.Add((Pairs[idx].Key.Copy(), Pairs[idx].Value.Copy()));
                }
                return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                var n = (NumberValue)other;
                var newPairs = new List<(RuntimeValue, RuntimeValue)>();
                foreach (var (k, v) in Pairs)
                {
                    if (v.Type == RuntimeValueType.Number)
                    {
                        double res = Math.Pow((double)((NumberValue)v).Value, (double)n.Value);
                        newPairs.Add((k.Copy(), new NumberValue((BigNumber)res).SetContext(Context)));
                    }
                    else
                    {
                        newPairs.Add((k.Copy(), v.Copy()));
                    }
                }
                return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            else if (other.Type == RuntimeValueType.Map)
            {
                var m = (MapValue)other;
                var result = new List<(RuntimeValue, RuntimeValue)>();
                foreach (var (k, v) in Pairs)
                {
                    if (IndexOfKey(m.Pairs, k) < 0) result.Add((k.Copy(), v.Copy()));
                }
                foreach (var (k, v) in m.Pairs)
                {
                    if (IndexOfKey(Pairs, k) < 0) result.Add((k.Copy(), v.Copy()));
                }
                return (new MapValue(result).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            return base.PowedBy(other);
        }

        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int idx = (int)((NumberValue)other).Value;
                var ordered = PairsToListDeterministic();
                if (ordered.Count == 0) return (null, IllegalOperation(other));
                int wrapped = ((idx % ordered.Count) + ordered.Count) % ordered.Count;
                return (ordered[wrapped].Item2, null);
            }

            int pos = IndexOfKey(Pairs, other);
            return (new BooleanValue(pos >= 0).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = (int)((NumberValue)other).Value;
                if (n < 0) return (null, IllegalOperation(other));
                var ordered = PairsToListDeterministic();
                if (n >= ordered.Count) return (new MapValue(new List<(RuntimeValue, RuntimeValue)>()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                var newPairs = ordered.Skip(n).Select(p => (p.Item1.Copy(), p.Item2.Copy())).ToList();
                return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            return base.BitwiseLeftShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                int n = (int)((NumberValue)other).Value;
                if (n < 0) return (null, IllegalOperation(other));
                var ordered = PairsToListDeterministic();
                if (n >= ordered.Count) return (new MapValue(new List<(RuntimeValue, RuntimeValue)>()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                var newPairs = ordered.Take(ordered.Count - n).Select(p => (p.Item1.Copy(), p.Item2.Copy())).ToList();
                return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            return base.BitwiseRightShiftedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Map)
            {
                var m = (MapValue)other;
                var outPairs = new List<(RuntimeValue, RuntimeValue)>();
                foreach (var (k, v) in Pairs)
                {
                    int idx = IndexOfKey(m.Pairs, k);
                    if (idx >= 0)
                    {
                        var rv = m.Pairs[idx].Value;
                        if (v.Type == RuntimeValueType.Number && rv.Type == RuntimeValueType.Number)
                        {
                            int a = (int)((NumberValue)v).Value;
                            int b = (int)((NumberValue)rv).Value;
                            outPairs.Add((k.Copy(), new NumberValue((BigNumber)(a & b)).SetContext(Context)));
                        }
                        else
                        {
                            outPairs.Add((k.Copy(), v.Copy()));
                        }
                    }
                }
                return (new MapValue(outPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseAndedBy(other);
        }

        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Map)
            {
                var m = (MapValue)other;
                var newPairs = DeepCopyPairs();

                foreach (var (ok, ov) in m.Pairs)
                {
                    int idx = IndexOfKey(newPairs, ok);
                    if (idx >= 0)
                    {
                        var leftVal = newPairs[idx].Item2;
                        if (leftVal.Type == RuntimeValueType.Number && ov.Type == RuntimeValueType.Number)
                        {
                            int a = (int)((NumberValue)leftVal).Value;
                            int b = (int)((NumberValue)ov).Value;
                            newPairs[idx] = (newPairs[idx].Item1, new NumberValue((BigNumber)(a | b)).SetContext(Context));
                        }
                        else
                        {
                            newPairs[idx] = (newPairs[idx].Item1, ov.Copy());
                        }
                    }
                    else
                    {
                        newPairs.Add((ok.Copy(), ov.Copy()));
                    }
                }
                return (new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            return base.BitwiseOredBy(other);
        }

        private (RuntimeValue?, Error?) EvaluateComparisonMap(RuntimeValue other, Func<RuntimeValue, RuntimeValue, (RuntimeValue?, Error?)> valueComparisonFunc)
        {
            if (other.Type != RuntimeValueType.Map)
                return (new BooleanValue(false).SetContext(Context), null);

            var oMap = (MapValue)other;

            if (Pairs.Count != oMap.Pairs.Count)
                return (new BooleanValue(false).SetContext(Context), null);

            foreach (var (k1, v1) in Pairs)
            {
                int idx = IndexOfKey(oMap.Pairs, k1);
                if (idx < 0)
                    return (new BooleanValue(false).SetContext(Context), null);

                var v2 = oMap.Pairs[idx].Value;
                var (comparisonResult, comparisonError) = valueComparisonFunc(v1, v2);
                if (comparisonError != null) return (null, comparisonError);
                if (comparisonResult == null) return (new BooleanValue(false).SetContext(Context), null);

                if (!comparisonResult.IsTrue())
                    return (new BooleanValue(false).SetContext(Context), null);
            }

            return (new BooleanValue(true).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other) => EvaluateComparisonMap(other, (a, b) => a.GetComparisonEq(b));
        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other) => EvaluateComparisonMap(other, (a, b) => a.GetComparisonNe(b));
        public override (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other) => EvaluateComparisonMap(other, (a, b) => a.GetComparisonStrictEq(b));
        public override (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other) => EvaluateComparisonMap(other, (a, b) => a.GetComparisonStrictNe(b));

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new BooleanValue(!IsTrue()).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            var inverted = new List<(RuntimeValue, RuntimeValue)>();
            foreach (var (k, v) in Pairs)
            {
                int existingIdx = IndexOfKey(inverted, v);
                if (existingIdx >= 0) inverted.RemoveAt(existingIdx);

                inverted.Add((v.Copy(), k.Copy()));
            }
            return (new MapValue(inverted).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
        }

        public override RuntimeValue Copy()
        {
            var newPairs = DeepCopyPairs();
            return new MapValue(newPairs).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue()
        {
            foreach (var (_, v) in Pairs)
            {
                if (!v.IsTrue()) return false;
            }
            return true;
        }

        public override string ToString()
        {
            try
            {
                var kvs = Pairs.Select(p =>
                {
                    string ks = p.Key is StringValue sv ? sv.ToRepr() : p.Key.ToString();
                    string vs = p.Value is StringValue vsStr ? vsStr.ToRepr() : p.Value.ToString();
                    return $"{ks}: {vs}";
                });
                return "{" + string.Join(", ", kvs) + "}";
            }
            catch
            {
                return "{map}";
            }
        }
    }
}
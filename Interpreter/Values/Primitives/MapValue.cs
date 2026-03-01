using RaLanguage.Errors;
using RaLanguage.Errors.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class MapValue : RuntimeValue
    {
        public List<(RuntimeValue Key, RuntimeValue Value)> Pairs { get; }

        public MapValue()
        {
            Pairs = new List<(RuntimeValue, RuntimeValue)>();
        }

        public MapValue(List<(RuntimeValue, RuntimeValue)> pairs)
        {
            Pairs = pairs;
        }

        public override RuntimeValueType Type => RuntimeValueType.Map;

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Map)
            {
                var otherMap = (MapValue)other;

                var newPairs = new List<(RuntimeValue, RuntimeValue)>();
                foreach (var (k, v) in Pairs)
                    newPairs.Add((k, v));

                foreach (var (ok, ov) in otherMap.Pairs)
                {
                    int idx = IndexOfKey(newPairs, ok);
                    if (idx >= 0)
                        newPairs[idx] = (ok, ov);
                    else
                        newPairs.Add((ok, ov));
                }

                var result = new MapValue(newPairs).SetContext(Context).SetPos(PositionStart, PositionEnd);
                return (result, null);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            try
            {
                int idx = IndexOfKey(Pairs, other);

                if (idx >= 0)
                {
                    Pairs.RemoveAt(idx);
                    return (new MapValue(Pairs).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }
                else
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Key not found in map", Context));
                }
            }
            catch
            {
                return base.SubbedBy(other);
            }
        }

        public override (RuntimeValue?, Error?) ListAccess(RuntimeValue other)
        {
            try
            {
                int idx = IndexOfKey(Pairs, other);
                if (idx >= 0)
                {
                    return (Pairs[idx].Value, null);
                }
                else
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Key not found in map", Context));
                }
            }
            catch
            {
                return base.ListAccess(other);
            }
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

        private (RuntimeValue?, Error?) EvaluateComparisonMap(RuntimeValue other, Func<RuntimeValue, RuntimeValue, (RuntimeValue?, Error?)> valueComparisonFunc)
        {
            if (other.Type != RuntimeValueType.Map)
                return (new BooleanValue(false).SetContext(Context), null);

            var oMap = (MapValue)other;

            if (Pairs.Count != oMap.Pairs.Count)
                return (new BooleanValue(false).SetContext(Context), null);

            for (int i = 0; i < Pairs.Count; i++)
            {
                var (k1, v1) = Pairs[i];
                int idx = IndexOfKey(oMap.Pairs, k1);
                if (idx < 0)
                    return (new BooleanValue(false).SetContext(Context), null);

                var (k2, v2) = oMap.Pairs[idx];

                var (comparisonResult, comparisonError) = valueComparisonFunc(v1, v2);
                if (comparisonError != null) return (null, comparisonError);
                if (comparisonResult == null) return (new BooleanValue(false).SetContext(Context), null);

                if (!comparisonResult.IsTrue())
                    return (new BooleanValue(false).SetContext(Context), null);
            }

            return (new BooleanValue(true).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            return EvaluateComparisonMap(other, (a, b) => a.GetComparisonEq(b));
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            var eq = EvaluateComparisonMap(other, (a, b) => a.GetComparisonEq(b));
            if (eq.Item2 != null) return (null, eq.Item2);
            if (eq.Item1 == null) return (new BooleanValue(true).SetContext(Context), null);
            return (new BooleanValue(!eq.Item1.IsTrue()).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other)
        {
            return EvaluateComparisonMap(other, (a, b) => a.GetComparisonStrictEq(b));
        }

        public override (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other)
        {
            var eq = EvaluateComparisonMap(other, (a, b) => a.GetComparisonStrictEq(b));
            if (eq.Item2 != null) return (null, eq.Item2);
            if (eq.Item1 == null) return (new BooleanValue(true).SetContext(Context), null);
            return (new BooleanValue(!eq.Item1.IsTrue()).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return base.Notted();
        }

        public override RuntimeValue Copy()
        {
            var newPairs = new List<(RuntimeValue, RuntimeValue)>();
            foreach (var (k, v) in Pairs)
                newPairs.Add((k, v));
            return new MapValue(newPairs).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override bool IsTrue()
        {
            foreach (var (_, v) in Pairs)
            {
                if (!v.IsTrue())
                    return false;
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

        private static int IndexOfKey(List<(RuntimeValue Key, RuntimeValue Value)> pairs, RuntimeValue keyToFind)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                var candidateKey = pairs[i].Key;

                var cmpTuple = candidateKey.GetComparisonStrictEq(keyToFind);
                var cmpVal = cmpTuple.Item1;
                var error = cmpTuple.Item2;
                if (error != null) continue;
                if (cmpVal != null && cmpVal.IsTrue()) return i;

                if (candidateKey.Equals(keyToFind)) return i;
            }

            return -1;
        }
    }
}
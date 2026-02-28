using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ListValue : RuntimeValue
    {
        // TODO: https://chatgpt.com/c/69a1416f-1a40-8327-9137-21565e2941a0

        public List<RuntimeValue> Elements { get; }
        public ListValue(List<RuntimeValue> elements) { Elements = elements; }
        public override RuntimeValueType Type => RuntimeValueType.List;

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            var newList = (ListValue)Copy();
            newList.Elements.Add(other);
            return (newList, null);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                var newList = (ListValue)Copy();
                try
                {
                    int idx = (int)n.Value;
                    if (idx < 0 || idx >= newList.Elements.Count) throw new IndexOutOfRangeException();
                    newList.Elements.RemoveAt(idx);
                    return (newList, null);
                }
                catch
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Element at this index could not be removed from list because index is out of bounds", Context));
                }
            }
            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.List)
            {
                ListValue l = (ListValue)other;
                var newList = (ListValue)Copy();
                newList.Elements.AddRange(l.Elements);
                return (newList, null);
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
                    if (idx < 0 || idx >= Elements.Count) throw new IndexOutOfRangeException();
                    return (Elements[idx], null);
                }
                catch
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Element at this index could not be retrieved from list because index is out of bounds", Context));
                }
            }
            return base.DivedBy(other);
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

                    if (index > Elements.Count - 1)
                    {
                        return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Index out of bounds", Context));
                    }

                    return (Elements[index], null);
                }
            }
            catch {}

            return base.ListAccess(other);
        }

        public override (RuntimeValue?, Error?) ListSet(RuntimeValue indexValue, RuntimeValue value)
        {
            if (indexValue.Type != RuntimeValueType.Number)
            {
                return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, "Index must be a number", Context));
            }

            NumberValue number = (NumberValue)indexValue;
            int index = (int)number.Value;

            if (index < 0 || index >= Elements.Count)
            {
                return (null, new RuntimeError(indexValue.PositionStart, indexValue.PositionEnd, "Index out of bounds", Context));
            }

            Elements[index] = value;
            return (value.SetContext(Context), null);
        }

        public override RuntimeValue Copy()
        {
            return new ListValue(Elements)
                .SetPos(PositionStart, PositionEnd)
                .SetContext(Context);
        }

        public override bool IsTrue()
        {
            foreach (RuntimeValue v in Elements)
            {
                if (!v.IsTrue())
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString() => "[" + string.Join(", ", Elements.Select(e =>
            e is StringValue s ? s.ToRepr() : e.ToString())) + "]";
    }
}
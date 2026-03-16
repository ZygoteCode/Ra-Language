using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values
{
    public abstract class RuntimeValue
    {
        public Position PositionStart { get; set; }
        public Position PositionEnd { get; set; }
        public Context Context { get; set; }
        public VariableDeclarationType VariableDeclarationType { get; set; } = VariableDeclarationType.VARIABLE;
        public abstract RuntimeValueType Type { get; }
        public virtual bool IsCopy => false;

        public RuntimeValue SetPos(Position positionStart, Position positionEnd)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
            return this;
        }

        public RuntimeValue SetContext(Context context)
        {
            Context = context;
            return this;
        }

        public RuntimeValue SetDeclarationType(VariableDeclarationType declarationType)
        {
            VariableDeclarationType = declarationType;
            return this;
        }

        public virtual (RuntimeValue?, Error?) AddedTo(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) SubbedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) MultedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) DivedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) PowedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) ModuledBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) ListAccess(RuntimeValue other) => (null, IllegalOperation(other));

        public virtual (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (Type == RuntimeValueType.Null && other.Type == RuntimeValueType.Null) return (new NumberValue(BigNumber.One).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            return (null, IllegalOperation(other));
        }

        public virtual (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (Type == RuntimeValueType.Null || other.Type == RuntimeValueType.Null)
            {
                if (Type == RuntimeValueType.Null && other.Type == RuntimeValueType.Null) return (new NumberValue(BigNumber.Zero).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
                return (new NumberValue(BigNumber.One).SetPos(PositionStart, PositionEnd).SetContext(Context), null);
            }

            return (null, IllegalOperation(other));
        }

        public virtual (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other) => (null, IllegalOperation(other));
        public virtual (RuntimeValue?, Error?) Notted() => (null, IllegalOperation(this));
        public virtual (RuntimeValue?, Error?) BitwiseNotted() => (null, IllegalOperation(this));
        public virtual (RuntimeValue?, Error?) Factorial() => (null, IllegalOperation(this));
        public virtual (RuntimeValue?, Error?) ListSet(RuntimeValue index, RuntimeValue value) => (null, IllegalOperation(this));

        public virtual (RuntimeValue?, Error?) AndedBy(RuntimeValue other)
        {
            return (new BooleanValue(IsTrue() && other.IsTrue()), null);
        }

        public virtual (RuntimeValue?, Error?) OredBy(RuntimeValue other)
        {
            return (new BooleanValue(IsTrue() || other.IsTrue()), null);
        }

        public virtual (RuntimeValue?, Error?) InCollection(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.List)
            {
                ListValue l = (ListValue)other;

                foreach (var element in l.Elements)
                {
                    if (element.Equals(this)) return (new BooleanValue(true), null);
                }

                return (new BooleanValue(false), null);
            }
            else if (other.Type == RuntimeValueType.Set)
            {
                SetValue s = (SetValue)other;

                foreach (var element in s.Elements)
                {
                    if (element.Equals(this)) return (new BooleanValue(true), null);
                }

                return (new BooleanValue(false), null);
            }
            else if (other.Type == RuntimeValueType.String && Type == RuntimeValueType.String)
            {
                StringValue s1 = (StringValue)other;
                StringValue s2 = (StringValue)this;
                return (new BooleanValue(s1.Value.Contains(s2.Value)), null);
            }
            else if (other.Type == RuntimeValueType.String && Type == RuntimeValueType.Number)
            {
                StringValue s1 = (StringValue)other;
                NumberValue n1 = (NumberValue)this;
                return (new BooleanValue(s1.Value.Contains(n1.Value.ToString())), null);
            }
            else if (other.Type == RuntimeValueType.Tuple)
            {
                TupleValue t = (TupleValue)other;

                foreach (var element in t.Elements)
                {
                    if (element.Equals(this)) return (new BooleanValue(true), null);
                }

                return (new BooleanValue(false), null);
            }
            else if (other.Type == RuntimeValueType.Map && Type == RuntimeValueType.Tuple)
            {
                MapValue m = (MapValue)other;
                TupleValue t = (TupleValue)this;

                if (t.Elements.Count != 2) return (null, IllegalOperation(other));

                RuntimeValue v1 = t.Elements[0], v2 = t.Elements[1];

                foreach (var e in m.Pairs)
                {
                    if (e.Key.Equals(v1) && e.Value.Equals(v2)) return (new BooleanValue(true), null);
                }

                return (new BooleanValue(false), null);
            }

            return (null, IllegalOperation(other));
        }

        public virtual RuntimeResult Execute(List<RuntimeValue> args)
        {
            return new RuntimeResult().Failure(IllegalOperation());
        }

        public override bool Equals(object? obj)
        {
            if (obj == null) return false;

            if (obj is RuntimeValue)
            {
                RuntimeValue value = (RuntimeValue)obj;
                RuntimeValue? result = GetComparisonStrictEq(value).Item1;

                if (result == null) return false;
                if (result.Type != RuntimeValueType.Boolean) return false;
                BooleanValue b = (BooleanValue)result;
                return b.Value;
            }

            return base.Equals(obj);
        }

        public virtual (RuntimeValue?, Error?) CastTo(TypeDescriptor targetType)
        {
            var tn = targetType?.Name?.ToString() ?? "";

            if (string.Equals(tn, "string", StringComparison.Ordinal))
            {
                return (new StringValue(ToString()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "boolean", StringComparison.Ordinal) ||
                string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (new BooleanValue(IsTrue()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal) ||
                string.Equals(tn, "integer", StringComparison.Ordinal) ||
                string.Equals(tn, "i64", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.Integer)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    var l = (LongValue)this;
                    if (l.Value < int.MinValue || l.Value > int.MaxValue)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to int without overflow", Context));
                    }

                    return (new IntegerValue((int)l.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    var f = (FloatValue)this;
                    if (f.Value < int.MinValue || f.Value > int.MaxValue || MathF.Abs(f.Value - MathF.Truncate(f.Value)) > 0.000001f)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to int", Context));
                    }

                    return (new IntegerValue((int)f.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    var d = (DoubleValue)this;
                    if (d.Value < int.MinValue || d.Value > int.MaxValue || Math.Abs(d.Value - Math.Truncate(d.Value)) > 0.000001d)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to int", Context));
                    }

                    return (new IntegerValue((int)d.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)this;
                    var bi = n.Value.ToBigInteger();
                    if (bi < int.MinValue || bi > int.MaxValue || !BigNumber.Parse(bi.ToString()).Equals(n.Value))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer number to int", Context));
                    }

                    return (new IntegerValue((int)bi).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = (StringValue)this;
                    var parsed = IntegerValue.TryParseLiteral(s.Value);
                    if (parsed == null)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s.Value}' to int", Context));
                    }

                    return (parsed.SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    var b = (BooleanValue)this;
                    return (new IntegerValue(b.Value ? 1 : 0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'int'", Context));
            }

            if (string.Equals(tn, "long", StringComparison.Ordinal) ||
                string.Equals(tn, "i64", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.Long)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    var i = (IntegerValue)this;
                    return (new LongValue(i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    var f = (FloatValue)this;
                    if (f.Value < long.MinValue || f.Value > long.MaxValue || MathF.Abs(f.Value - MathF.Truncate(f.Value)) > 0.000001f)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to long", Context));
                    }

                    return (new LongValue((long)f.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    var d = (DoubleValue)this;
                    if (d.Value < long.MinValue || d.Value > long.MaxValue || Math.Abs(d.Value - Math.Truncate(d.Value)) > 0.000001d)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to long", Context));
                    }

                    return (new LongValue((long)d.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)this;
                    var bi = n.Value.ToBigInteger();
                    if (bi < long.MinValue || bi > long.MaxValue || !BigNumber.Parse(bi.ToString()).Equals(n.Value))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to long without overflow", Context));
                    }

                    return (new LongValue((long)bi).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = (StringValue)this;
                    var parsed = LongValue.TryParseLiteral(s.Value);
                    if (parsed == null)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s.Value}' to long", Context));
                    }

                    return (parsed.SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    var b = (BooleanValue)this;
                    return (new LongValue(b.Value ? 1L : 0L).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'long'", Context));
            }

            if (string.Equals(tn, "float", StringComparison.Ordinal) ||
                string.Equals(tn, "f32", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.Float)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    var i = (IntegerValue)this;
                    return (new FloatValue(i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    var l = (LongValue)this;
                    return (new FloatValue(l.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    var d = (DoubleValue)this;
                    return (new FloatValue((float)d.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)this;
                    if (!float.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to float", Context));
                    }

                    return (new FloatValue(f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = (StringValue)this;
                    var parsed = FloatValue.TryParseLiteral(s.Value);
                    if (parsed == null)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s.Value}' to float", Context));
                    }

                    return (parsed.SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    var b = (BooleanValue)this;
                    return (new FloatValue(b.Value ? 1f : 0f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'float'", Context));
            }

            if (string.Equals(tn, "double", StringComparison.Ordinal) ||
                string.Equals(tn, "f64", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.Double)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    var i = (IntegerValue)this;
                    return (new DoubleValue(i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    var l = (LongValue)this;
                    return (new DoubleValue(l.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    var f = (FloatValue)this;
                    return (new DoubleValue(f.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)this;
                    if (!double.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to double", Context));
                    }

                    return (new DoubleValue(d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = (StringValue)this;
                    var parsed = DoubleValue.TryParseLiteral(s.Value);
                    if (parsed == null)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s.Value}' to double", Context));
                    }

                    return (parsed.SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    var b = (BooleanValue)this;
                    return (new DoubleValue(b.Value ? 1.0 : 0.0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'double'", Context));
            }

            if (string.Equals(tn, "number", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.Number)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    var i = (IntegerValue)this;
                    return (new NumberValue(BigNumber.Parse(i.Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    var l = (LongValue)this;
                    return (new NumberValue(BigNumber.Parse(l.Value.ToString())).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    var f = (FloatValue)this;
                    return (new NumberValue(BigNumber.Parse(f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    var d = (DoubleValue)this;
                    return (new NumberValue(BigNumber.Parse(d.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = (StringValue)this;
                    try
                    {
                        return (new NumberValue(BigNumber.Parse(s.Value)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                    catch
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s.Value}' to number", Context));
                    }
                }
                
                if (Type == RuntimeValueType.Boolean)
                {
                    var b = (BooleanValue)this;
                    return (new NumberValue(b.Value ? BigNumber.One : BigNumber.Zero).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'number'", Context));
            }

            return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to '{targetType}'", Context));
        }

        public abstract RuntimeValue Copy();

        public virtual bool IsTrue() => false;

        public Error IllegalOperation(RuntimeValue? other = null)
        {
            if (other == null) other = this;
            return new RuntimeError(PositionStart, other.PositionEnd, "Illegal operation", Context);
        }
    }
}
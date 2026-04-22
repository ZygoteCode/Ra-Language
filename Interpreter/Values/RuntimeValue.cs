using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Operators;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values
{
    public abstract class RuntimeValue
    {
        public Position PositionStart { get; private set; }
        public Position PositionEnd { get; private set; }
        public Context Context { get; private set; }
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
                return (new StringValue(RaLanguage.Utilities.StringConversionUtility.ConvertToString(this)).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "bool", StringComparison.Ordinal))
            {
                return (new BooleanValue(IsTrue()).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }

            if (string.Equals(tn, "int", StringComparison.Ordinal))
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

            if (string.Equals(tn, "long", StringComparison.Ordinal))
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

            if (string.Equals(tn, "float", StringComparison.Ordinal))
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

            if (string.Equals(tn, "double", StringComparison.Ordinal))
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

            if (string.Equals(tn, "uint", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.UnsignedInteger)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    var i = (IntegerValue)this;
                    if (i.Value < 0)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative int to uint", Context));
                    }

                    return (new UnsignedIntegerValue((uint)i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    var l = (LongValue)this;
                    if (l.Value < 0 || l.Value > uint.MaxValue)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to uint without overflow", Context));
                    }

                    return (new UnsignedIntegerValue((uint)l.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    var f = (FloatValue)this;
                    if (MathF.Abs(f.Value - MathF.Truncate(f.Value)) > 0.000001f || f.Value < 0f || f.Value > uint.MaxValue)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to uint", Context));
                    }

                    return (new UnsignedIntegerValue((uint)f.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    var d = (DoubleValue)this;
                    if (Math.Abs(d.Value - Math.Truncate(d.Value)) > 0.000001d || d.Value < 0.0 || d.Value > uint.MaxValue)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to uint", Context));
                    }

                    return (new UnsignedIntegerValue((uint)d.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)this;
                    var s = n.Value.ToString();

                    if (!uint.TryParse(s, out var ui))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast number '{s}' to uint", Context));
                    }

                    return (new UnsignedIntegerValue(ui).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = (StringValue)this;
                    var parsed = UnsignedIntegerValue.TryParseLiteral(s.Value);
                    if (parsed == null)
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s.Value}' to uint", Context));
                    }

                    return (parsed.SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    var b = (BooleanValue)this;
                    return (new UnsignedIntegerValue(b.Value ? 1u : 0u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'uint'", Context));
            }

            if (string.Equals(tn, "ulong", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.UnsignedLong)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedInteger)
                {
                    var u = (UnsignedIntegerValue)this;
                    return (new UnsignedLongValue(u.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    var i = (IntegerValue)this;
                    if (i.Value < 0) return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative int to ulong", Context));
                    return (new UnsignedLongValue((ulong)i.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    var l = (LongValue)this;
                    if (l.Value < 0) return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative long to ulong", Context));
                    return (new UnsignedLongValue((ulong)l.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    var f = (FloatValue)this;
                    if (f.Value < 0f || f.Value > ulong.MaxValue || MathF.Abs(f.Value - MathF.Truncate(f.Value)) > 0.000001f)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to ulong", Context));

                    return (new UnsignedLongValue((ulong)f.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    var d = (DoubleValue)this;
                    if (d.Value < 0d || d.Value > ulong.MaxValue || Math.Abs(d.Value - Math.Truncate(d.Value)) > 0.000001d)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to ulong", Context));

                    return (new UnsignedLongValue((ulong)d.Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)this;
                    if (!ulong.TryParse(n.Value.ToString(), out var ul))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to ulong", Context));

                    return (new UnsignedLongValue(ul).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = (StringValue)this;
                    var parsed = UnsignedLongValue.TryParseLiteral(s.Value);
                    if (parsed == null)
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s.Value}' to ulong", Context));

                    return (parsed.SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    var b = (BooleanValue)this;
                    return (new UnsignedLongValue(b.Value ? 1UL : 0UL).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'ulong'", Context));
            }

            if (string.Equals(tn, "short", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.Short)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    int i = ((IntegerValue)this).Value;
                    if (i < short.MinValue || i > short.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int to short without overflow", Context));

                    return (new ShortValue((short)i).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedInteger)
                {
                    uint u = ((UnsignedIntegerValue)this).Value;
                    if (u > short.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint to short without overflow", Context));

                    return (new ShortValue((short)u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    long l = ((LongValue)this).Value;
                    if (l < short.MinValue || l > short.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to short without overflow", Context));

                    return (new ShortValue((short)l).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedLong)
                {
                    ulong ul = ((UnsignedLongValue)this).Value;
                    if (ul > (ulong)short.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to short without overflow", Context));

                    return (new ShortValue((short)ul).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    float f = ((FloatValue)this).Value;
                    if (MathF.Abs(f - MathF.Truncate(f)) > 0.000001f || f < short.MinValue || f > short.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to short", Context));

                    return (new ShortValue((short)f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    double d = ((DoubleValue)this).Value;
                    if (Math.Abs(d - Math.Truncate(d)) > 0.000001d || d < short.MinValue || d > short.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to short", Context));

                    return (new ShortValue((short)d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)this;
                    if (!short.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to short", Context));

                    return (new ShortValue(s).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var sv = (StringValue)this;
                    if (!short.TryParse(sv.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s))
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{sv.Value}' to short", Context));

                    return (new ShortValue(s).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    return (new ShortValue(((BooleanValue)this).Value ? (short)1 : (short)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'short'", Context));
            }

            if (string.Equals(tn, "ushort", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.UnsignedShort)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Short)
                {
                    short s = ((ShortValue)this).Value;
                    if (s < 0)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative short to ushort", Context));

                    return (new UnsignedShortValue((ushort)s).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    int i = ((IntegerValue)this).Value;
                    if (i < 0 || i > ushort.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int to ushort without overflow", Context));

                    return (new UnsignedShortValue((ushort)i).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedInteger)
                {
                    uint u = ((UnsignedIntegerValue)this).Value;
                    if (u > ushort.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint to ushort without overflow", Context));

                    return (new UnsignedShortValue((ushort)u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    long l = ((LongValue)this).Value;
                    if (l < 0 || l > ushort.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to ushort without overflow", Context));

                    return (new UnsignedShortValue((ushort)l).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedLong)
                {
                    ulong ul = ((UnsignedLongValue)this).Value;
                    if (ul > ushort.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to ushort without overflow", Context));

                    return (new UnsignedShortValue((ushort)ul).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    float f = ((FloatValue)this).Value;
                    if (f < 0f || f > ushort.MaxValue || MathF.Abs(f - MathF.Truncate(f)) > 0.000001f)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to ushort", Context));

                    return (new UnsignedShortValue((ushort)f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    double d = ((DoubleValue)this).Value;
                    if (d < 0d || d > ushort.MaxValue || Math.Abs(d - Math.Truncate(d)) > 0.000001d)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to ushort", Context));

                    return (new UnsignedShortValue((ushort)d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)this;
                    if (!ushort.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var us))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast number to ushort", Context));

                    return (new UnsignedShortValue(us).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = (StringValue)this;
                    var parsed = UnsignedShortValue.TryParseLiteral(s.Value);
                    if (parsed == null)
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s.Value}' to ushort", Context));

                    return (parsed.SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    return (new UnsignedShortValue(((BooleanValue)this).Value ? (ushort)1 : (ushort)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'ushort'", Context));
            }

            if (string.Equals(tn, "int128", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.Int128)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Short)
                {
                    return (new Int128Value(((ShortValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedShort)
                {
                    return (new Int128Value(((UnsignedShortValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    return (new Int128Value(((IntegerValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedInteger)
                {
                    return (new Int128Value(((UnsignedIntegerValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    return (new Int128Value(((LongValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedLong)
                {
                    return (new Int128Value(((UnsignedLongValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    float f = ((FloatValue)this).Value;
                    if (MathF.Abs(f - MathF.Truncate(f)) > 0.000001f)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to int128", Context));

                    return (new Int128Value((Int128)f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    double d = ((DoubleValue)this).Value;
                    if (Math.Abs(d - Math.Truncate(d)) > 0.000001d)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to int128", Context));

                    return (new Int128Value((Int128)d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var s = ((NumberValue)this).Value.ToString();
                    if (!Int128.TryParse(s, out var i128))
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast number '{s}' to int128", Context));

                    return (new Int128Value(i128).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = ((StringValue)this).Value;
                    if (!Int128.TryParse(s, out var i128))
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s}' to int128", Context));

                    return (new Int128Value(i128).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    return (new Int128Value(((BooleanValue)this).Value ? (Int128)1 : (Int128)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'int128'", Context));
            }

            if (string.Equals(tn, "uint128", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.UnsignedInt128)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Short)
                {
                    short s = ((ShortValue)this).Value;
                    if (s < 0) return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative short to uint128", Context));
                    return (new UnsignedInt128Value((UInt128)s).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedShort)
                {
                    return (new UnsignedInt128Value((UInt128)((UnsignedShortValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    int i = ((IntegerValue)this).Value;
                    if (i < 0) return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative int to uint128", Context));
                    return (new UnsignedInt128Value((UInt128)(uint)i).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedInteger)
                {
                    return (new UnsignedInt128Value((UInt128)((UnsignedIntegerValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    long l = ((LongValue)this).Value;
                    if (l < 0) return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative long to uint128", Context));
                    return (new UnsignedInt128Value((UInt128)(ulong)l).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedLong)
                {
                    return (new UnsignedInt128Value((UInt128)((UnsignedLongValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Int128)
                {
                    var v = ((Int128Value)this).Value;
                    if (v < 0) return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast negative int128 to uint128", Context));
                    if (!UInt128.TryParse(v.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var u))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to uint128", Context));
                    return (new UnsignedInt128Value(u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    float f = ((FloatValue)this).Value;
                    if (f < 0f || f > (float)UInt128.MaxValue || MathF.Abs(f - MathF.Truncate(f)) > 0.000001f)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to uint128", Context));

                    if (!UInt128.TryParse(((ulong)f).ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var u))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast float to uint128", Context));

                    return (new UnsignedInt128Value(u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    double d = ((DoubleValue)this).Value;
                    if (d < 0d || d > (double)UInt128.MaxValue || Math.Abs(d - Math.Truncate(d)) > 0.000001d)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to uint128", Context));

                    if (!UInt128.TryParse(((ulong)d).ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var u))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast double to uint128", Context));

                    return (new UnsignedInt128Value(u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var s = ((NumberValue)this).Value.ToString();
                    if (!UInt128.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var u))
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast number '{s}' to uint128", Context));

                    return (new UnsignedInt128Value(u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = ((StringValue)this).Value;
                    if (!UInt128.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var u))
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s}' to uint128", Context));

                    return (new UnsignedInt128Value(u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    return (new UnsignedInt128Value(((BooleanValue)this).Value ? (UInt128)1 : (UInt128)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'uint128'", Context));
            }

            if (string.Equals(tn, "decimal", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.Decimal)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Short)
                {
                    return (new DecimalValue(((ShortValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedShort)
                {
                    return (new DecimalValue(((UnsignedShortValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    return (new DecimalValue(((IntegerValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedInteger)
                {
                    return (new DecimalValue(((UnsignedIntegerValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    return (new DecimalValue(((LongValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedLong)
                {
                    return (new DecimalValue(((UnsignedLongValue)this).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Int128)
                {
                    var v = ((Int128Value)this).Value;
                    if (!decimal.TryParse(v.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to decimal without overflow", Context));

                    return (new DecimalValue(d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedInt128)
                {
                    var v = ((UnsignedInt128Value)this).Value;
                    if (!decimal.TryParse(v.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to decimal without overflow", Context));

                    return (new DecimalValue(d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    var f = ((FloatValue)this).Value;
                    return (new DecimalValue((decimal)f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    var d = ((DoubleValue)this).Value;
                    if (double.IsNaN(d) || double.IsInfinity(d))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast NaN/Infinity to decimal", Context));

                    try
                    {
                        return (new DecimalValue((decimal)d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                    }
                    catch
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast double to decimal without overflow", Context));
                    }
                }

                if (Type == RuntimeValueType.Number)
                {
                    var s = ((NumberValue)this).Value.ToString();
                    if (!decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dec))
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast number '{s}' to decimal", Context));

                    return (new DecimalValue(dec).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = ((StringValue)this).Value;
                    if (!decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dec))
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s}' to decimal", Context));

                    return (new DecimalValue(dec).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    return (new DecimalValue(((BooleanValue)this).Value ? 1m : 0m).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'decimal'", Context));
            }

            if (string.Equals(tn, "byte", StringComparison.Ordinal))
            {
                if (Type == RuntimeValueType.Byte)
                {
                    return (Copy().SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Short)
                {
                    short s = ((ShortValue)this).Value;
                    if (s < byte.MinValue || s > byte.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast short to byte without overflow", Context));

                    return (new ByteValue((byte)s).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedShort)
                {
                    ushort us = ((UnsignedShortValue)this).Value;
                    if (us > byte.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ushort to byte without overflow", Context));

                    return (new ByteValue((byte)us).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Integer)
                {
                    int i = ((IntegerValue)this).Value;
                    if (i < byte.MinValue || i > byte.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int to byte without overflow", Context));

                    return (new ByteValue((byte)i).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedInteger)
                {
                    uint u = ((UnsignedIntegerValue)this).Value;
                    if (u > byte.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint to byte without overflow", Context));

                    return (new ByteValue((byte)u).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Long)
                {
                    long l = ((LongValue)this).Value;
                    if (l < byte.MinValue || l > byte.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast long to byte without overflow", Context));

                    return (new ByteValue((byte)l).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedLong)
                {
                    ulong ul = ((UnsignedLongValue)this).Value;
                    if (ul > byte.MaxValue)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast ulong to byte without overflow", Context));

                    return (new ByteValue((byte)ul).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Int128)
                {
                    var v = ((Int128Value)this).Value;
                    if (v < 0 || v > 255)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast int128 to byte without overflow", Context));

                    return (new ByteValue((byte)v).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.UnsignedInt128)
                {
                    var v = ((UnsignedInt128Value)this).Value;
                    if (v > 255)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast uint128 to byte without overflow", Context));

                    return (new ByteValue((byte)v).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Float)
                {
                    float f = ((FloatValue)this).Value;
                    if (f < 0f || f > byte.MaxValue || MathF.Abs(f - MathF.Truncate(f)) > 0.000001f)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer float to byte", Context));

                    return (new ByteValue((byte)f).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Double)
                {
                    double d = ((DoubleValue)this).Value;
                    if (d < 0d || d > byte.MaxValue || Math.Abs(d - Math.Truncate(d)) > 0.000001d)
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer double to byte", Context));

                    return (new ByteValue((byte)d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Decimal)
                {
                    decimal d = ((DecimalValue)this).Value;
                    if (d < 0m || d > byte.MaxValue || d != decimal.Truncate(d))
                        return (null, new RuntimeError(PositionStart, PositionEnd, "Cannot cast non-integer decimal to byte", Context));

                    return (new ByteValue((byte)d).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Number)
                {
                    var s = ((NumberValue)this).Value.ToString();
                    if (!byte.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var b))
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast number '{s}' to byte", Context));

                    return (new ByteValue(b).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.String)
                {
                    var s = ((StringValue)this).Value;
                    if (!byte.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var b))
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast string '{s}' to byte", Context));

                    return (new ByteValue(b).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                if (Type == RuntimeValueType.Boolean)
                {
                    return (new ByteValue(((BooleanValue)this).Value ? (byte)1 : (byte)0).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                }

                return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to 'byte'", Context));
            }

            return (null, new RuntimeError(PositionStart, PositionEnd, $"Cannot cast type '{Type}' to '{targetType}'", Context));
        }

        protected (RuntimeValue?, Error?) TryOperatorDispatch(TokenType operatorType, RuntimeValue other, Func<RuntimeValue, RuntimeValue, (RuntimeValue?, Error?)> fallback)
        {
            if (this is RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue classInstance)
            {
                var type = classInstance.Definition;
                var parameterTypeName = GetTypeName(other);

                var op = type.ResolveOperator(operatorType, parameterTypeName);
                if (op != null)
                {
                    if (IsComparisonOperator(operatorType) && op.ReturnType != null &&
                        !string.Equals(op.ReturnType.Name, "bool", StringComparison.Ordinal))
                    {
                        return (null, new RuntimeError(
                            op.PositionStart,
                            op.PositionEnd,
                            $"Comparison operator must return 'bool', but returns '{op.ReturnType.Name}'",
                            Context));
                    }

                    try
                    {
                        var boundOp = new BoundOperatorValue(
                            this,
                            operatorType,
                            parameterTypeName,
                            op.ReturnType,
                            op.BodyNode,
                            op.ShouldAutoReturn)
                            .SetContext(Context)
                            .SetPos(PositionStart, PositionEnd);

                        var result = boundOp.Execute(new List<RuntimeValue> { other });
                        if (result.Error != null)
                            return (null, result.Error);

                        if (result.Value == null)
                            return (null, new RuntimeError(
                                PositionStart,
                                PositionEnd,
                                $"Operator {operatorType} returned null value",
                                Context));

                        return (result.Value, null);
                    }
                    catch (Exception ex)
                    {
                        return (null, new RuntimeError(
                            PositionStart,
                            PositionEnd,
                            $"Operator execution failed: {ex.Message}",
                            Context));
                    }
                }
                
                return (null, new RuntimeError(
                    PositionStart,
                    PositionEnd,
                    $"Operator '{GetOperatorSymbol(operatorType)}' is not defined for class '{type.ClassName}' and parameter type '{parameterTypeName}'",
                    Context));
            }
            else if (this is RaLanguage.Interpreter.Values.Structs.StructInstanceValue structInstance)
            {
                var type = structInstance.Definition;
                var parameterTypeName = GetTypeName(other);

                var op = type.ResolveOperator(operatorType, parameterTypeName);
                if (op != null)
                {
                    if (IsComparisonOperator(operatorType) && op.ReturnType != null &&
                        !string.Equals(op.ReturnType.Name, "bool", StringComparison.Ordinal))
                    {
                        return (null, new RuntimeError(
                            op.PositionStart,
                            op.PositionEnd,
                            $"Comparison operator must return 'bool', but returns '{op.ReturnType.Name}'",
                            Context));
                    }

                    try
                    {
                        var boundOp = new BoundOperatorValue(
                            this,
                            operatorType,
                            parameterTypeName,
                            op.ReturnType,
                            op.BodyNode,
                            op.ShouldAutoReturn)
                            .SetContext(Context)
                            .SetPos(PositionStart, PositionEnd);

                        var result = boundOp.Execute(new List<RuntimeValue> { other });
                        if (result.Error != null)
                            return (null, result.Error);

                        if (result.Value == null)
                            return (null, new RuntimeError(
                                PositionStart,
                                PositionEnd,
                                $"Operator {operatorType} returned null value",
                                Context));

                        return (result.Value, null);
                    }
                    catch (Exception ex)
                    {
                        return (null, new RuntimeError(
                            PositionStart,
                            PositionEnd,
                            $"Operator execution failed: {ex.Message}",
                            Context));
                    }
                }
                
                return (null, new RuntimeError(
                    PositionStart,
                    PositionEnd,
                    $"Operator '{GetOperatorSymbol(operatorType)}' is not defined for struct '{type.StructName}' and parameter type '{parameterTypeName}'",
                    Context));
            }

            return fallback(this, other);
        }

        private string GetOperatorSymbol(TokenType type)
        {
            return type switch
            {
                TokenType.PLUS => "+",
                TokenType.MINUS => "-",
                TokenType.MUL => "*",
                TokenType.DIV => "/",
                TokenType.POW => "^",
                TokenType.MODULO => "%",
                TokenType.EE => "==",
                TokenType.NE => "!=",
                TokenType.LT => "<",
                TokenType.GT => ">",
                TokenType.LTE => "<=",
                TokenType.GTE => ">=",
                TokenType.BITWISE_AND => "&",
                TokenType.BITWISE_OR => "|",
                TokenType.BITWISE_LEFT_SHIFT => "<<",
                TokenType.BITWISE_RIGHT_SHIFT => ">>",
                _ => type.ToString()
            };
        }

        private string GetTypeName(RuntimeValue value)
        {
            return value.Type switch
            {
                RuntimeValueType.ClassInstance => ((RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue)value).Definition.ClassName,
                RuntimeValueType.StructInstance => ((RaLanguage.Interpreter.Values.Structs.StructInstanceValue)value).Definition.StructName,
                RuntimeValueType.Integer => "int",
                RuntimeValueType.Long => "long",
                RuntimeValueType.Float => "float",
                RuntimeValueType.Double => "double",
                RuntimeValueType.Short => "short",
                RuntimeValueType.UnsignedShort => "ushort",
                RuntimeValueType.UnsignedInteger => "uint",
                RuntimeValueType.UnsignedLong => "ulong",
                RuntimeValueType.Int128 => "int128",
                RuntimeValueType.UnsignedInt128 => "uint128",
                RuntimeValueType.Decimal => "decimal",
                RuntimeValueType.Byte => "byte",
                RuntimeValueType.Boolean => "bool",
                RuntimeValueType.String => "string",
                _ => value.Type.ToString()
            };
        }

        private bool IsComparisonOperator(TokenType type)
        {
            return type switch
            {
                TokenType.EE or TokenType.NE or TokenType.LT or TokenType.GT or
                TokenType.LTE or TokenType.GTE or TokenType.STRICT_EE or TokenType.STRICT_NE => true,
                _ => false
            };
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
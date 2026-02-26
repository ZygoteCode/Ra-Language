using RaLanguage.Errors;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class BooleanValue : RuntimeValue
    {
        public bool Value { get; }
        public static BooleanValue True => new BooleanValue(true);
        public static BooleanValue False => new BooleanValue(false);
        public override RuntimeValueType Type => RuntimeValueType.Boolean;

        public BooleanValue(bool value)
        {
            Value = value;
        }

        public override RuntimeValue Copy()
        {
            return new BooleanValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue(b.Value == Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(s.Value == Value.ToString()).SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue(b.Value != Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(s.Value != Value.ToString()).SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) AndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(Value && !n.Value.IsZero()).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue(Value && b.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(Value && s.Value == "true").SetContext(Context), null);
            }

            return base.AndedBy(other);
        }

        public override (RuntimeValue?, Error?) OredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(Value || !n.Value.IsZero()).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue(Value || b.Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(Value || s.Value == "true").SetContext(Context), null);
            }

            return base.OredBy(other);
        }

        public override (RuntimeValue?, Error?) Notted()
        {
            return (new BooleanValue(!Value).SetContext(Context), null);
        }

        public override bool IsTrue() => Value;
        public override string ToString() => Value.ToString().ToLower();
    }
}
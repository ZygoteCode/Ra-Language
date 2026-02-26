using Microsoft.VisualBasic;
using RaLanguage.Errors;
using System.Text;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class StringValue : RuntimeValue
    {
        public string Value { get; }
        public StringValue(string value) { Value = value; }
        public override RuntimeValueType Type => RuntimeValueType.String;

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new StringValue(Value + s.Value).SetContext(Context), null);
            }

            return base.AddedTo(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                var sb = new StringBuilder();
                for (int i = 0; i < (int)n.Value; i++) sb.Append(Value);
                return (new StringValue(sb.ToString()).SetContext(Context), null);
            }
            return base.MultedBy(other);
        }

        public override bool IsTrue() => Value.Length > 0;

        public override RuntimeValue Copy()
        {
            return new StringValue(Value).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override (RuntimeValue?, Error?) BitwiseNotted()
        {
            return (new StringValue(Strings.StrReverse(Value)).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(s.Value == Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(n.Value.ToString() == Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue(b.Value.ToString() == Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Null)
            {
                return (new BooleanValue(Value == "null").SetContext(Context), null);
            }

            return base.GetComparisonEq(other);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(s.Value != Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(n.Value.ToString() != Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue(b.Value.ToString() != Value).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Null)
            {
                return (new BooleanValue(Value != "null").SetContext(Context), null);
            }

            return base.GetComparisonNe(other);
        }

        public override (RuntimeValue?, Error?) AndedBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(s.Value == "true" && Value == "true").SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(n.IsTrue() && Value == "true").SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue(b.Value && Value == "true").SetContext(Context), null);
            }

            return base.AndedBy(other);
        }

        public override (RuntimeValue?, Error?) OredBy(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(s.Value == "true" || Value == "true").SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Number)
            {
                NumberValue n = (NumberValue)other;
                return (new BooleanValue(n.IsTrue() || Value == "true").SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.Boolean)
            {
                BooleanValue b = (BooleanValue)other;
                return (new BooleanValue(b.Value || Value == "true").SetContext(Context), null);
            }

            return base.OredBy(other);
        }

        public override string ToString() => Value;
        public string ToRepr() => $"\"{Value}\""; // for debug repr
    }
}
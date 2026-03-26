using RaLanguage.Errors;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class NullValue : RuntimeValue
    {
        public sealed override RuntimeValueType Type => RuntimeValueType.Null;
        public static NullValue Null => new NullValue();
        public sealed override bool IsCopy => true;

        public sealed override RuntimeValue Copy()
        {
            return new NullValue().SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other.Type == RuntimeValueType.Null)
            {
                return (new BooleanValue(true).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(s.Value == "null").SetContext(Context), null);
            }

            return (new BooleanValue(true).SetContext(Context), null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other.Type != RuntimeValueType.Null)
            {
                return (new BooleanValue(true).SetContext(Context), null);
            }
            else if (other.Type == RuntimeValueType.String)
            {
                StringValue s = (StringValue)other;
                return (new BooleanValue(s.Value != "null").SetContext(Context), null);
            }

            return (new BooleanValue(false).SetContext(Context), null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other)
        {
            return (new BooleanValue(other.Type == RuntimeValueType.Null).SetContext(Context), null);
        }

        public sealed override (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other)
        {
            return (new BooleanValue(other.Type != RuntimeValueType.Null).SetContext(Context), null);
        }

        public sealed override bool IsTrue() => false;
        public sealed override string ToString() => "null";
    }
}
using RaLanguage.Errors;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class NullValue : RuntimeValue
    {
        public override RuntimeValueType Type => RuntimeValueType.Null;

        public override RuntimeValue Copy()
        {
            return new NullValue().SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other is NullValue)
            {
                return (new NumberValue(BigNumber.One).SetContext(Context), null);
            }

            return (new NumberValue(BigNumber.Zero).SetContext(Context), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            if (other is not NullValue)
            {
                return (new NumberValue(BigNumber.One).SetContext(Context), null);
            }

            return (new NumberValue(BigNumber.Zero).SetContext(Context), null);
        }

        public override string ToString() => "null";
    }
}
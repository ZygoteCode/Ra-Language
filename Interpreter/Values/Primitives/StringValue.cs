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

        public override string ToString() => Value;
        public string ToRepr() => $"\"{Value}\""; // for debug repr
    }
}
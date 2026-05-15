using RaLanguage.Errors;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class GenericTypeValue : RuntimeValue
    {
        public string ParameterName { get; }
        public TypeDescriptor BoundType { get; }

        public override RuntimeValueType Type => RuntimeValueType.GenericTypeBinding;
        public override bool IsCopy => false;

        public GenericTypeValue(string parameterName, TypeDescriptor boundType)
        {
            ParameterName = parameterName;
            BoundType = boundType;
        }

        public override RuntimeValue Copy()
            => new GenericTypeValue(ParameterName, BoundType)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other is GenericTypeValue g)
            {
                bool eq = TypeSystem.StrictTypeEquals(BoundType, g.BoundType);
                return (new BooleanValue(eq).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            return (new BooleanValue(false).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other)
        {
            var (eq, err) = GetComparisonEq(other);
            if (err != null) return (null, err);
            return (new BooleanValue(!((BooleanValue)eq!).Value).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
        }

        public override string ToString() => BoundType?.ToString() ?? ParameterName;
    }
}

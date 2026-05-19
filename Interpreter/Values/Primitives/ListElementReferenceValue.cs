using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ListElementReferenceValue : RuntimeValue, IReferenceValue
    {
        public ListValue List { get; }
        public int Index { get; }

        public override RuntimeValueType Type => RuntimeValueType.Reference;

        public RuntimeValue Value
        {
            get
            {
                if (Index < 0 || Index >= List.Elements.Count)
                    throw new InvalidOperationException("List index out of range");
                return List.Elements[Index];
            }
            set
            {
                if (Index < 0 || Index >= List.Elements.Count)
                    throw new InvalidOperationException("List index out of range");
                List.Elements[Index] = value;
            }
        }

        public ListElementReferenceValue(ListValue list, int index)
        {
            List = list;
            Index = index;
        }

        public override RuntimeValue Copy()
        {
            return new ListElementReferenceValue(List, Index)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public override string ToString() => $"&list[{Index}]={Value}";

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other) => Value.AddedTo(other);
        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other) => Value.SubbedBy(other);
        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other) => Value.MultedBy(other);
        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other) => Value.DivedBy(other);
        public override (RuntimeValue?, Error?) PowedBy(RuntimeValue other) => Value.PowedBy(other);
        public override (RuntimeValue?, Error?) ModuledBy(RuntimeValue other) => Value.ModuledBy(other);
        public override (RuntimeValue?, Error?) BitwiseLeftShiftedBy(RuntimeValue other) => Value.BitwiseLeftShiftedBy(other);
        public override (RuntimeValue?, Error?) BitwiseRightShiftedBy(RuntimeValue other) => Value.BitwiseRightShiftedBy(other);
        public override (RuntimeValue?, Error?) BitwiseAndedBy(RuntimeValue other) => Value.BitwiseAndedBy(other);
        public override (RuntimeValue?, Error?) BitwiseOredBy(RuntimeValue other) => Value.BitwiseOredBy(other);
        public override (RuntimeValue?, Error?) ListAccess(RuntimeValue other) => Value.ListAccess(other);
        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other) => Value.GetComparisonEq(other);
        public override (RuntimeValue?, Error?) GetComparisonNe(RuntimeValue other) => Value.GetComparisonNe(other);
        public override (RuntimeValue?, Error?) GetComparisonStrictEq(RuntimeValue other) => Value.GetComparisonStrictEq(other);
        public override (RuntimeValue?, Error?) GetComparisonStrictNe(RuntimeValue other) => Value.GetComparisonStrictNe(other);
        public override (RuntimeValue?, Error?) GetComparisonLt(RuntimeValue other) => Value.GetComparisonLt(other);
        public override (RuntimeValue?, Error?) GetComparisonGt(RuntimeValue other) => Value.GetComparisonGt(other);
        public override (RuntimeValue?, Error?) GetComparisonLte(RuntimeValue other) => Value.GetComparisonLte(other);
        public override (RuntimeValue?, Error?) GetComparisonGte(RuntimeValue other) => Value.GetComparisonGte(other);
        public override (RuntimeValue?, Error?) Notted() => Value.Notted();
        public override (RuntimeValue?, Error?) BitwiseNotted() => Value.BitwiseNotted();
        public override (RuntimeValue?, Error?) Factorial() => Value.Factorial();
        public override (RuntimeValue?, Error?) AndedBy(RuntimeValue other) => Value.AndedBy(other);
        public override (RuntimeValue?, Error?) OredBy(RuntimeValue other) => Value.OredBy(other);
        public override (RuntimeValue?, Error?) InCollection(RuntimeValue other) => Value.InCollection(other);
        public override bool IsTrue() => Value.IsTrue();
    }
}

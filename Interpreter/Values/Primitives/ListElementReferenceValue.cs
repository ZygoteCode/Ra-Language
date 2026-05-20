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

        public override ValueResult AddedTo(RuntimeValue other) => Value.AddedTo(other);
        public override ValueResult SubbedBy(RuntimeValue other) => Value.SubbedBy(other);
        public override ValueResult MultedBy(RuntimeValue other) => Value.MultedBy(other);
        public override ValueResult DivedBy(RuntimeValue other) => Value.DivedBy(other);
        public override ValueResult PowedBy(RuntimeValue other) => Value.PowedBy(other);
        public override ValueResult ModuledBy(RuntimeValue other) => Value.ModuledBy(other);
        public override ValueResult BitwiseLeftShiftedBy(RuntimeValue other) => Value.BitwiseLeftShiftedBy(other);
        public override ValueResult BitwiseRightShiftedBy(RuntimeValue other) => Value.BitwiseRightShiftedBy(other);
        public override ValueResult BitwiseAndedBy(RuntimeValue other) => Value.BitwiseAndedBy(other);
        public override ValueResult BitwiseOredBy(RuntimeValue other) => Value.BitwiseOredBy(other);
        public override ValueResult ListAccess(RuntimeValue other) => Value.ListAccess(other);
        public override ValueResult GetComparisonEq(RuntimeValue other) => Value.GetComparisonEq(other);
        public override ValueResult GetComparisonNe(RuntimeValue other) => Value.GetComparisonNe(other);
        public override ValueResult GetComparisonStrictEq(RuntimeValue other) => Value.GetComparisonStrictEq(other);
        public override ValueResult GetComparisonStrictNe(RuntimeValue other) => Value.GetComparisonStrictNe(other);
        public override ValueResult GetComparisonLt(RuntimeValue other) => Value.GetComparisonLt(other);
        public override ValueResult GetComparisonGt(RuntimeValue other) => Value.GetComparisonGt(other);
        public override ValueResult GetComparisonLte(RuntimeValue other) => Value.GetComparisonLte(other);
        public override ValueResult GetComparisonGte(RuntimeValue other) => Value.GetComparisonGte(other);
        public override ValueResult Notted() => Value.Notted();
        public override ValueResult BitwiseNotted() => Value.BitwiseNotted();
        public override ValueResult Factorial() => Value.Factorial();
        public override ValueResult AndedBy(RuntimeValue other) => Value.AndedBy(other);
        public override ValueResult OredBy(RuntimeValue other) => Value.OredBy(other);
        public override ValueResult InCollection(RuntimeValue other) => Value.InCollection(other);
        public override bool IsTrue() => Value.IsTrue();
    }
}

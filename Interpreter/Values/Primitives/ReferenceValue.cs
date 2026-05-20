using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ReferenceValue : RuntimeValue, IReferenceValue
    {
        public SymbolTable TargetSymbolTable { get; }
        public string VariableName { get; }
        public override RuntimeValueType Type => RuntimeValueType.Reference;

        public RuntimeValue Value
        {
            get
            {
                var entry = TargetSymbolTable.GetEntry(VariableName);
                if (entry == null)
                    throw new InvalidOperationException($"Referenced variable '{VariableName}' no longer exists");
                return entry.Value;
            }
            set
            {
                var entry = TargetSymbolTable.GetEntry(VariableName);
                if (entry == null)
                    throw new InvalidOperationException($"Referenced variable '{VariableName}' no longer exists");

                // The IsConstBinding flag is the canonical "absolutely immutable" check
                // now (const, let const). Explicit DeclarationType checks below cover
                // the rest of the immutability story so that the legacy interface and
                // the borrow interface enforce the same guarantees.
                if (entry.IsConstBinding || entry.DeclarationType == Parser.Nodes.Variables.VariableDeclarationType.CONST)
                    throw new InvalidOperationException($"Cannot modify const variable '{VariableName}'");

                if (entry.DeclarationType == Parser.Nodes.Variables.VariableDeclarationType.LET_CONST)
                    throw new InvalidOperationException($"Cannot modify 'let const' variable '{VariableName}'");

                if (entry.DeclarationType == Parser.Nodes.Variables.VariableDeclarationType.FINAL)
                    throw new InvalidOperationException($"Cannot modify final variable '{VariableName}'");

                if (entry.DeclarationType == Parser.Nodes.Variables.VariableDeclarationType.LET)
                    throw new InvalidOperationException($"Cannot modify immutable 'let' variable '{VariableName}'");

                if (entry.IsBorrowed)
                    throw new InvalidOperationException($"Cannot modify '{VariableName}' through an alias while borrows are alive");

                entry.Value = value;
            }
        }

        public ReferenceValue(SymbolTable targetSymbolTable, string variableName)
        {
            TargetSymbolTable = targetSymbolTable;
            VariableName = variableName;
        }

        public override RuntimeValue Copy()
        {
            return new ReferenceValue(TargetSymbolTable, VariableName)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public override string ToString()
        {
            try
            {
                return $"&{VariableName}={Value}";
            }
            catch
            {
                return $"&{VariableName}=<invalid>";
            }
        }

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

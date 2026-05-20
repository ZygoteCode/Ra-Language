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

using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Structs
{
    public class StructInstanceValue : RuntimeValue
    {
        public StructTypeValue Definition { get; }
        public Dictionary<string, RuntimeValue> Fields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> FieldPublicity { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, VariableDeclarationType> FieldDeclarationTypes { get; } = new(StringComparer.Ordinal);

        // M41: shape-indexed slot array, parity with ClassInstanceValue (M38).
        // Dictionary above remains ground truth for reflection / iteration;
        // slot array is the O(1) read path consulted by the M28.1 IC.
        public RuntimeValue?[] FieldSlots;

        public sealed override RuntimeValueType Type => RuntimeValueType.StructInstance;
        public sealed override bool IsCopy => true;

        public StructInstanceValue(StructTypeValue definition)
        {
            Definition = definition;
            int slotCount = definition.FieldSlotCount;
            FieldSlots = slotCount > 0 ? new RuntimeValue?[slotCount] : System.Array.Empty<RuntimeValue?>();
        }

        public void SetField(string name, RuntimeValue value, bool isPublic, VariableDeclarationType declarationType = VariableDeclarationType.VARIABLE)
        {
            var stored = value.IsCopy ? value.Copy() : value;
            Fields[name] = stored;
            FieldPublicity[name] = isPublic;
            FieldDeclarationTypes[name] = declarationType;
            int idx = Definition.GetFieldSlotIndex(name);
            if ((uint)idx < (uint)FieldSlots.Length) FieldSlots[idx] = stored;
        }

        public bool HasField(string name) => Fields.ContainsKey(name);

        public bool IsFieldPublic(string name) => FieldPublicity.TryGetValue(name, out var p) && p;

        public VariableDeclarationType GetFieldDeclarationType(string name)
            => FieldDeclarationTypes.TryGetValue(name, out var dt) ? dt : VariableDeclarationType.VARIABLE;

        public RuntimeValue GetField(string name)
        {
            var v = Fields[name];
            return v.IsCopy ? v.Copy() : v;
        }

        public void SetMember(string name, RuntimeValue value)
        {
            if (!Fields.ContainsKey(name))
                throw new KeyNotFoundException(name);

            var stored = value.IsCopy ? value.Copy() : value;
            Fields[name] = stored;
            int idx = Definition.GetFieldSlotIndex(name);
            if ((uint)idx < (uint)FieldSlots.Length) FieldSlots[idx] = stored;
        }

        public sealed override ValueResult AddedTo(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.PLUS, other, (l, r) => l.AddedTo(other));

        public sealed override ValueResult SubbedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.MINUS, other, (l, r) => l.SubbedBy(other));

        public sealed override ValueResult MultedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.MUL, other, (l, r) => l.MultedBy(other));

        public sealed override ValueResult DivedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.DIV, other, (l, r) => l.DivedBy(other));

        public sealed override ValueResult PowedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.POW, other, (l, r) => l.PowedBy(other));

        public sealed override ValueResult ModuledBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.MODULO, other, (l, r) => l.ModuledBy(other));

        public sealed override ValueResult BitwiseLeftShiftedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_LEFT_SHIFT, other, (l, r) => l.BitwiseLeftShiftedBy(other));

        public sealed override ValueResult BitwiseRightShiftedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_RIGHT_SHIFT, other, (l, r) => l.BitwiseRightShiftedBy(other));

        public sealed override ValueResult BitwiseAndedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_AND, other, (l, r) => l.BitwiseAndedBy(other));

        public sealed override ValueResult BitwiseOredBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_OR, other, (l, r) => l.BitwiseOredBy(other));

        public sealed override ValueResult GetComparisonEq(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.EE, other, (l, r) => l.GetComparisonEq(other));

        public sealed override ValueResult GetComparisonNe(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.NE, other, (l, r) => l.GetComparisonNe(other));

        public sealed override ValueResult GetComparisonLt(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.LT, other, (l, r) => l.GetComparisonLt(other));

        public sealed override ValueResult GetComparisonGt(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.GT, other, (l, r) => l.GetComparisonGt(other));

        public sealed override ValueResult GetComparisonLte(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.LTE, other, (l, r) => l.GetComparisonLte(other));

        public sealed override ValueResult GetComparisonGte(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.GTE, other, (l, r) => l.GetComparisonGte(other));

        public sealed override RuntimeValue Copy()
        {
            var copy = new StructInstanceValue(Definition);
            foreach (var kv in Fields)
            {
                copy.Fields[kv.Key] = kv.Value.IsCopy ? kv.Value.Copy() : kv.Value;
                copy.FieldPublicity[kv.Key] = FieldPublicity.TryGetValue(kv.Key, out var p) && p;
                copy.FieldDeclarationTypes[kv.Key] = FieldDeclarationTypes.TryGetValue(kv.Key, out var dt) ? dt : VariableDeclarationType.VARIABLE;
            }

            return copy.SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public (string value, bool hasCustomToString) TryCallToString()
        {
            var toStringMethod = Definition.Methods
                .FirstOrDefault(m => string.Equals(m.NameTok.Value?.ToString(), "to_string", StringComparison.Ordinal) 
                                  && m.ArgNameToks.Count == 0);

            if (toStringMethod == null)
            {
                return (ToString(), false);
            }

            try
            {
                var boundMethod = new BoundStructMethodValue(Definition, this, toStringMethod)
                    .SetContext(Context)
                    .SetPos(PositionStart, PositionEnd);

                var result = SyncAwait.Get(boundMethod.Execute(new List<RuntimeValue>()));

                if (result.Error != null || result.Value == null)
                {
                    return (ToString(), false);
                }

                if (result.Value.Type == RuntimeValueType.String)
                {
                    return (((RaLanguage.Interpreter.Values.Primitives.StringValue)result.Value).Value, true);
                }

                return (ToString(), false);
            }
            catch
            {
                return (ToString(), false);
            }
        }

        public sealed override string ToString()
            => $"{Definition.StructName}{{{string.Join(", ", Fields.Select(kv => $"{kv.Key}: {kv.Value}"))}}}";
    }
}
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ClassInstanceValue : RuntimeValue
    {
        public ClassTypeValue Definition { get; }
        public Dictionary<string, RuntimeValue> Fields { get; }
        public Dictionary<string, bool> FieldPublicity { get; }
        public Dictionary<string, TypeDescriptor?> FieldTypes { get; }
        public Dictionary<string, VariableDeclarationType> FieldDeclarationTypes { get; }
        public Dictionary<string, TypeDescriptor> GenericBindings { get; }

        // M38: shape-indexed slot array. Indexed by
        // Definition.GetFieldSlotIndex(name). Lazily resized when a field is
        // first assigned. The Dictionary above remains the ground truth for
        // code paths that need to iterate by name (reflection builtins,
        // annotations_of, ToString); the slot array is a parallel store
        // optimised for O(1) field reads from the IC-driven hot path. Both
        // stores are kept in sync by SetField / SetMember.
        public RuntimeValue?[] FieldSlots;

        public override RuntimeValueType Type => RuntimeValueType.ClassInstance;
        public override bool IsCopy => false;

        public ClassInstanceValue(ClassTypeValue definition)
            : this(
                definition,
                new Dictionary<string, RuntimeValue>(StringComparer.Ordinal),
                new Dictionary<string, bool>(StringComparer.Ordinal),
                new Dictionary<string, TypeDescriptor?>(StringComparer.Ordinal),
                new Dictionary<string, VariableDeclarationType>(),
                new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal))
        {
        }

        public ClassInstanceValue(ClassTypeValue definition, Dictionary<string, TypeDescriptor> genericBindings)
            : this(
                definition,
                new Dictionary<string, RuntimeValue>(StringComparer.Ordinal),
                new Dictionary<string, bool>(StringComparer.Ordinal),
                new Dictionary<string, TypeDescriptor?>(StringComparer.Ordinal),
                new Dictionary<string, VariableDeclarationType>(),
                genericBindings ?? new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal))
        {
        }

        private ClassInstanceValue(
            ClassTypeValue definition,
            Dictionary<string, RuntimeValue> fields,
            Dictionary<string, bool> publicity,
            Dictionary<string, TypeDescriptor?> types,
            Dictionary<string, VariableDeclarationType> declarationTypes,
            Dictionary<string, TypeDescriptor> genericBindings)
        {
            Definition = definition;
            Fields = fields;
            FieldPublicity = publicity;
            FieldTypes = types;
            FieldDeclarationTypes = declarationTypes;
            GenericBindings = genericBindings ?? new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal);
            // M38: size the slot array to the class's static shape. Empty
            // array when the class declares no fields (avoids array
            // allocation for tag-only classes). On Copy, we rebuild the
            // slot array from the source dict so the new instance shares
            // a shape but not the underlying values reference.
            int slotCount = definition.FieldSlotCount;
            FieldSlots = slotCount > 0 ? new RuntimeValue?[slotCount] : System.Array.Empty<RuntimeValue?>();
            if (slotCount > 0 && fields.Count > 0)
            {
                foreach (var kv in fields)
                {
                    int idx = definition.GetFieldSlotIndex(kv.Key);
                    if ((uint)idx < (uint)FieldSlots.Length)
                        FieldSlots[idx] = kv.Value;
                }
            }
        }

        public void SetField(string name, RuntimeValue value, bool isPublic, TypeDescriptor? fieldType = null, VariableDeclarationType declarationType = VariableDeclarationType.VARIABLE)
        {
            var stored = value.IsCopy ? value.Copy() : value;
            Fields[name] = stored;
            FieldPublicity[name] = isPublic;
            FieldTypes[name] = fieldType;
            FieldDeclarationTypes[name] = declarationType;
            // M38: mirror into shape-indexed slot array.
            int idx = Definition.GetFieldSlotIndex(name);
            if ((uint)idx < (uint)FieldSlots.Length)
                FieldSlots[idx] = stored;
        }

        public bool HasField(string name) => Fields.ContainsKey(name);
        public bool IsFieldPublic(string name) => FieldPublicity.TryGetValue(name, out var p) && p;

        public TypeDescriptor? GetFieldType(string name)
            => FieldTypes.TryGetValue(name, out var t) ? t : null;

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
            // M38: mirror into the slot array. The KeyNotFoundException
            // above ensures `name` is a declared field so the slot index
            // is always valid for this class's shape.
            int idx = Definition.GetFieldSlotIndex(name);
            if ((uint)idx < (uint)FieldSlots.Length)
                FieldSlots[idx] = stored;
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

        public override RuntimeValue Copy()
            => new ClassInstanceValue(Definition, Fields, FieldPublicity, FieldTypes, FieldDeclarationTypes, GenericBindings)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public (string value, bool hasCustomToString) TryCallToString()
        {
            var toStringMethod = Definition.ResolveInstanceMethods("to_string")
                .FirstOrDefault(m => m.ArgNameToks.Count == 0);

            if (toStringMethod == null)
            {
                return (ToString(), false);
            }

            try
            {
                var boundMethod = new BoundClassMethodValue(Definition, this, toStringMethod, false)
                    .SetContext(Context)
                    .SetPos(PositionStart, PositionEnd);

                var result = SyncAwait.Get(boundMethod.Execute(new List<RuntimeValue>()));

                if (result.Error != null || result.Value == null)
                {
                    return (ToString(), false);
                }

                if (result.Value.Type == RuntimeValueType.String)
                {
                    return (((StringValue)result.Value).Value, true);
                }

                return (ToString(), false);
            }
            catch
            {
                return (ToString(), false);
            }
        }

        public override string ToString()
            => $"{Definition.ClassName}{{{string.Join(", ", Fields.Select(kv => $"{kv.Key}: {kv.Value}"))}}}";
    }
}
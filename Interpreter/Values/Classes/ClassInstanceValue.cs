using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Classes
{
    public class ClassInstanceValue : RuntimeValue
    {
        public ClassTypeValue Definition { get; }
        public Dictionary<string, RuntimeValue> Fields { get; }
        public Dictionary<string, bool> FieldPublicity { get; }
        public Dictionary<string, TypeDescriptor?> FieldTypes { get; }

        public override RuntimeValueType Type => RuntimeValueType.ClassInstance;
        public override bool IsCopy => false;

        public ClassInstanceValue(ClassTypeValue definition)
            : this(definition, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal),
                        new Dictionary<string, bool>(StringComparer.Ordinal),
                        new Dictionary<string, TypeDescriptor?>(StringComparer.Ordinal))
        {
        }

        private ClassInstanceValue(
            ClassTypeValue definition,
            Dictionary<string, RuntimeValue> fields,
            Dictionary<string, bool> publicity,
            Dictionary<string, TypeDescriptor?> types)
        {
            Definition = definition;
            Fields = fields;
            FieldPublicity = publicity;
            FieldTypes = types;
        }

        public void SetField(string name, RuntimeValue value, bool isPublic, TypeDescriptor? fieldType = null)
        {
            Fields[name] = value.IsCopy ? value.Copy() : value;
            FieldPublicity[name] = isPublic;
            FieldTypes[name] = fieldType;
        }

        public bool HasField(string name) => Fields.ContainsKey(name);

        public bool IsFieldPublic(string name) => FieldPublicity.TryGetValue(name, out var p) && p;

        public TypeDescriptor? GetFieldType(string name)
            => FieldTypes.TryGetValue(name, out var t) ? t : null;

        public RuntimeValue GetField(string name)
        {
            var v = Fields[name];
            return v.IsCopy ? v.Copy() : v;
        }

        public void SetMember(string name, RuntimeValue value)
        {
            if (!Fields.ContainsKey(name))
                throw new KeyNotFoundException(name);

            Fields[name] = value.IsCopy ? value.Copy() : value;
        }

        public override RuntimeValue Copy()
        {
            // reference copy: stessa state, nuovo wrapper
            return new ClassInstanceValue(Definition, Fields, FieldPublicity, FieldTypes)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public override string ToString()
            => $"{Definition.ClassName}{{{string.Join(", ", Fields.Select(kv => $"{kv.Key}: {kv.Value}"))}}}";
    }
}
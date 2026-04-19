using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Structs
{
    public class StructInstanceValue : RuntimeValue
    {
        public StructTypeValue Definition { get; }
        public Dictionary<string, RuntimeValue> Fields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> FieldPublicity { get; } = new(StringComparer.Ordinal);

        public sealed override RuntimeValueType Type => RuntimeValueType.StructInstance;
        public sealed override bool IsCopy => true;

        public StructInstanceValue(StructTypeValue definition)
        {
            Definition = definition;
        }

        public void SetField(string name, RuntimeValue value, bool isPublic)
        {
            Fields[name] = value.IsCopy ? value.Copy() : value;
            FieldPublicity[name] = isPublic;
        }

        public bool HasField(string name) => Fields.ContainsKey(name);

        public bool IsFieldPublic(string name) => FieldPublicity.TryGetValue(name, out var p) && p;

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

        public sealed override RuntimeValue Copy()
        {
            var copy = new StructInstanceValue(Definition);
            foreach (var kv in Fields)
            {
                copy.Fields[kv.Key] = kv.Value.IsCopy ? kv.Value.Copy() : kv.Value;
                copy.FieldPublicity[kv.Key] = FieldPublicity.TryGetValue(kv.Key, out var p) && p;
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

                var result = boundMethod.Execute(new List<RuntimeValue>());
                
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
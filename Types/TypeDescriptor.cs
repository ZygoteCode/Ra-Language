using RaLanguage.Interpreter.Values;

namespace RaLanguage.Types
{
    public enum BuiltInType
    {
        Number,
        Boolean,
        String,
        Tuple,
        Map,
        Set,
        List,
        Any,
        Unknown
    }

    public class TypeDescriptor
    {
        public bool IsBuiltIn { get; }
        public BuiltInType BuiltIn { get; }
        public string? NamedType { get; }

        private TypeDescriptor(BuiltInType builtIn)
        {
            IsBuiltIn = true;
            BuiltIn = builtIn;
            NamedType = null;
        }

        private TypeDescriptor(string named)
        {
            IsBuiltIn = false;
            NamedType = named;
            BuiltIn = BuiltInType.Unknown;
        }

        public static TypeDescriptor FromBuiltIn(BuiltInType b) => new TypeDescriptor(b);
        public static TypeDescriptor FromName(string name) => new TypeDescriptor(name);

        public static TypeDescriptor Parse(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return FromBuiltIn(BuiltInType.Unknown);
            var t = typeName.Trim().ToLowerInvariant();
            return t switch
            {
                "number" => FromBuiltIn(BuiltInType.Number),
                "boolean" => FromBuiltIn(BuiltInType.Boolean),
                "bool" => FromBuiltIn(BuiltInType.Boolean),
                "string" => FromBuiltIn(BuiltInType.String),
                "tuple" => FromBuiltIn(BuiltInType.Tuple),
                "map" => FromBuiltIn(BuiltInType.Map),
                "set" => FromBuiltIn(BuiltInType.Set),
                "list" => FromBuiltIn(BuiltInType.List),
                "any" => FromBuiltIn(BuiltInType.Any),
                "auto" => FromBuiltIn(BuiltInType.Any),
                _ => FromName(typeName)
            };
        }

        public override string ToString()
        {
            if (IsBuiltIn) return BuiltIn.ToString().ToLowerInvariant();
            return NamedType ?? "unknown";
        }

        public static TypeDescriptor FromRuntimeValue(RuntimeValue v)
        {
            return v.Type switch
            {
                RuntimeValueType.Number => FromBuiltIn(BuiltInType.Number),
                RuntimeValueType.Boolean => FromBuiltIn(BuiltInType.Boolean),
                RuntimeValueType.String => FromBuiltIn(BuiltInType.String),
                RuntimeValueType.Tuple => FromBuiltIn(BuiltInType.Tuple),
                RuntimeValueType.Map => FromBuiltIn(BuiltInType.Map),
                RuntimeValueType.Set => FromBuiltIn(BuiltInType.Set),
                RuntimeValueType.List => FromBuiltIn(BuiltInType.List),
                _ => FromBuiltIn(BuiltInType.Any)
            };
        }
    }
}
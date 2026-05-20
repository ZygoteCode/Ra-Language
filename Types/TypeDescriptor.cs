namespace RaLanguage.Types
{
    public enum BuiltInType
    {
        Number,
        String,
        List,
        Function,
        BaseFunction,
        Null,
        Boolean,
        Set,
        Map,
        Tuple,
        Integer,
        Long,
        Float,
        Double,
        UnsignedInteger,
        UnsignedLong,
        Short,
        UnsignedShort,
        Int128,
        UnsignedInt128,
        Decimal,
        Byte,
        Any,
        Unknown
    }

    // Resolved at construction time so type checking does not pay for a
    // ToLowerInvariant() + switch-on-string per declaration site.
    public enum PrimitiveTypeKind : byte
    {
        None = 0,
        String,
        Int,
        Number,
        Long,
        Float,
        Double,
        UInt,
        ULong,
        Short,
        UShort,
        Int128,
        UInt128,
        Decimal,
        Byte,
        Bool
    }

    public class TypeDescriptor : IEquatable<TypeDescriptor>
    {
        public string Name { get; }
        public List<TypeDescriptor> GenericArgs { get; }
        public bool IsTypeParameter { get; }
        public string TypeParameterName { get; }
        public bool IsRefType { get; }
        public TypeDescriptor? RefElementType { get; }
        public PrimitiveTypeKind PrimitiveKind { get; }

        // Borrow-system extensions. Populated when ParseType reads `&T` / `&mut T` /
        // `&'a T`. Pure metadata: TypeSystem.IsAssignable still treats them as ref
        // types; the borrow checker is what enforces mutability and lifetime.
        public bool IsMutableRef { get; }
        public string? Lifetime { get; }

        public TypeDescriptor(string name, List<TypeDescriptor>? genericArgs = null, bool isRefType = false, TypeDescriptor? refElementType = null, bool isMutableRef = false, string? lifetime = null)
        {
            Name = name;
            GenericArgs = genericArgs ?? new List<TypeDescriptor>();
            IsTypeParameter = false;
            TypeParameterName = null;
            IsRefType = isRefType;
            RefElementType = refElementType;
            IsMutableRef = isMutableRef;
            Lifetime = lifetime;
            PrimitiveKind = ResolvePrimitive(name);
        }

        private TypeDescriptor(string typeParamName, bool isTypeParam)
        {
            TypeParameterName = typeParamName;
            IsTypeParameter = isTypeParam;
            Name = typeParamName;
            GenericArgs = new List<TypeDescriptor>();
            IsRefType = false;
            RefElementType = null;
            IsMutableRef = false;
            Lifetime = null;
            PrimitiveKind = PrimitiveTypeKind.None;
        }

        private static PrimitiveTypeKind ResolvePrimitive(string? name)
        {
            if (name == null) return PrimitiveTypeKind.None;
            // Case-insensitive resolution without allocating a lowered string.
            // ASCII fast path: every primitive keyword is ASCII letters/digits.
            switch (name.Length)
            {
                case 3:
                    if (EqualsIgnoreAsciiCase(name, "int")) return PrimitiveTypeKind.Int;
                    break;
                case 4:
                    if (EqualsIgnoreAsciiCase(name, "long")) return PrimitiveTypeKind.Long;
                    if (EqualsIgnoreAsciiCase(name, "uint")) return PrimitiveTypeKind.UInt;
                    if (EqualsIgnoreAsciiCase(name, "byte")) return PrimitiveTypeKind.Byte;
                    if (EqualsIgnoreAsciiCase(name, "bool")) return PrimitiveTypeKind.Bool;
                    break;
                case 5:
                    if (EqualsIgnoreAsciiCase(name, "float")) return PrimitiveTypeKind.Float;
                    if (EqualsIgnoreAsciiCase(name, "ulong")) return PrimitiveTypeKind.ULong;
                    if (EqualsIgnoreAsciiCase(name, "short")) return PrimitiveTypeKind.Short;
                    break;
                case 6:
                    if (EqualsIgnoreAsciiCase(name, "string")) return PrimitiveTypeKind.String;
                    if (EqualsIgnoreAsciiCase(name, "number")) return PrimitiveTypeKind.Number;
                    if (EqualsIgnoreAsciiCase(name, "double")) return PrimitiveTypeKind.Double;
                    if (EqualsIgnoreAsciiCase(name, "ushort")) return PrimitiveTypeKind.UShort;
                    if (EqualsIgnoreAsciiCase(name, "int128")) return PrimitiveTypeKind.Int128;
                    break;
                case 7:
                    if (EqualsIgnoreAsciiCase(name, "uint128")) return PrimitiveTypeKind.UInt128;
                    if (EqualsIgnoreAsciiCase(name, "decimal")) return PrimitiveTypeKind.Decimal;
                    break;
            }
            return PrimitiveTypeKind.None;
        }

        private static bool EqualsIgnoreAsciiCase(string a, string b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                char ca = a[i], cb = b[i];
                if (ca == cb) continue;
                if (ca >= 'A' && ca <= 'Z') ca = (char)(ca + 32);
                if (cb >= 'A' && cb <= 'Z') cb = (char)(cb + 32);
                if (ca != cb) return false;
            }
            return true;
        }

        public static TypeDescriptor TypeParameter(string name) => new TypeDescriptor(name, true);

        public static TypeDescriptor RefType(TypeDescriptor elementType, bool isMutable = false, string? lifetime = null)
        {
            string prefix = isMutable ? "&mut " : "&";
            string lifetimeBit = lifetime != null ? $"'{lifetime} " : string.Empty;
            return new TypeDescriptor(
                $"{prefix}{lifetimeBit}{elementType.Name}",
                elementType.GenericArgs,
                isRefType: true,
                refElementType: elementType,
                isMutableRef: isMutable,
                lifetime: lifetime);
        }

        public static TypeDescriptor Tuple(List<TypeDescriptor> elements)
        {
            return new TypeDescriptor("tuple", elements);
        }

        public bool IsTupleType => string.Equals(Name, "tuple", StringComparison.Ordinal) && GenericArgs.Count > 0;

        public override string ToString()
        {
            if (IsTypeParameter) return TypeParameterName;
            if (IsTupleType) return $"({string.Join(", ", GenericArgs.Select(a => a.ToString()))})";
            if (GenericArgs.Count == 0) return Name;
            return $"{Name}<{string.Join(", ", GenericArgs.Select(a => a.ToString()))}>";
        }

        public bool Equals(TypeDescriptor? other)
        {
            if (other == null) return false;
            if (IsTypeParameter && other.IsTypeParameter)
                return string.Equals(TypeParameterName, other.TypeParameterName, StringComparison.Ordinal);
            if (IsTypeParameter || other.IsTypeParameter) return false;
            if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) return false;
            if (GenericArgs.Count != other.GenericArgs.Count) return false;
            for (int i = 0; i < GenericArgs.Count; i++)
                if (!GenericArgs[i].Equals(other.GenericArgs[i])) return false;
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as TypeDescriptor);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (IsTypeParameter ? TypeParameterName : Name).GetHashCode();
                foreach (var a in GenericArgs) h = h * 31 + (a?.GetHashCode() ?? 0);
                return h;
            }
        }

        public TypeDescriptor Substitute(Dictionary<string, TypeDescriptor> bindings)
        {
            if (IsTypeParameter)
            {
                if (bindings.TryGetValue(TypeParameterName, out var bound)) return bound;
                return this;
            }
            if (IsRefType && RefElementType != null)
            {
                var substitutedElement = RefElementType.Substitute(bindings);
                return RefType(substitutedElement, IsMutableRef, Lifetime);
            }
            if (GenericArgs.Count == 0) return this;
            var substituted = GenericArgs.Select(a => a.Substitute(bindings)).ToList();
            return new TypeDescriptor(Name, substituted);
        }

        public bool ReferencesAnyTypeParameter(IEnumerable<string> paramNames)
        {
            var set = new HashSet<string>(paramNames, StringComparer.Ordinal);
            return ReferencesAny(set);
        }

        private bool ReferencesAny(HashSet<string> set)
        {
            if (IsTypeParameter) return set.Contains(TypeParameterName);
            if (RefElementType != null && RefElementType.ReferencesAny(set)) return true;
            foreach (var a in GenericArgs)
                if (a.ReferencesAny(set)) return true;
            return false;
        }

        public static TypeDescriptor Parse(string s)
        {
            var p = new _StringParser(s);
            return p.ParseType();
        }

        private class _StringParser
        {
            private readonly string _s;
            private int _i;
            public _StringParser(string s) { _s = s.Trim(); _i = 0; }
            public TypeDescriptor ParseType(HashSet<string>? genericParams = null)
            {
                var name = ParseIdentifier();

                if (Peek() == '<')
                {
                    Consume('<');
                    var args = new List<TypeDescriptor>();

                    while (true)
                    {
                        args.Add(ParseType(genericParams));
                        if (Peek() == ',')
                        {
                            Consume(',');
                            continue;
                        }
                        break;
                    }

                    Consume('>');
                    return new TypeDescriptor(name, args);
                }

                if (genericParams != null && genericParams.Contains(name))
                    return TypeDescriptor.TypeParameter(name);

                return new TypeDescriptor(name);
            }

            private char Peek() => _i < _s.Length ? _s[_i] : '\0';
            private void Consume(char c) { if (Peek() == c) { _i++; while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; } else throw new Exception($"Parse error, expected '{c}' at {_i}"); }
            private string ParseIdentifier()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
                var start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                var ident = _s.Substring(start, _i - start);
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
                return ident;
            }
        }
    }
}

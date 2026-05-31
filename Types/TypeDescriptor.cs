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

        // Precomputed `Name == "any"` tag. `any` is the single most common
        // early-out in every assignability / narrowing entry point, so we
        // resolve it once at construction and read a bool instead of running
        // an ordinal string compare on every check. Only the public nominal
        // ctor can yield it — union/fn/ref/tuple/type-parameter descriptors
        // are never the unconstrained `any`.
        public bool IsAny { get; }

        // Precomputed "this names a canonical lowercase scalar primitive" tag,
        // resolved CASE-SENSITIVELY (unlike PrimitiveKind, which is case-
        // insensitive for the benefit of `is`-test matching). IsAssignable's
        // primitive fast path gates on this so that capitalized names such as
        // `String` / `Int` keep their historical "opaque user type that accepts
        // anything" behavior (CLAUDE.md backward-compat) instead of being
        // reclassified as the lowercase primitive. When true, PrimitiveKind is
        // the exact kind for this descriptor.
        public bool IsScalarPrimitive { get; }

        // Borrow-system extensions. Populated when ParseType reads `&T` / `&mut T` /
        // `&'a T`. Pure metadata: TypeSystem.IsAssignable still treats them as ref
        // types; the borrow checker is what enforces mutability and lifetime.
        public bool IsMutableRef { get; }
        public string? Lifetime { get; }

        // Structural function-type metadata. Set when ParseType reads
        // `fn(P1, P2, ...) -> R`. The descriptor name is the literal "fn"
        // so existing nominal-name dispatch keeps working; the structural
        // payload below is what TypeSystem.IsAssignable consults for
        // variance-aware compatibility against any BaseFunctionValue.
        //
        //   FunctionParamTypes: declared parameter types, in order. `any`
        //     anywhere means "accept anything in this slot."
        //   FunctionReturnType: declared return type. Null or `any` for
        //     "any return type". `void` is normalised to `any` at parse
        //     time so `fn(int)` and `fn(int) -> void` describe the same
        //     shape.
        //   IsFunctionType: cheap tag-check the type system can fast-path.
        public bool IsFunctionType { get; }
        public List<TypeDescriptor>? FunctionParamTypes { get; }
        public TypeDescriptor? FunctionReturnType { get; }

        // Structural union-type metadata. A union descriptor names a set of
        // alternatives; a value matches the union iff it matches at least one
        // member. Construction flattens nested unions, deduplicates members,
        // and collapses singleton unions to the bare member (via the Union()
        // factory). Members are stored in first-seen order so diagnostics
        // remain stable and predictable.
        //
        //   IsUnionType: fast tag check the type system fast-paths against.
        //   UnionMembers: ordered, deduplicated list of alternatives; null on
        //     non-union descriptors. Never empty when IsUnionType is true.
        public bool IsUnionType { get; }
        public List<TypeDescriptor>? UnionMembers { get; }

        // Shared immutable empty generic-argument list. The descriptor's
        // GenericArgs list is never mutated anywhere in the codebase (verified),
        // so every zero-arg descriptor can point at the same instance instead
        // of allocating a fresh List<> per construction — and descriptors are
        // constructed very frequently (every parsed type annotation, every
        // GetDescriptorFromRuntimeValue, every Substitute rebuild).
        internal static readonly List<TypeDescriptor> EmptyArgs = new List<TypeDescriptor>(0);

        // Interned singletons for the bare scalar primitives + `any`. These are
        // immutable and carry no generic args, so the same instance is reused
        // everywhere a primitive descriptor is needed at runtime — chiefly
        // GetDescriptorFromRuntimeValue on the generic-inference path, which
        // previously newed a descriptor per argument per call.
        public static readonly TypeDescriptor Any = new TypeDescriptor("any");
        internal static readonly TypeDescriptor Number = new TypeDescriptor("number");
        internal static readonly TypeDescriptor String = new TypeDescriptor("string");
        internal static readonly TypeDescriptor Bool = new TypeDescriptor("bool");
        internal static readonly TypeDescriptor Int = new TypeDescriptor("int");
        internal static readonly TypeDescriptor Long = new TypeDescriptor("long");
        internal static readonly TypeDescriptor Float = new TypeDescriptor("float");
        internal static readonly TypeDescriptor Double = new TypeDescriptor("double");
        internal static readonly TypeDescriptor UInt = new TypeDescriptor("uint");
        internal static readonly TypeDescriptor ULong = new TypeDescriptor("ulong");
        internal static readonly TypeDescriptor Short = new TypeDescriptor("short");
        internal static readonly TypeDescriptor UShort = new TypeDescriptor("ushort");
        internal static readonly TypeDescriptor Int128T = new TypeDescriptor("int128");
        internal static readonly TypeDescriptor UInt128T = new TypeDescriptor("uint128");
        internal static readonly TypeDescriptor Decimal = new TypeDescriptor("decimal");
        internal static readonly TypeDescriptor Byte = new TypeDescriptor("byte");

        public TypeDescriptor(string name, List<TypeDescriptor>? genericArgs = null, bool isRefType = false, TypeDescriptor? refElementType = null, bool isMutableRef = false, string? lifetime = null)
        {
            Name = name;
            GenericArgs = genericArgs ?? EmptyArgs;
            IsTypeParameter = false;
            TypeParameterName = null;
            IsRefType = isRefType;
            RefElementType = refElementType;
            IsMutableRef = isMutableRef;
            Lifetime = lifetime;
            PrimitiveKind = ResolvePrimitive(name);
            IsAny = name is "any";
            // Canonical lowercase scalar primitive only — see the field doc.
            IsScalarPrimitive = PrimitiveKind != PrimitiveTypeKind.None && HasNoUpperAscii(name);
            IsFunctionType = false;
            FunctionParamTypes = null;
            FunctionReturnType = null;
            IsUnionType = false;
            UnionMembers = null;
        }

        private TypeDescriptor(List<TypeDescriptor> paramTypes, TypeDescriptor? returnType)
        {
            Name = "fn";
            GenericArgs = EmptyArgs;
            IsTypeParameter = false;
            TypeParameterName = null;
            IsRefType = false;
            RefElementType = null;
            IsMutableRef = false;
            Lifetime = null;
            PrimitiveKind = PrimitiveTypeKind.None;
            IsAny = false;
            IsScalarPrimitive = false;
            IsFunctionType = true;
            FunctionParamTypes = paramTypes ?? new List<TypeDescriptor>();
            FunctionReturnType = returnType;
            IsUnionType = false;
            UnionMembers = null;
        }

        public static TypeDescriptor FunctionType(List<TypeDescriptor> paramTypes, TypeDescriptor? returnType)
            => new TypeDescriptor(paramTypes, returnType);

        // Private union-only constructor. The Union() factory is the only
        // caller — it performs normalization (flatten, dedup, singleton
        // collapse) before deciding whether a real union descriptor is
        // warranted. By the time we get here, members has >= 2 entries and
        // is already in the desired order.
        private TypeDescriptor(List<TypeDescriptor> unionMembers)
        {
            Name = "union";
            GenericArgs = EmptyArgs;
            IsTypeParameter = false;
            TypeParameterName = null;
            IsRefType = false;
            RefElementType = null;
            IsMutableRef = false;
            Lifetime = null;
            PrimitiveKind = PrimitiveTypeKind.None;
            IsAny = false;
            IsScalarPrimitive = false;
            IsFunctionType = false;
            FunctionParamTypes = null;
            FunctionReturnType = null;
            IsUnionType = true;
            UnionMembers = unionMembers;
        }

        // Public factory: build a union from N member descriptors.
        //
        //   - Null / empty / single-member input is unwrapped to its only
        //     element (or `any` for the empty case — a union with no
        //     alternatives is the unconstrained type by construction).
        //   - Nested unions flatten one level at a time recursively so
        //     `(A | B) | C` collapses to `A | B | C`.
        //   - Members are deduplicated by structural Equals; first-seen
        //     order is preserved so diagnostics print in source order.
        //   - `any` short-circuits: a union containing `any` *is* `any`,
        //     because `any` already subsumes every alternative.
        public static TypeDescriptor Union(IEnumerable<TypeDescriptor> members)
        {
            if (members == null) return new TypeDescriptor("any");
            var flat = new List<TypeDescriptor>();
            foreach (var m in members)
            {
                if (m == null) continue;
                if (m.IsUnionType && m.UnionMembers != null)
                {
                    foreach (var inner in m.UnionMembers) AddUnique(flat, inner);
                }
                else
                {
                    AddUnique(flat, m);
                }
            }
            if (flat.Count == 0) return new TypeDescriptor("any");
            for (int i = 0; i < flat.Count; i++)
            {
                if (string.Equals(flat[i].Name, "any", StringComparison.Ordinal) && !flat[i].IsTypeParameter)
                    return new TypeDescriptor("any");
            }
            if (flat.Count == 1) return flat[0];
            return new TypeDescriptor(flat);
        }

        private static void AddUnique(List<TypeDescriptor> list, TypeDescriptor candidate)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Equals(candidate)) return;
            list.Add(candidate);
        }

        private TypeDescriptor(string typeParamName, bool isTypeParam)
        {
            TypeParameterName = typeParamName;
            IsTypeParameter = isTypeParam;
            Name = typeParamName;
            GenericArgs = EmptyArgs;
            IsRefType = false;
            RefElementType = null;
            IsMutableRef = false;
            Lifetime = null;
            PrimitiveKind = PrimitiveTypeKind.None;
            IsAny = false;
            IsScalarPrimitive = false;
            IsFunctionType = false;
            FunctionParamTypes = null;
            FunctionReturnType = null;
            IsUnionType = false;
            UnionMembers = null;
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

        // True when `name` contains no uppercase ASCII letter — i.e. it is the
        // canonical lowercase spelling of a primitive. Used to gate IsScalarPrimitive
        // case-sensitively while PrimitiveKind itself stays case-insensitive.
        private static bool HasNoUpperAscii(string? name)
        {
            if (name == null) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c >= 'A' && c <= 'Z') return false;
            }
            return true;
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
            if (IsUnionType && UnionMembers != null)
            {
                // Render members in their stored (first-seen) order so the
                // string output matches how the user wrote it. Function-typed
                // members get parenthesised so `fn() -> int | string` cannot
                // be misread as `fn() -> int` unioned with `string`.
                return string.Join(" | ", UnionMembers.Select(m => m.IsFunctionType ? $"({m})" : m.ToString()));
            }
            if (IsFunctionType)
            {
                var ps = FunctionParamTypes ?? new List<TypeDescriptor>();
                var paramsStr = string.Join(", ", ps.Select(p => p.ToString()));
                if (FunctionReturnType == null) return $"fn({paramsStr})";
                return $"fn({paramsStr}) -> {FunctionReturnType}";
            }
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
            // Union equality is set equality on the (already deduplicated)
            // member list — two unions are equal when each side's members
            // appear in the other, regardless of declaration order. This is
            // what makes `int | string` and `string | int` interchangeable
            // for type checks, generic unification, and exhaustiveness.
            if (IsUnionType || other.IsUnionType)
            {
                if (IsUnionType != other.IsUnionType) return false;
                var lm = UnionMembers!;
                var rm = other.UnionMembers!;
                if (lm.Count != rm.Count) return false;
                for (int i = 0; i < lm.Count; i++)
                {
                    bool found = false;
                    for (int j = 0; j < rm.Count; j++)
                    {
                        if (lm[i].Equals(rm[j])) { found = true; break; }
                    }
                    if (!found) return false;
                }
                return true;
            }
            if (IsFunctionType || other.IsFunctionType)
            {
                if (IsFunctionType != other.IsFunctionType) return false;
                var lp = FunctionParamTypes ?? new List<TypeDescriptor>();
                var rp = other.FunctionParamTypes ?? new List<TypeDescriptor>();
                if (lp.Count != rp.Count) return false;
                for (int i = 0; i < lp.Count; i++)
                    if (!lp[i].Equals(rp[i])) return false;
                if ((FunctionReturnType == null) != (other.FunctionReturnType == null)) return false;
                if (FunctionReturnType != null && !FunctionReturnType.Equals(other.FunctionReturnType!)) return false;
                return true;
            }
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
                if (IsUnionType && UnionMembers != null)
                {
                    // Order-independent hash so `int | string` and
                    // `string | int` hash identically (they Equals identically
                    // too — see set-equality logic in Equals). XOR is the
                    // canonical commutative combiner here.
                    int unionHash = 0;
                    foreach (var m in UnionMembers) unionHash ^= m?.GetHashCode() ?? 0;
                    return h ^ unionHash;
                }
                if (IsFunctionType)
                {
                    var ps = FunctionParamTypes;
                    if (ps != null) foreach (var p in ps) h = h * 31 + (p?.GetHashCode() ?? 0);
                    h = h * 31 + (FunctionReturnType?.GetHashCode() ?? 0);
                    return h;
                }
                foreach (var a in GenericArgs) h = h * 31 + (a?.GetHashCode() ?? 0);
                return h;
            }
        }

        public TypeDescriptor Substitute(Dictionary<string, TypeDescriptor> bindings)
        {
            // Empty binding set can never rewrite anything — identity. This is
            // a common call shape (callers that probe with no bound params) and
            // skipping it avoids rebuilding+reallocating composite descriptors.
            if (bindings.Count == 0) return this;

            if (IsTypeParameter)
            {
                if (bindings.TryGetValue(TypeParameterName, out var bound)) return bound;
                return this;
            }
            if (IsUnionType && UnionMembers != null)
            {
                // Substitute each member then re-normalize: a substitution
                // can collapse two distinct members to the same concrete
                // type (e.g. `T | U` with T=U=int becomes just `int`). Only
                // re-normalize (allocate) if a member actually changed.
                var um = UnionMembers;
                List<TypeDescriptor>? newMembers = null;
                for (int i = 0; i < um.Count; i++)
                {
                    var s = um[i].Substitute(bindings);
                    if (newMembers == null && !ReferenceEquals(s, um[i]))
                    {
                        newMembers = new List<TypeDescriptor>(um.Count);
                        for (int j = 0; j < i; j++) newMembers.Add(um[j]);
                    }
                    newMembers?.Add(s);
                }
                return newMembers == null ? this : Union(newMembers);
            }
            if (IsRefType && RefElementType != null)
            {
                var substitutedElement = RefElementType.Substitute(bindings);
                if (ReferenceEquals(substitutedElement, RefElementType)) return this;
                return RefType(substitutedElement, IsMutableRef, Lifetime);
            }
            if (IsFunctionType)
            {
                var ps = FunctionParamTypes ?? EmptyArgs;
                List<TypeDescriptor>? newParams = null;
                for (int i = 0; i < ps.Count; i++)
                {
                    var s = ps[i].Substitute(bindings);
                    if (newParams == null && !ReferenceEquals(s, ps[i]))
                    {
                        newParams = new List<TypeDescriptor>(ps.Count);
                        for (int j = 0; j < i; j++) newParams.Add(ps[j]);
                    }
                    newParams?.Add(s);
                }
                var newRet = FunctionReturnType?.Substitute(bindings);
                if (newParams == null && ReferenceEquals(newRet, FunctionReturnType)) return this;
                return FunctionType(newParams ?? ps, newRet);
            }
            if (GenericArgs.Count == 0) return this;
            var ga = GenericArgs;
            List<TypeDescriptor>? substituted = null;
            for (int i = 0; i < ga.Count; i++)
            {
                var s = ga[i].Substitute(bindings);
                if (substituted == null && !ReferenceEquals(s, ga[i]))
                {
                    substituted = new List<TypeDescriptor>(ga.Count);
                    for (int j = 0; j < i; j++) substituted.Add(ga[j]);
                }
                substituted?.Add(s);
            }
            return substituted == null ? this : new TypeDescriptor(Name, substituted);
        }

        public bool ReferencesAnyTypeParameter(IEnumerable<string> paramNames)
        {
            var set = new HashSet<string>(paramNames, StringComparer.Ordinal);
            return ReferencesAny(set);
        }

        private bool ReferencesAny(HashSet<string> set)
        {
            if (IsTypeParameter) return set.Contains(TypeParameterName);
            if (IsUnionType && UnionMembers != null)
            {
                foreach (var m in UnionMembers)
                    if (m.ReferencesAny(set)) return true;
                return false;
            }
            if (RefElementType != null && RefElementType.ReferencesAny(set)) return true;
            if (IsFunctionType)
            {
                if (FunctionParamTypes != null)
                    foreach (var p in FunctionParamTypes)
                        if (p.ReferencesAny(set)) return true;
                if (FunctionReturnType != null && FunctionReturnType.ReferencesAny(set)) return true;
                return false;
            }
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

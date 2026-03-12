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

    public class TypeDescriptor : IEquatable<TypeDescriptor>
    {
        public string Name { get; }
        public List<TypeDescriptor> GenericArgs { get; }
        public bool IsTypeParameter { get; }
        public string TypeParameterName { get; }

        public TypeDescriptor(string name, List<TypeDescriptor>? genericArgs = null)
        {
            Name = name;
            GenericArgs = genericArgs ?? new List<TypeDescriptor>();
            IsTypeParameter = false;
            TypeParameterName = null;
        }

        private TypeDescriptor(string typeParamName, bool isTypeParam)
        {
            TypeParameterName = typeParamName;
            IsTypeParameter = isTypeParam;
            Name = typeParamName;
            GenericArgs = new List<TypeDescriptor>();
        }

        public static TypeDescriptor TypeParameter(string name) => new TypeDescriptor(name, true);

        public override string ToString()
        {
            if (IsTypeParameter) return TypeParameterName;
            if (GenericArgs.Count == 0) return Name;
            return $"{Name}<{string.Join(", ", GenericArgs.Select(a => a.ToString()))}>";
        }

        public bool Equals(TypeDescriptor? other)
        {
            if (other == null) return false;
            if (IsTypeParameter || other.IsTypeParameter) return false;
            if (Name != other.Name) return false;
            if (GenericArgs.Count != other.GenericArgs.Count) return false;
            for (int i = 0; i < GenericArgs.Count; i++)
                if (!GenericArgs[i].Equals(other.GenericArgs[i])) return false;
            return true;
        }

        // substitute type parameters using a binding map
        public TypeDescriptor Substitute(Dictionary<string, TypeDescriptor> bindings)
        {
            if (IsTypeParameter)
            {
                if (bindings.TryGetValue(TypeParameterName, out var bound)) return bound;
                return this;
            }
            if (GenericArgs.Count == 0) return this;
            var substituted = GenericArgs.Select(a => a.Substitute(bindings)).ToList();
            return new TypeDescriptor(Name, substituted);
        }

        // Parse from a string like "list<number>" or "T"
        // This is a simple parser for convenience (used if parser passes whole string). Prefer token-based ParseType in Parser.
        public static TypeDescriptor Parse(string s)
        {
            var p = new _StringParser(s);
            return p.ParseType();
        }

        // small internal string parser
        private class _StringParser
        {
            private readonly string _s;
            private int _i;
            public _StringParser(string s) { _s = s.Trim(); _i = 0; }
            public TypeDescriptor ParseType()
            {
                var name = ParseIdentifier();
                if (Peek() == '<')
                {
                    Consume('<');
                    var args = new List<TypeDescriptor>();
                    while (true)
                    {
                        args.Add(ParseType());
                        if (Peek() == ',') { Consume(','); continue; }
                        break;
                    }
                    Consume('>');
                    return new TypeDescriptor(name, args);
                }
                // treat single uppercase identifier as type parameter (heuristic)
                if (!string.IsNullOrEmpty(name) && char.IsUpper(name[0]) && name.Length <= 5)
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
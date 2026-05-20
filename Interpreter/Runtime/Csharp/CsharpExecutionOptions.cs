using System;
using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.Runtime.Csharp
{
    /// <summary>
    /// Description of one inline <c>csharp { ... }</c> block: the source, the user-requested
    /// imports, the assembly references, and the declared return type. Used both as a cache key
    /// and as the input to <see cref="CsharpExecutor"/>.
    /// </summary>
    public sealed class CsharpExecutionOptions : IEquatable<CsharpExecutionOptions>
    {
        public string Source { get; }
        public IReadOnlyList<string> Usings { get; }
        public IReadOnlyList<string> References { get; }
        public string? ReturnType { get; }

        private readonly int _hash;

        public CsharpExecutionOptions(string source, IReadOnlyList<string> usings, IReadOnlyList<string> references, string? returnType)
        {
            Source = source ?? string.Empty;
            Usings = usings ?? Array.Empty<string>();
            References = references ?? Array.Empty<string>();
            ReturnType = string.IsNullOrEmpty(returnType) ? null : returnType;
            _hash = ComputeHash();
        }

        private int ComputeHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + Source.GetHashCode(StringComparison.Ordinal);
                h = h * 31 + (ReturnType?.GetHashCode(StringComparison.Ordinal) ?? 0);
                foreach (var u in Usings) h = h * 31 + u.GetHashCode(StringComparison.Ordinal);
                foreach (var r in References) h = h * 31 + r.GetHashCode(StringComparison.Ordinal);
                return h;
            }
        }

        public bool Equals(CsharpExecutionOptions? other)
        {
            if (other == null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (!string.Equals(Source, other.Source, StringComparison.Ordinal)) return false;
            if (!string.Equals(ReturnType, other.ReturnType, StringComparison.Ordinal)) return false;
            if (Usings.Count != other.Usings.Count) return false;
            if (References.Count != other.References.Count) return false;
            for (int i = 0; i < Usings.Count; i++)
                if (!string.Equals(Usings[i], other.Usings[i], StringComparison.Ordinal)) return false;
            for (int i = 0; i < References.Count; i++)
                if (!string.Equals(References[i], other.References[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is CsharpExecutionOptions o && Equals(o);

        public override int GetHashCode() => _hash;

        public string ToCacheDescription()
        {
            var sb = new StringBuilder();
            sb.Append(Source);
            sb.Append("|return=");
            sb.Append(ReturnType ?? "<inferred>");
            sb.Append("|using=");
            for (int i = 0; i < Usings.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Usings[i]); }
            sb.Append("|ref=");
            for (int i = 0; i < References.Count; i++) { if (i > 0) sb.Append(','); sb.Append(References[i]); }
            return sb.ToString();
        }
    }
}

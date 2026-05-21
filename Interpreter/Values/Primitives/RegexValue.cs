using System.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Values.Primitives
{
    // Immutable runtime wrapper around `System.Text.RegularExpressions.Regex`.
    //
    // RegexValue is the type behind every regex literal `re"..."flags` and
    // every successful `regex(pattern, flags)` builtin call. The .NET regex
    // engine runs in interpreted mode (no Reflection.Emit, no source
    // generators) so RegexValue stays NativeAOT-clean.
    //
    // Pattern compilation is centralised through `Compile`, which consults a
    // process-wide LRU-bounded cache: identical (pattern, options) pairs are
    // compiled exactly once for the lifetime of the process. The cache is
    // also where the `regex(...)` builtin gains its constant-folding-like
    // amortisation for non-literal patterns built from string concatenation.
    public sealed class RegexValue : RuntimeValue
    {
        public string Pattern { get; }
        public string Flags { get; }
        public RegexOptions Options { get; }
        public Regex Regex { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.Regex;
        public sealed override bool IsCopy => true;

        public RegexValue(string pattern, string flags, RegexOptions options, Regex regex)
        {
            Pattern = pattern ?? string.Empty;
            Flags = flags ?? string.Empty;
            Options = options;
            Regex = regex;
        }

        public sealed override RuntimeValue Copy() => this;
        public sealed override bool IsTrue() => true;
        public sealed override string ToString() => Flags.Length == 0
            ? $"re\"{Pattern}\""
            : $"re\"{Pattern}\"{Flags}";

        // -------------------------------------------------------------------
        // Compile cache. Keyed by (pattern, options) tuple. Concurrency-safe
        // even though the interpreter is single-threaded, because the same
        // RegexValue instance can be observed by spawn / async fibers that
        // race on first compile. Cache is bounded to keep memory pressure
        // predictable; oldest entries are dropped on overflow.
        // -------------------------------------------------------------------

        private const int MaxCachedEntries = 256;
        private static readonly ConcurrentDictionary<CacheKey, Regex> _cache = new();
        private static readonly object _trimLock = new();

        public static Regex Compile(string pattern, RegexOptions options)
        {
            var key = new CacheKey(pattern, options);
            if (_cache.TryGetValue(key, out var existing)) return existing;

            // Interpreted engine. NEVER add RegexOptions.Compiled here — that
            // path uses Reflection.Emit on full .NET and is incompatible with
            // NativeAOT. Default backtracking matcher is the only AOT-safe
            // option until S.T.RegularExpressions ships a source generator
            // we can opt into.
            var regex = new Regex(pattern, options, TimeSpan.FromSeconds(5));

            if (_cache.Count >= MaxCachedEntries)
            {
                lock (_trimLock)
                {
                    if (_cache.Count >= MaxCachedEntries)
                    {
                        foreach (var k in _cache.Keys)
                        {
                            _cache.TryRemove(k, out _);
                            if (_cache.Count < MaxCachedEntries / 2) break;
                        }
                    }
                }
            }

            _cache[key] = regex;
            return regex;
        }

        public static RegexOptions ParseFlags(string flags)
        {
            var options = RegexOptions.CultureInvariant;
            if (string.IsNullOrEmpty(flags)) return options;
            foreach (var c in flags)
            {
                switch (c)
                {
                    case 'i': case 'I': options |= RegexOptions.IgnoreCase; break;
                    case 'm': case 'M': options |= RegexOptions.Multiline; break;
                    case 's': case 'S': options |= RegexOptions.Singleline; break;
                    case 'x': case 'X': options |= RegexOptions.IgnorePatternWhitespace; break;
                    case 'n': case 'N': options |= RegexOptions.ExplicitCapture; break;
                    default:
                        throw new ArgumentException($"unknown regex flag '{c}'; valid flags are i, m, s, x, n");
                }
            }
            return options;
        }

        public static int CacheCount => _cache.Count;
        public static void ClearCache() => _cache.Clear();

        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            public readonly string Pattern;
            public readonly RegexOptions Options;
            public CacheKey(string p, RegexOptions o) { Pattern = p; Options = o; }
            public bool Equals(CacheKey other) =>
                Options == other.Options &&
                string.Equals(Pattern, other.Pattern, StringComparison.Ordinal);
            public override bool Equals(object? obj) => obj is CacheKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(Pattern, (int)Options);
        }
    }
}

using System.Text.RegularExpressions;
using RaLanguage.Interpreter.Runtime;

namespace RaLanguage.Interpreter.Values.Primitives
{
    // Wraps a `System.Text.RegularExpressions.Match` together with the source
    // string the match was produced against. Holding on to the source gives
    // builtins everything they need without leaking the .NET Match object to
    // user code, and lets the value's truthiness reflect Match.Success — so
    // `if re_match(pat, s) { ... }` reads naturally.
    public sealed class MatchValue : RuntimeValue
    {
        public Match Match { get; }
        public string Source { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.RegexMatch;
        public sealed override bool IsCopy => true;

        public MatchValue(Match match, string source)
        {
            Match = match;
            Source = source ?? string.Empty;
        }

        public sealed override RuntimeValue Copy() => this;
        public sealed override bool IsTrue() => Match.Success;
        public sealed override string ToString() => Match.Success ? Match.Value : "";
    }
}

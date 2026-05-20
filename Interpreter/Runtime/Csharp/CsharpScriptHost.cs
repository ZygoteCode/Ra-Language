using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Csharp
{
    /// <summary>
    /// Globals object exposed to every compiled <c>csharp { ... }</c> script. The Roslyn scripting
    /// engine surfaces every public member of this class as a top-level identifier inside the
    /// script body, which lets the inline C# source reach back into the host for values that
    /// cannot be expressed as a static literal (complex objects, lists, foreign handles, …).
    ///
    /// Kept deliberately small: the inline subsystem favours literal substitution for primitives,
    /// and only falls back to <c>Ra.Get("name")</c> when the value is unsuitable for inlining.
    /// </summary>
    public sealed class CsharpScriptHost
    {
        public IDictionary<string, object?> Vars { get; }

        public CsharpScriptHost(IDictionary<string, object?> vars)
        {
            Vars = vars;
        }

        public object? Get(string name)
        {
            if (Vars == null) return null;
            return Vars.TryGetValue(name, out var v) ? v : null;
        }

        public T GetAs<T>(string name)
        {
            var v = Get(name);
            if (v is T t) return t;
            if (v == null) return default!;
            return (T)System.Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        public bool Has(string name) => Vars != null && Vars.ContainsKey(name);
    }
}

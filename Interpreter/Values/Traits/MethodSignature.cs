using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Values.Traits
{
    public static class MethodSignature
    {
        public static string NameOf(ICallableMethodDefinition m)
            => m.NameTok?.Value?.ToString() ?? "";

        public static bool MatchesSignature(ICallableMethodDefinition a, ICallableMethodDefinition b)
        {
            if (!string.Equals(NameOf(a), NameOf(b), StringComparison.Ordinal))
                return false;

            if (a.HasVarArgs != b.HasVarArgs)
                return false;

            if (a.ArgTypes.Count != b.ArgTypes.Count)
                return false;

            for (int i = 0; i < a.ArgTypes.Count; i++)
            {
                var ta = a.ArgTypes[i]?.ToString() ?? "";
                var tb = b.ArgTypes[i]?.ToString() ?? "";
                if (!string.Equals(ta, tb, StringComparison.Ordinal))
                    return false;
            }

            if (a.HasVarArgs)
            {
                var va = a.VarArgType?.ToString() ?? "";
                var vb = b.VarArgType?.ToString() ?? "";
                if (!string.Equals(va, vb, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }
}
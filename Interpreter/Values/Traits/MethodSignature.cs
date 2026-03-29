using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Values.Traits
{
    public static class MethodSignature
    {
        public static string NameOf(ICallableMethodDefinition m)
            => m.NameTok?.Value?.ToString() ?? "";

        public static string KeyOf(ICallableMethodDefinition m)
        {
            var args = string.Join(",", m.ArgTypes.Select(t => t?.ToString() ?? "_"));
            return $"{NameOf(m)}({args})|var:{m.HasVarArgs}|varg:{m.VarArgType?.ToString() ?? "_"}|ret:{m.ReturnType?.ToString() ?? "_"}";
        }

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
                var x = a.ArgTypes[i]?.ToString() ?? "";
                var y = b.ArgTypes[i]?.ToString() ?? "";
                if (!string.Equals(x, y, StringComparison.Ordinal))
                    return false;
            }

            var aVar = a.VarArgType?.ToString() ?? "";
            var bVar = b.VarArgType?.ToString() ?? "";
            if (!string.Equals(aVar, bVar, StringComparison.Ordinal))
                return false;

            var aRet = a.ReturnType?.ToString() ?? "";
            var bRet = b.ReturnType?.ToString() ?? "";
            if (!string.Equals(aRet, bRet, StringComparison.Ordinal))
                return false;

            return true;
        }
    }
}
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Classes
{
    public static class CallableBinder
    {
        public static bool CanBind(
            Context context,
            FunctionDefinitionNode fn,
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs)
        {
            var argNames = fn.ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList();
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            int formalCount = argNames.Count;

            if (!fn.HasVarArgs && positionalArgs.Count > formalCount)
                return false;

            for (int i = 0; i < positionalArgs.Count && i < formalCount; i++)
                assigned.Add(argNames[i]);

            if (fn.HasVarArgs)
            {
                for (int i = formalCount; i < positionalArgs.Count; i++)
                {

                }
            }

            foreach (var kv in namedArgs)
            {
                if (fn.HasVarArgs && fn.VarArgNameTok != null &&
                    string.Equals(kv.Key, fn.VarArgNameTok.Value.ToString(), StringComparison.Ordinal))
                {
                    if (kv.Value.Type != RuntimeValueType.List)
                        return false;
                    continue;
                }

                if (!argNames.Contains(kv.Key, StringComparer.Ordinal))
                    return false;

                if (!assigned.Add(kv.Key))
                    return false;
            }

            for (int i = 0; i < formalCount; i++)
            {
                if (assigned.Contains(argNames[i]))
                    continue;

                if (i >= fn.ParamDefaults.Count || fn.ParamDefaults[i] == null)
                    return false;
            }

            for (int i = 0; i < formalCount; i++)
            {
                RuntimeValue? actual = null;
                string argName = argNames[i];

                if (i < positionalArgs.Count)
                    actual = positionalArgs[i];
                else if (namedArgs.TryGetValue(argName, out var namedActual))
                    actual = namedActual;

                if (actual == null)
                    continue;

                var expected = i < fn.ArgTypes.Count ? fn.ArgTypes[i] : null;
                if (expected != null && !TypeSystem.IsAssignable(context, expected, actual))
                    return false;
            }

            if (fn.HasVarArgs && fn.VarArgType != null)
            {
                for (int i = formalCount; i < positionalArgs.Count; i++)
                {
                    if (!TypeSystem.IsAssignable(context, fn.VarArgType, positionalArgs[i]))
                        return false;
                }
            }

            return true;
        }
    }
}
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Structs
{
    public static class StructBinder
    {
        public static bool CanBind(
            List<string> argNames,
            List<TypeDescriptor?> argTypes,
            List<AstNode?> paramDefaults,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType,
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs)
        {
            var formalCount = argNames.Count;
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            int positionalIndex = 0;

            for (; positionalIndex < positionalArgs.Count; positionalIndex++)
            {
                if (positionalIndex < formalCount)
                {
                    assigned.Add(argNames[positionalIndex]);
                }
                else
                {
                    if (!hasVarArgs)
                        return false;
                }
            }

            foreach (var kv in namedArgs)
            {
                if (hasVarArgs && varArgNameTok != null && string.Equals(kv.Key, varArgNameTok.Value.ToString(), StringComparison.Ordinal))
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

                if (i >= paramDefaults.Count || paramDefaults[i] == null)
                    return false;
            }

            return true;
        }
    }
}
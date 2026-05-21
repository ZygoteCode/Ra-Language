using RaLanguage.Parser.Nodes.Interfaces;
using System.Threading.Tasks;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Values.Interfaces
{
    public static class InterfaceCompatibility
    {
        public static bool AreCompatible(FunctionDefinitionNode actual, InterfaceMethodSignatureNode expected)
        {
            var actualName = actual.VarNameTok?.Value?.ToString() ?? "";
            var expectedName = expected.NameTok.Value?.ToString() ?? "";

            if (!string.Equals(actualName, expectedName, StringComparison.Ordinal))
                return false;

            if (actual.ArgTypes.Count != expected.ArgTypes.Count)
                return false;

            for (int i = 0; i < expected.ArgTypes.Count; i++)
            {
                var expectedType = expected.ArgTypes[i];
                var actualType = i < actual.ArgTypes.Count ? actual.ArgTypes[i] : null;

                if (expectedType == null)
                    continue;

                if (actualType == null)
                    return false;

                if (!string.Equals(expectedType.ToString(), actualType.ToString(), StringComparison.Ordinal))
                    return false;
            }

            if (expected.ReturnType != null)
            {
                if (actual.ReturnType == null)
                    return false;

                if (!string.Equals(expected.ReturnType.ToString(), actual.ReturnType.ToString(), StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }
}
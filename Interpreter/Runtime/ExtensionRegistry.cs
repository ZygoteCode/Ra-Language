using RaLanguage.Interpreter.Values;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    public class ExtensionRegistry
    {
        private readonly Dictionary<string, List<FunctionDefinitionNode>> _methods = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, List<FunctionDefinitionNode>> AllMethods => _methods;

        public void Register(string targetTypeName, FunctionDefinitionNode method)
        {
            if (!_methods.TryGetValue(targetTypeName, out var list))
            {
                list = new List<FunctionDefinitionNode>();
                _methods[targetTypeName] = list;
            }

            list.Add(method);
        }

        public List<FunctionDefinitionNode> Resolve(RuntimeValue receiver, string memberName)
        {
            var result = new List<FunctionDefinitionNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var typeKey in GetCandidateTypeKeys(receiver))
            {
                if (!_methods.TryGetValue(typeKey, out var list))
                    continue;

                foreach (var method in list)
                {
                    var mName = method.VarNameTok?.Value?.ToString() ?? "";
                    if (!string.Equals(mName, memberName, StringComparison.Ordinal))
                        continue;

                    var sig = MethodSignature.KeyOf(method);
                    if (seen.Add(sig))
                        result.Add(method);
                }
            }

            return result;
        }

        private IEnumerable<string> GetCandidateTypeKeys(RuntimeValue receiver)
        {
            if (receiver.Type == RuntimeValueType.ClassInstance)
            {
                var current = ((ClassInstanceValue)receiver).Definition;
                while (current != null)
                {
                    yield return current.ClassName;
                    current = current.BaseClass;
                }

                yield break;
            }

            if (receiver.Type == RuntimeValueType.StructInstance)
            {
                yield return ((StructInstanceValue)receiver).Definition.StructName;
                yield break;
            }

            if (receiver.Type == RuntimeValueType.Enum || receiver.Type == RuntimeValueType.EnumType)
            {
                yield return TypeSystem.GetExtensionTargetName(receiver);
                yield break;
            }

            yield return TypeSystem.GetExtensionTargetName(receiver);
        }
    }
}
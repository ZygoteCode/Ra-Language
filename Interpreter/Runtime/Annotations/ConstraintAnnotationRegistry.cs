using System.Threading.Tasks;
using System.Collections.Generic;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public static class ConstraintAnnotationRegistry
    {
        private static readonly Dictionary<string, System.Func<TypeDescriptor, bool>> _checks = new()
        {
            ["numeric"] = IsNumeric,
            ["integer"] = IsInteger,
            ["stringlike"] = IsStringlike,
            ["orderable"] = IsOrderable,
            ["hashable"] = IsHashable,
            ["collection"] = IsCollection,
        };

        public static bool IsConstraintAnnotation(string name) => _checks.ContainsKey(name);

        public static bool IsSatisfied(string constraintName, TypeDescriptor boundType)
        {
            if (boundType == null) return false;
            if (!_checks.TryGetValue(constraintName, out var fn)) return false;
            return fn(boundType);
        }

        public static void Register(string name, System.Func<TypeDescriptor, bool> check)
            => _checks[name] = check;

        public static IEnumerable<string> All => _checks.Keys;

        private static bool IsNumeric(TypeDescriptor td)
        {
            return td.Name is "int" or "long" or "short" or "byte" or "int128"
                or "uint" or "ulong" or "ushort" or "uint128"
                or "float" or "double" or "decimal" or "number";
        }

        private static bool IsInteger(TypeDescriptor td)
        {
            return td.Name is "int" or "long" or "short" or "byte" or "int128"
                or "uint" or "ulong" or "ushort" or "uint128";
        }

        private static bool IsStringlike(TypeDescriptor td)
        {
            return td.Name is "string" or "char";
        }

        private static bool IsOrderable(TypeDescriptor td)
        {
            return IsNumeric(td) || IsStringlike(td);
        }

        private static bool IsHashable(TypeDescriptor td)
        {
            return IsNumeric(td) || IsStringlike(td) || td.Name == "bool";
        }

        private static bool IsCollection(TypeDescriptor td)
        {
            return td.Name is "list" or "set" or "map" or "tuple";
        }
    }
}

using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public static class MetadataKeyResolver
    {
        public static System.Func<string, string?> ForContext(Context ctx)
        {
            return key => ResolveParentKey(key, ctx);
        }

        public static string? ResolveParentKey(string key, Context ctx)
        {
            int colonIdx = key.IndexOf(':');
            if (colonIdx <= 0 || colonIdx == key.Length - 1) return null;

            var kind = key.Substring(0, colonIdx);
            var rest = key.Substring(colonIdx + 1);

            int dotIdx = rest.IndexOf('.');
            string? owner = null;
            string name;
            if (dotIdx > 0)
            {
                owner = rest.Substring(0, dotIdx);
                name = rest.Substring(dotIdx + 1);
            }
            else
            {
                name = rest;
            }

            if (kind == "class")
            {
                var cls = ctx.SymbolTable.Get(name) as ClassTypeValue;
                if (cls?.BaseClass != null) return $"class:{cls.BaseClass.ClassName}";
                return null;
            }

            if (owner != null && IsClassMemberKind(kind))
            {
                int firstSegEnd = owner.IndexOf('.');
                string ownerClass = firstSegEnd >= 0 ? owner.Substring(0, firstSegEnd) : owner;
                var cls = ctx.SymbolTable.Get(ownerClass) as ClassTypeValue;
                if (cls?.BaseClass != null)
                {
                    var newOwner = firstSegEnd >= 0
                        ? cls.BaseClass.ClassName + owner.Substring(firstSegEnd)
                        : cls.BaseClass.ClassName;
                    return $"{kind}:{newOwner}.{name}";
                }
                return null;
            }

            return null;
        }

        private static bool IsClassMemberKind(string kind)
            => kind == "method"
            || kind == "field"
            || kind == "static_field"
            || kind == "ctor"
            || kind == "op"
            || kind == "param";
    }
}

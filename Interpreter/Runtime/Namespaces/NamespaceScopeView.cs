using RaLanguage.Interpreter.Values;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Namespaces
{
    public sealed class NamespaceScopeView : SymbolTable
    {
        public NamespaceSymbolTable Target { get; }

        public NamespaceScopeView(NamespaceSymbolTable target, SymbolTable? parent)
            : base(target.LocalDict, parent)
        {
            Target = target;
        }

        public override void Set(
            string name,
            RuntimeValue value,
            bool isLet = false,
            TypeDescriptor? declaredType = null,
            bool isStaticallyTyped = false,
            bool isPublic = true)
        {
            if (LocalDict.TryGetValue(name, out var existing))
            {
                existing.Value = value;
                existing.IsLet = isLet;
                existing.DeclaredType = declaredType;
                existing.IsStaticallyTyped = isStaticallyTyped;
                existing.IsPublic = isPublic;
                return;
            }

            LocalDict[name] = new SymbolEntry(value, isLet, isPublic, declaredType, isStaticallyTyped);
        }

        public override void Remove(string name)
        {
            LocalDict.Remove(name);
        }
    }
}

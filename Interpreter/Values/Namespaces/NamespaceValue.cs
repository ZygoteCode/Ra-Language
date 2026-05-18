using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Namespaces;

namespace RaLanguage.Interpreter.Values.Namespaces
{
    public sealed class NamespaceValue : RuntimeValue
    {
        public string Name { get; }
        public string QualifiedName { get; }
        public NamespaceValue? ParentNamespace { get; }
        public NamespaceSymbolTable Members { get; }
        public bool IsRoot => ParentNamespace == null;

        public override RuntimeValueType Type => RuntimeValueType.Namespace;
        public override bool IsCopy => false;

        public NamespaceValue(string name, NamespaceValue? parent)
        {
            Name = name;
            ParentNamespace = parent;
            QualifiedName = parent == null || parent.IsRoot
                ? name
                : parent.QualifiedName + "." + name;
            Members = new NamespaceSymbolTable(this);
        }

        public NamespaceValue? GetChildNamespace(string name)
        {
            var entry = Members.GetLocalEntry(name);
            if (entry?.Value is NamespaceValue ns) return ns;
            return null;
        }

        public NamespaceValue GetOrCreateChild(string name)
        {
            var existing = GetChildNamespace(name);
            if (existing != null) return existing;

            var child = new NamespaceValue(name, this);
            Members.SetLocal(name, new SymbolEntry(child, isLet: false, isPublic: true));
            return child;
        }

        public override RuntimeValue Copy() => this;

        public override string ToString() =>
            IsRoot ? "<global namespace>" : $"<namespace '{QualifiedName}'>";
    }
}

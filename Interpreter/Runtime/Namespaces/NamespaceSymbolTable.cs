using RaLanguage.Interpreter.Values.Namespaces;

namespace RaLanguage.Interpreter.Runtime.Namespaces
{
    public sealed class NamespaceSymbolTable : SymbolTable
    {
        public NamespaceValue Owner { get; }

        public NamespaceSymbolTable(NamespaceValue owner) : base(parent: null)
        {
            Owner = owner;
        }

        public SymbolEntry? GetLocalEntry(string name)
        {
            return LocalDict.TryGetValue(name, out var e) ? e : null;
        }

        public void SetLocal(string name, SymbolEntry entry)
        {
            LocalDict[name] = entry;
        }

        public IEnumerable<KeyValuePair<string, SymbolEntry>> EnumerateLocal()
        {
            foreach (var kvp in LocalDict)
                yield return kvp;
        }

        public bool ContainsLocal(string name) => LocalDict.ContainsKey(name);
    }
}

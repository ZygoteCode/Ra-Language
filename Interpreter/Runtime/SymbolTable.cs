using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolTable
    {
        private Dictionary<string, SymbolEntry> _symbols = new();
        public SymbolTable? Parent { get; private set; }

        public SymbolTable(SymbolTable? parent = null)
        {
            Parent = parent;
        }

        protected SymbolTable(Dictionary<string, SymbolEntry> sharedSymbols, SymbolTable? parent)
        {
            _symbols = sharedSymbols;
            Parent = parent;
        }

        internal Dictionary<string, SymbolEntry> LocalDict => _symbols;

        public void SetParent(SymbolTable? parent)
        {
            Parent = parent;
        }

        public virtual RuntimeValue? Get(string name)
        {
            var entry = GetEntry(name);
            return entry?.Value;
        }

        public virtual SymbolEntry? GetEntry(string name)
        {
            // Iterative parent walk avoids virtual recursion on each scope hop. Hot path.
            SymbolTable? st = this;
            while (st != null)
            {
                if (st._symbols.TryGetValue(name, out var e)) return e;
                st = st.Parent;
            }
            return null;
        }

        public virtual void Set(string name, RuntimeValue value, bool isLet = false, TypeDescriptor? declaredType = null, bool isStaticallyTyped = false, bool isPublic = true)
        {
            SetWithDeclarationType(name, value, isLet, declaredType, isStaticallyTyped, isPublic, null);
        }

        public void SetWithDeclarationType(string name, RuntimeValue value, bool isLet, TypeDescriptor? declaredType, bool isStaticallyTyped, bool isPublic, VariableDeclarationType? declarationType)
        {
            // Single walk; resolves owner scope and writes once. Avoids the previous pattern
            // that re-indexed the dictionary five times per assignment.
            SymbolTable? st = this;
            while (st != null)
            {
                if (st._symbols.TryGetValue(name, out var existing))
                {
                    existing.Value = value;
                    existing.IsLet = isLet;
                    existing.DeclaredType = declaredType;
                    existing.IsStaticallyTyped = isStaticallyTyped;
                    existing.IsPublic = isPublic;
                    if (declarationType.HasValue) existing.DeclarationType = declarationType.Value;
                    return;
                }
                st = st.Parent;
            }

            var entry = new SymbolEntry(value, isLet, isPublic, declaredType, isStaticallyTyped,
                declarationType ?? VariableDeclarationType.VARIABLE);
            _symbols[name] = entry;
        }

        public virtual void Remove(string name)
        {
            SymbolTable? st = this;
            while (st != null)
            {
                if (st._symbols.Remove(name)) return;
                st = st.Parent;
            }
        }

        public void ApplyChangesFrom(SymbolTable? symbolTable)
        {
            if (symbolTable == null)
            {
                return;
            }

            foreach (var key in symbolTable.GetLocalKeys())
            {
                if (GetEntry(key) != null)
                {
                    var entry = symbolTable.GetEntry(key);
                    if (entry != null)
                    {
                        Set(key, entry.Value, entry.IsLet, entry.DeclaredType, entry.IsStaticallyTyped, entry.IsPublic);
                    }
                }
            }

            ApplyChangesFrom(symbolTable.Parent);
        }

        public IEnumerable<string> GetLocalKeys()
        {
            return _symbols.Keys.ToList();
        }

        public void Clear()
        {
            _symbols.Clear();
        }

        public void DetachParent()
        {
            Parent = null;
        }
    }
}
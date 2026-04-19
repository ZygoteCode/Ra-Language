using RaLanguage.Interpreter.Values;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolTable
    {
        private readonly Dictionary<string, SymbolEntry> _symbols = new();
        public SymbolTable? Parent { get; private set; }

        public SymbolTable(SymbolTable? parent = null)
        {
            Parent = parent;
        }

        public RuntimeValue? Get(string name)
        {
            var entry = GetEntry(name);
            return entry?.Value;
        }

        public SymbolEntry? GetEntry(string name)
        {
            if (_symbols.TryGetValue(name, out var e))
            {
                return e;
            }

            return Parent?.GetEntry(name);
        }

        public void Set(string name, RuntimeValue value, bool isLet = false, TypeDescriptor? declaredType = null, bool isStaticallyTyped = false, bool isPublic = true)
        {
            SymbolTable? st = this;
            SymbolTable? owner = null;
            while (st != null)
            {
                if (st._symbols.ContainsKey(name))
                {
                    owner = st;
                    break;
                }
                st = st.Parent;
            }

            if (owner != null)
            {
                owner._symbols[name].Value = value;
                owner._symbols[name].IsLet = isLet;
                owner._symbols[name].DeclaredType = declaredType;
                owner._symbols[name].IsStaticallyTyped = isStaticallyTyped;
                owner._symbols[name].IsPublic = isPublic;
            }
            else
            {
                _symbols[name] = new SymbolEntry(value, isLet, isPublic, declaredType, isStaticallyTyped);
            }
        }

        public void Remove(string name)
        {
            SymbolTable? st = this;
            SymbolTable? owner = null;
            while (st != null)
            {
                if (st._symbols.ContainsKey(name))
                {
                    owner = st;
                    break;
                }
                st = st.Parent;
            }

            if (owner != null)
            {
                var entry = owner._symbols[name];
                owner._symbols.Remove(name);
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
                var entry = symbolTable.GetEntry(key);
                if (entry != null)
                {
                    Set(key, entry.Value, entry.IsLet, entry.DeclaredType, entry.IsStaticallyTyped, entry.IsPublic);
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
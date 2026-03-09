using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolTable : IDisposable
    {
        private readonly Dictionary<string, SymbolEntry> _symbols = new();
        public SymbolTable? Parent { get; private set; }

        private bool _disposed;

        public SymbolTable(SymbolTable? parent = null)
        {
            Parent = parent;
            _disposed = false;
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

        public void Set(string name, RuntimeValue value, bool isLet = false)
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
            }
            else
            {
                _symbols[name] = new SymbolEntry(value, isLet);
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
                try { entry.Dispose(); } catch { }
                owner._symbols.Remove(name);
            }
        }

        public void ApplyChangesFrom(SymbolTable? symbolTable)
        {
            if (symbolTable == null)
            {
                return;
            }

            foreach (KeyValuePair<string, SymbolEntry> keyValuePair in _symbols)
            {
                RuntimeValue? value = symbolTable.Get(keyValuePair.Key);

                if (value == null)
                {
                    continue;
                }

                Set(keyValuePair.Key, value);
            }

            ApplyChangesFrom(symbolTable.Parent);
        }

        public IEnumerable<string> GetLocalKeys()
        {
            return _symbols.Keys.ToList();
        }

        public void Clear()
        {
            foreach (var kv in _symbols)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _symbols.Clear();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                foreach (var kv in _symbols)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
                _symbols.Clear();
            }

            _disposed = true;
        }

        ~SymbolTable()
        {
            Dispose(false);
        }

        public void DisposeRecursively()
        {
            Dispose();

            var p = Parent;
            Parent = null;
            while (p != null)
            {
                var next = p.Parent;
                try { p.Dispose(); } catch { }
                p = next;
            }
        }

        public void DetachParent()
        {
            Parent = null;
        }
    }
}
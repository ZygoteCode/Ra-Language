using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolTable
    {
        private readonly Dictionary<string, RuntimeValue> _symbols = new();
        public SymbolTable? Parent { get; }

        public SymbolTable(SymbolTable? parent = null)
        {
            Parent = parent;
        }

        public RuntimeValue? Get(string name)
        {
            if (_symbols.TryGetValue(name, out var val)) return val;
            return Parent?.Get(name);
        }

        public void Set(string name, RuntimeValue value)
        {
            _symbols[name] = value;
        }

        public void Remove(string name)
        {
            _symbols.Remove(name);
        }
    }
}
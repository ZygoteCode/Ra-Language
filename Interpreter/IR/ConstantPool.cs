using System.Collections.Generic;
using RaLanguage.Interpreter.Values;

namespace RaLanguage.Interpreter.IR
{
    // Per-function constant pool. Strings, regex literals, numeric literals
    // that don't fit a 16-bit immediate, and even nested RaFunctions (for
    // OP_CLOSURE) all live here. Index is u16 so the pool tops out at 65535
    // entries — adequate for any real Ra function in the test corpus.
    //
    // Interning is by reference identity for RuntimeValue: callers should
    // pre-intern via NumberNodeVisitor's CachedValue (already done) so two
    // syntactically identical literals share one entry.
    internal sealed class ConstantPool
    {
        private readonly List<RuntimeValue?> _items = new();

        public int Count => _items.Count;

        public ushort Add(RuntimeValue? v)
        {
            int idx = _items.Count;
            if (idx > ushort.MaxValue)
                throw new IrCompileException("constant pool overflow (>65535 entries)");
            _items.Add(v);
            return (ushort)idx;
        }

        public RuntimeValue?[] ToArray() => _items.ToArray();
    }

    // Identifier-name pool (for OP_LOAD_GLOBAL, OP_STORE_GLOBAL, OP_GET_MEMBER).
    // Strings are interned by ordinal-equality so the same identifier shares
    // one slot across the function.
    internal sealed class NamePool
    {
        private readonly List<string> _names = new();
        private readonly Dictionary<string, ushort> _index = new(System.StringComparer.Ordinal);

        public int Count => _names.Count;

        public ushort Add(string name)
        {
            if (_index.TryGetValue(name, out var existing)) return existing;
            int idx = _names.Count;
            if (idx > ushort.MaxValue)
                throw new IrCompileException("name pool overflow (>65535 entries)");
            _names.Add(name);
            _index[name] = (ushort)idx;
            return (ushort)idx;
        }

        public string[] ToArray() => _names.ToArray();
    }
}

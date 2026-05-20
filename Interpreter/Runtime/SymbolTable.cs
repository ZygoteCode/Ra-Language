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

        // Force-write into THIS scope without walking up. Required for parameter
        // binding and `var`/`let` declarations: a recursive call's `n=child` would
        // otherwise stomp the caller's `n=parent` because the standard Set walks up
        // looking for an existing entry. The previous behaviour broke any function
        // that recurses with a parameter name that also exists in an enclosing scope
        // (e.g. async fn fib(n)).
        public void SetLocal(string name, RuntimeValue value, bool isLet = false, TypeDescriptor? declaredType = null, bool isStaticallyTyped = false, bool isPublic = true)
        {
            SetLocalWithDeclarationType(name, value, isLet, declaredType, isStaticallyTyped, isPublic, null);
        }

        public void SetLocalWithDeclarationType(string name, RuntimeValue value, bool isLet, TypeDescriptor? declaredType, bool isStaticallyTyped, bool isPublic, VariableDeclarationType? declarationType)
        {
            if (_symbols.TryGetValue(name, out var existing))
            {
                existing.Value = value;
                existing.IsLet = isLet;
                existing.DeclaredType = declaredType;
                existing.IsStaticallyTyped = isStaticallyTyped;
                existing.IsPublic = isPublic;
                if (declarationType.HasValue) existing.DeclarationType = declarationType.Value;
                return;
            }
            _symbols[name] = new SymbolEntry(value, isLet, isPublic, declaredType, isStaticallyTyped,
                declarationType ?? VariableDeclarationType.VARIABLE);
        }

        // Like GetEntry but does NOT walk up. Used by `var` declaration to decide
        // whether a shadow is allowed in the current scope (legal) versus a true
        // redeclaration of a local (illegal).
        public SymbolEntry? GetLocalEntry(string name)
        {
            return _symbols.TryGetValue(name, out var e) ? e : null;
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

        // Assignment-only walk-up: find the nearest binding of `name` in this scope
        // or any ancestor and replace its Value. Returns false if no binding exists.
        // Unlike Set/SetWithDeclarationType, this never auto-declares and never
        // touches metadata flags (IsLet, IsPublic, DeclarationType, ...). Use this
        // for `x = ...` assignment; use SetLocal/Set for declaration.
        public bool TryAssign(string name, RuntimeValue value)
        {
            SymbolTable? st = this;
            while (st != null)
            {
                if (st._symbols.TryGetValue(name, out var existing))
                {
                    existing.Value = value;
                    return true;
                }
                st = st.Parent;
            }
            return false;
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
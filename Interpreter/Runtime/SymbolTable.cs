using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolTable
    {
        private Dictionary<string, SymbolEntry> _symbols = new();
        public SymbolTable? Parent { get; private set; }

        // Bumps whenever the local key set changes (add or remove). Pure value
        // mutations on an existing SymbolEntry (Value, IsLet, ...) do NOT bump,
        // because the SymbolEntry pointer remains valid — and that's exactly what
        // the AST inline cache wants to detect: "is my cached pointer still bound
        // to this name in this table?" If the generation matches, the answer is
        // yes; the pointer is reusable without a dict lookup.
        //
        // Per-table only. Parent-chain shadowing is not tracked here; the cache
        // policy (see VariableAccessNodeVisitor) only memoises hits found in the
        // local dict, which sidesteps the cross-scope invalidation problem.
        private int _localGeneration;
        public int LocalGeneration
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _localGeneration;
        }

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
                if (declarationType.HasValue)
                {
                    existing.DeclarationType = declarationType.Value;
                    existing.ApplyDeclarationTypeDefaults();
                }
                return;
            }
            _symbols[name] = new SymbolEntry(value, isLet, isPublic, declaredType, isStaticallyTyped,
                declarationType ?? VariableDeclarationType.VARIABLE);
            _localGeneration++;
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
                    if (declarationType.HasValue)
                    {
                        existing.DeclarationType = declarationType.Value;
                        existing.ApplyDeclarationTypeDefaults();
                    }
                    return;
                }
                st = st.Parent;
            }

            var entry = new SymbolEntry(value, isLet, isPublic, declaredType, isStaticallyTyped,
                declarationType ?? VariableDeclarationType.VARIABLE);
            _symbols[name] = entry;
            _localGeneration++;
        }

        public virtual void Remove(string name)
        {
            SymbolTable? st = this;
            while (st != null)
            {
                if (st._symbols.Remove(name))
                {
                    st._localGeneration++;
                    return;
                }
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
            ReleaseLocalBorrows();
            if (_symbols.Count > 0)
            {
                _symbols.Clear();
                _localGeneration++;
            }
        }

        public void DetachParent()
        {
            Parent = null;
        }

        // Walk only THIS scope's entries and release any BorrowValue they hold so the
        // source SymbolEntry's borrow counter is correctly decremented. Called by
        // ScopeNodeVisitor on scope exit, by loop visitors before Clear(), and by
        // function epilogue on return. Idempotent per-borrow.
        public void ReleaseLocalBorrows()
        {
            foreach (var kv in _symbols)
            {
                var entry = kv.Value;
                if (entry.Value is RaLanguage.Interpreter.Values.Primitives.BorrowValue bv && !bv.Released)
                {
                    bv.Release();
                }
            }
        }
    }
}
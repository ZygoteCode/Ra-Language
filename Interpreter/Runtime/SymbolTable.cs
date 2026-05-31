using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime
{
    public class SymbolTable
    {
        // PERF: every scope (function call, loop body, block) historically
        // allocated a backing dictionary up-front, even when it declared no
        // names at all (the common case once locals/params resolve to frame
        // slots). `_symbols` now starts pointing at a shared, never-mutated
        // empty sentinel; the FIRST real write swaps in a private dictionary
        // via Mutable(). Reads (TryGetValue / Count / enumerate / Remove of an
        // absent key) are correct against the empty sentinel unchanged. Any
        // sharing path (LocalDict, the shared-symbols ctor) materialises a real
        // dict first so the shared reference stays stable.
        private static readonly Dictionary<string, SymbolEntry> s_emptySymbols = new();
        private Dictionary<string, SymbolEntry> _symbols = s_emptySymbols;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Dictionary<string, SymbolEntry> Mutable()
        {
            if (ReferenceEquals(_symbols, s_emptySymbols)) _symbols = new();
            return _symbols;
        }

        public SymbolTable? Parent { get; private set; }

        // Optional slot-indexed view of the local entries. Populated only when
        // the resolver pipeline + a visitor opt in: declarations carry a
        // BindingId allocated by Interpreter.Pipeline.Resolver, and the visitor
        // can route hot accesses through GetEntryBySlot(offset) to bypass the
        // dictionary lookup entirely. The slot array stays null on tables that
        // never opt in, so unmodified visitors keep their existing fast path
        // (dict + inline cache via SymbolLookupCache) untouched.
        //
        // Invariants when non-null:
        //   * Slots[i] is either null (offset i not allocated in this table) or
        //     points to the same SymbolEntry as _symbols[name] for the name the
        //     resolver assigned to (frame_id, i).
        //   * The array grows monotonically; entries are never reordered.
        //   * The frame_id half of a BindingId is owned by the resolver and is
        //     not stored here — the runtime treats slots as "anonymous offsets
        //     into this table". Two tables can both own offset 0 without
        //     conflict: they belong to different BindingId.FrameIds.
        private List<SymbolEntry>? _slots;

        public SymbolEntry? GetEntryBySlot(int offset)
        {
            var slots = _slots;
            if (slots == null || (uint)offset >= (uint)slots.Count) return null;
            return slots[offset];
        }

        // Append-only slot registration. Returns the offset assigned. The
        // caller is responsible for keeping `name` and `entry` in agreement
        // with the dictionary; declaration-style call sites should already
        // hold an entry created by the standard SetLocal* path.
        public int RegisterSlot(SymbolEntry entry)
        {
            _slots ??= new List<SymbolEntry>();
            int offset = _slots.Count;
            _slots.Add(entry);
            return offset;
        }

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

        // Materialises a real dict: callers use this to SHARE the backing store
        // (namespace scope views) or to iterate-and-mutate, so the sentinel must
        // not leak here.
        internal Dictionary<string, SymbolEntry> LocalDict => Mutable();

        public void SetParent(SymbolTable? parent)
        {
            Parent = parent;
        }

        // PERF (direct-slot arg binding): optional frame-backing. A call scope
        // that binds its parameters straight into the VmFrame's SlotLocals
        // (skipping this table's dictionary) attaches the parameter names +
        // their frame-slot offsets + the live SlotLocals array here, so that
        // name-based lookups (reflection, bridged visitors, a stray helper)
        // still resolve a parameter by name. The local dictionary is still
        // consulted FIRST, so an explicit body shadow wins. null on ordinary
        // tables — a single null-check on the hot lookup path.
        private IReadOnlyList<string>? _frameParamNames;
        private int[]? _frameParamSlots;
        private SymbolEntry?[]? _frameSlots;

        public void AttachFrameParams(IReadOnlyList<string> paramNames, int[] paramSlots, SymbolEntry?[] frameSlots)
        {
            _frameParamNames = paramNames;
            _frameParamSlots = paramSlots;
            _frameSlots = frameSlots;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SymbolEntry? FrameParamEntry(string name)
        {
            var pn = _frameParamNames;
            if (pn == null) return null;
            int n = pn.Count;
            for (int i = 0; i < n; i++)
            {
                if (string.Equals(pn[i], name, System.StringComparison.Ordinal))
                {
                    var ps = _frameParamSlots;
                    var fs = _frameSlots;
                    if (ps != null && fs != null)
                    {
                        int slot = ps[i];
                        if ((uint)slot < (uint)fs.Length) return fs[slot];
                    }
                    return null;
                }
            }
            return null;
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
                if (st._frameParamNames != null)
                {
                    var fe = st.FrameParamEntry(name);
                    if (fe != null) return fe;
                }
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
            Mutable()[name] = new SymbolEntry(value, isLet, isPublic, declaredType, isStaticallyTyped,
                declarationType ?? VariableDeclarationType.VARIABLE);
            _localGeneration++;
        }

        // Like GetEntry but does NOT walk up. Used by `var` declaration to decide
        // whether a shadow is allowed in the current scope (legal) versus a true
        // redeclaration of a local (illegal).
        public SymbolEntry? GetLocalEntry(string name)
        {
            if (_symbols.TryGetValue(name, out var e)) return e;
            // A frame-bound parameter counts as a local of this scope (so the
            // redeclaration / shadow check sees it exactly as the dict did).
            return FrameParamEntry(name);
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
            Mutable()[name] = entry;
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
                if (st._frameParamNames != null)
                {
                    // Drop a frame-bound parameter by clearing its slot entry —
                    // a subsequent read then fails "not defined" as for any
                    // dropped binding.
                    var pn = st._frameParamNames;
                    for (int i = 0; i < pn.Count; i++)
                    {
                        if (string.Equals(pn[i], name, System.StringComparison.Ordinal))
                        {
                            var ps = st._frameParamSlots;
                            var fs = st._frameSlots;
                            if (ps != null && fs != null)
                            {
                                int slot = ps[i];
                                if ((uint)slot < (uint)fs.Length && fs[slot] != null)
                                {
                                    fs[slot] = null;
                                    st._localGeneration++;
                                    return;
                                }
                            }
                            break;
                        }
                    }
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
                if (st._frameParamNames != null)
                {
                    var fe = st.FrameParamEntry(name);
                    if (fe != null) { fe.Value = value; return true; }
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
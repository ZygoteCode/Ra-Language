using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ClassInstanceValue : RuntimeValue
    {
        public ClassTypeValue Definition { get; }
        public Dictionary<string, RuntimeValue> Fields { get; }
        public Dictionary<string, bool> FieldPublicity { get; }
        public Dictionary<string, TypeDescriptor?> FieldTypes { get; }
        public Dictionary<string, VariableDeclarationType> FieldDeclarationTypes { get; }
        public Dictionary<string, TypeDescriptor> GenericBindings { get; }

        // M38: shape-indexed slot array. Indexed by
        // Definition.GetFieldSlotIndex(name). Lazily resized when a field is
        // first assigned. The Dictionary above remains the ground truth for
        // code paths that need to iterate by name (reflection builtins,
        // annotations_of, ToString); the slot array is a parallel store
        // optimised for O(1) field reads from the IC-driven hot path. Both
        // stores are kept in sync by SetField / SetMember.
        public RuntimeValue?[] FieldSlots;

        // Tracks which lazy properties on this instance have already
        // been initialised. Allocated lazily on first lazy access so
        // instances of classes with no lazy properties pay zero
        // overhead.
        public HashSet<string>? LazyInitialized;

        // Names currently being initialised. Used to detect re-entrant
        // lazy access ("read inside its own initializer"). Same lazy-
        // allocation pattern as LazyInitialized.
        public HashSet<string>? LazyInitializing;

        // Per-instance event subscriber storage. Allocated lazily on
        // first subscribe so instances of classes with no live event
        // subscriptions pay zero overhead. Keyed by event name; values
        // are mutable subscriber lists, not snapshots.
        public Dictionary<string, RaLanguage.Interpreter.Runtime.Events.EventSubscriberList>? EventSubs;

        // Extension-field storage. Lazy-allocated on first write of
        // any ext-field. Indexed by the global slot returned by
        // `ExtensionFieldStorage.AllocateSlot`. Null means "this
        // instance has never been touched by an ext-field" — common
        // path pays zero allocation. See RA_EXTENSIONS_DESIGN.md §10.
        public RuntimeValue?[]? ExtFieldSlots;
        // Initialisation bitset paralleling ExtFieldSlots. Required to
        // tell "explicitly assigned null" from "never assigned", which
        // gates the let/final/const single-shot write rules and the
        // lazy default-value evaluation on first read.
        public ulong[]? ExtFieldInitBits;
        // Lazy-initialisation re-entrancy guard. A slot is "lazy
        // initialising" between the moment its default eval begins
        // and the moment the result is stored back. Reading the same
        // field from within its own default expression raises an
        // explicit error instead of looping forever.
        public ulong[]? ExtFieldLazyBits;

        public override RuntimeValueType Type => RuntimeValueType.ClassInstance;
        public override bool IsCopy => false;

        public ClassInstanceValue(ClassTypeValue definition)
            : this(
                definition,
                new Dictionary<string, RuntimeValue>(StringComparer.Ordinal),
                new Dictionary<string, bool>(StringComparer.Ordinal),
                new Dictionary<string, TypeDescriptor?>(StringComparer.Ordinal),
                new Dictionary<string, VariableDeclarationType>(),
                new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal))
        {
        }

        public ClassInstanceValue(ClassTypeValue definition, Dictionary<string, TypeDescriptor> genericBindings)
            : this(
                definition,
                new Dictionary<string, RuntimeValue>(StringComparer.Ordinal),
                new Dictionary<string, bool>(StringComparer.Ordinal),
                new Dictionary<string, TypeDescriptor?>(StringComparer.Ordinal),
                new Dictionary<string, VariableDeclarationType>(),
                genericBindings ?? new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal))
        {
        }

        private ClassInstanceValue(
            ClassTypeValue definition,
            Dictionary<string, RuntimeValue> fields,
            Dictionary<string, bool> publicity,
            Dictionary<string, TypeDescriptor?> types,
            Dictionary<string, VariableDeclarationType> declarationTypes,
            Dictionary<string, TypeDescriptor> genericBindings)
        {
            Definition = definition;
            Fields = fields;
            FieldPublicity = publicity;
            FieldTypes = types;
            FieldDeclarationTypes = declarationTypes;
            GenericBindings = genericBindings ?? new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal);
            // M38: size the slot array to the class's static shape. Empty
            // array when the class declares no fields (avoids array
            // allocation for tag-only classes). On Copy, we rebuild the
            // slot array from the source dict so the new instance shares
            // a shape but not the underlying values reference.
            int slotCount = definition.FieldSlotCount;
            FieldSlots = slotCount > 0 ? new RuntimeValue?[slotCount] : System.Array.Empty<RuntimeValue?>();
            if (slotCount > 0 && fields.Count > 0)
            {
                foreach (var kv in fields)
                {
                    int idx = definition.GetFieldSlotIndex(kv.Key);
                    if ((uint)idx < (uint)FieldSlots.Length)
                        FieldSlots[idx] = kv.Value;
                }
            }
        }

        public void SetField(string name, RuntimeValue value, bool isPublic, TypeDescriptor? fieldType = null, VariableDeclarationType declarationType = VariableDeclarationType.VARIABLE)
        {
            var stored = value.IsCopy ? value.Copy() : value;
            Fields[name] = stored;
            FieldPublicity[name] = isPublic;
            FieldTypes[name] = fieldType;
            FieldDeclarationTypes[name] = declarationType;
            // M38: mirror into shape-indexed slot array.
            int idx = Definition.GetFieldSlotIndex(name);
            if ((uint)idx < (uint)FieldSlots.Length)
                FieldSlots[idx] = stored;
        }

        public bool HasField(string name) => Fields.ContainsKey(name);
        public bool IsFieldPublic(string name) => FieldPublicity.TryGetValue(name, out var p) && p;

        public TypeDescriptor? GetFieldType(string name)
            => FieldTypes.TryGetValue(name, out var t) ? t : null;

        public VariableDeclarationType GetFieldDeclarationType(string name)
            => FieldDeclarationTypes.TryGetValue(name, out var dt) ? dt : VariableDeclarationType.VARIABLE;

        public RuntimeValue GetField(string name)
        {
            var v = Fields[name];
            return v.IsCopy ? v.Copy() : v;
        }

        public void SetMember(string name, RuntimeValue value)
        {
            if (!Fields.ContainsKey(name))
                throw new KeyNotFoundException(name);

            var stored = value.IsCopy ? value.Copy() : value;
            Fields[name] = stored;
            // M38: mirror into the slot array. The KeyNotFoundException
            // above ensures `name` is a declared field so the slot index
            // is always valid for this class's shape.
            int idx = Definition.GetFieldSlotIndex(name);
            if ((uint)idx < (uint)FieldSlots.Length)
                FieldSlots[idx] = stored;
        }

        public sealed override ValueResult AddedTo(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.PLUS, other, (l, r) => l.AddedTo(other));

        public sealed override ValueResult SubbedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.MINUS, other, (l, r) => l.SubbedBy(other));

        public sealed override ValueResult MultedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.MUL, other, (l, r) => l.MultedBy(other));

        public sealed override ValueResult DivedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.DIV, other, (l, r) => l.DivedBy(other));

        public sealed override ValueResult PowedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.POW, other, (l, r) => l.PowedBy(other));

        public sealed override ValueResult ModuledBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.MODULO, other, (l, r) => l.ModuledBy(other));

        public sealed override ValueResult BitwiseLeftShiftedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_LEFT_SHIFT, other, (l, r) => l.BitwiseLeftShiftedBy(other));

        public sealed override ValueResult BitwiseRightShiftedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_RIGHT_SHIFT, other, (l, r) => l.BitwiseRightShiftedBy(other));

        public sealed override ValueResult BitwiseAndedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_AND, other, (l, r) => l.BitwiseAndedBy(other));

        public sealed override ValueResult BitwiseOredBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_OR, other, (l, r) => l.BitwiseOredBy(other));

        public sealed override ValueResult GetComparisonEq(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.EE, other, (l, r) => l.GetComparisonEq(other));

        public sealed override ValueResult GetComparisonNe(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.NE, other, (l, r) => l.GetComparisonNe(other));

        public sealed override ValueResult GetComparisonLt(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.LT, other, (l, r) => l.GetComparisonLt(other));

        public sealed override ValueResult GetComparisonGt(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.GT, other, (l, r) => l.GetComparisonGt(other));

        public sealed override ValueResult GetComparisonLte(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.LTE, other, (l, r) => l.GetComparisonLte(other));

        public sealed override ValueResult GetComparisonGte(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.GTE, other, (l, r) => l.GetComparisonGte(other));

        public override RuntimeValue Copy()
            => new ClassInstanceValue(Definition, Fields, FieldPublicity, FieldTypes, FieldDeclarationTypes, GenericBindings)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public (string value, bool hasCustomToString) TryCallToString()
        {
            var toStringMethod = Definition.ResolveInstanceMethods("to_string")
                .FirstOrDefault(m => m.ArgNameToks.Count == 0);

            if (toStringMethod == null)
            {
                return (ToString(), false);
            }

            try
            {
                var boundMethod = new BoundClassMethodValue(Definition, this, toStringMethod, false)
                    .SetContext(Context)
                    .SetPos(PositionStart, PositionEnd);

                var result = SyncAwait.Get(boundMethod.Execute(new List<RuntimeValue>()));

                if (result.Error != null || result.Value == null)
                {
                    return (ToString(), false);
                }

                if (result.Value.Type == RuntimeValueType.String)
                {
                    return (((StringValue)result.Value).Value, true);
                }

                return (ToString(), false);
            }
            catch
            {
                return (ToString(), false);
            }
        }

        public override string ToString()
            => $"{Definition.ClassName}{{{string.Join(", ", Fields.Select(kv => $"{kv.Key}: {kv.Value}"))}}}";

        // Extension indexer dispatch. `obj[idx]` on a class instance
        // routes through the registered `op_index` extension method
        // when no native indexer exists. Async setter / getter bodies
        // collapse via SyncAwait — same pattern used for to_string
        // dispatch above.
        public override ValueResult ListAccess(RuntimeValue other)
        {
            // Native class indexer declared in the body — `fn op_index(i): T { ret … }`.
            // The method group resolves the right overload by arity, so an
            // op_index(i) and an op_index(i, j) can coexist. A body indexer takes
            // precedence over an extension indexer (own members win, as for methods).
            var ownGet = Definition.ResolveInstanceMethods("op_index");
            if (ownGet.Count > 0)
            {
                var group = new Classes.BoundClassMethodGroupValue(Definition, this, ownGet)
                    .SetContext(Context).SetPos(PositionStart, PositionEnd);
                var r = SyncAwait.Get(group.Execute(new System.Collections.Generic.List<RuntimeValue> { other }));
                if (r.Error != null) return (null, r.Error);
                return (r.FuncReturnValue ?? r.Value, null);
            }

            if (Context?.Extensions != null)
            {
                var entry = Context.Extensions.ResolveIndexerEntry(this, isAssignment: false, out var amb);
                if (entry != null && amb != null)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd,
                        $"ambiguous extension indexer (get) on '{Definition.ClassName}' — declared in two imported modules:\n  - {entry.FormatSource()}\n  - {amb.FormatSource()}",
                        Context));
                }
                if (entry != null)
                {
                    var bound = new Classes.BoundExtensionMethodGroupValue(
                        this,
                        new System.Collections.Generic.List<Parser.Nodes.Functions.FunctionDefinitionNode> { entry.Method })
                        .SetContext(Context)
                        .SetPos(PositionStart, PositionEnd);
                    var r = SyncAwait.Get(bound.Execute(new System.Collections.Generic.List<RuntimeValue> { other }));
                    if (r.Error != null) return (null, r.Error);
                    return (r.Value, null);
                }
            }
            return (null, new RuntimeError(PositionStart, PositionEnd,
                $"type '{Definition.ClassName}' has no indexer: '{Definition.ClassName}[...]' is not defined",
                Context,
                code: RaLanguage.Errors.DiagnosticCode.RuntimeGeneric,
                primaryLabel: "no readable indexer on this type",
                help: "define `fn op_index(index): T { ret … }` in the class body or an `extend` block (add `fn op_index_set(index, value)` to also allow `obj[index] = value`)"));
        }

        public override ValueResult ListSet(RuntimeValue index, RuntimeValue value)
        {
            // Native class indexer setter declared in the body —
            // `fn op_index_set(i, v) { … }` (arity-overloaded; body wins over extension).
            var ownSet = Definition.ResolveInstanceMethods("op_index_set");
            if (ownSet.Count > 0)
            {
                var group = new Classes.BoundClassMethodGroupValue(Definition, this, ownSet)
                    .SetContext(Context).SetPos(PositionStart, PositionEnd);
                var r = SyncAwait.Get(group.Execute(new System.Collections.Generic.List<RuntimeValue> { index, value }));
                if (r.Error != null) return (null, r.Error);
                return (r.FuncReturnValue ?? r.Value ?? value, null);
            }

            if (Context?.Extensions != null)
            {
                var entry = Context.Extensions.ResolveIndexerEntry(this, isAssignment: true, out var amb);
                if (entry != null && amb != null)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd,
                        $"ambiguous extension indexer (set) on '{Definition.ClassName}' — declared in two imported modules:\n  - {entry.FormatSource()}\n  - {amb.FormatSource()}",
                        Context));
                }
                if (entry != null)
                {
                    var bound = new Classes.BoundExtensionMethodGroupValue(
                        this,
                        new System.Collections.Generic.List<Parser.Nodes.Functions.FunctionDefinitionNode> { entry.Method })
                        .SetContext(Context)
                        .SetPos(PositionStart, PositionEnd);
                    var r = SyncAwait.Get(bound.Execute(new System.Collections.Generic.List<RuntimeValue> { index, value }));
                    if (r.Error != null) return (null, r.Error);
                    return (r.Value ?? value, null);
                }
            }
            return (null, new RuntimeError(PositionStart, PositionEnd,
                $"type '{Definition.ClassName}' has no assignable indexer: '{Definition.ClassName}[...] = value' is not defined",
                Context,
                code: RaLanguage.Errors.DiagnosticCode.RuntimeGeneric,
                primaryLabel: "no writable indexer on this type",
                help: "define `fn op_index_set(index, value) { … }` in the class body or an `extend` block"));
        }
    }
}
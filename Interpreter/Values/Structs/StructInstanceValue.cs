using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Structs
{
    public class StructInstanceValue : RuntimeValue
    {
        public StructTypeValue Definition { get; }
        public Dictionary<string, RuntimeValue> Fields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> FieldPublicity { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, VariableDeclarationType> FieldDeclarationTypes { get; } = new(StringComparer.Ordinal);

        // M41: shape-indexed slot array, parity with ClassInstanceValue (M38).
        // Dictionary above remains ground truth for reflection / iteration;
        // slot array is the O(1) read path consulted by the M28.1 IC.
        public RuntimeValue?[] FieldSlots;

        // Lazy-property bookkeeping. Allocated on first use.
        public HashSet<string>? LazyInitialized;
        public HashSet<string>? LazyInitializing;

        // Per-instance event subscriber storage. Allocated on first
        // subscribe. Only RecordInstance (record class flavour) ever
        // populates this slot — value-record / struct event
        // declarations are rejected at parse time.
        public Dictionary<string, RaLanguage.Interpreter.Runtime.Events.EventSubscriberList>? EventSubs;

        // Extension-field storage. See ClassInstanceValue for the
        // contract. Null until the first ext-field write hits this
        // instance.
        public RuntimeValue?[]? ExtFieldSlots;
        public ulong[]? ExtFieldInitBits;
        public ulong[]? ExtFieldLazyBits;

        public override RuntimeValueType Type => RuntimeValueType.StructInstance;
        public override bool IsCopy => true;

        public StructInstanceValue(StructTypeValue definition)
        {
            Definition = definition;
            int slotCount = definition.FieldSlotCount;
            FieldSlots = slotCount > 0 ? new RuntimeValue?[slotCount] : System.Array.Empty<RuntimeValue?>();
        }

        public void SetField(string name, RuntimeValue value, bool isPublic, VariableDeclarationType declarationType = VariableDeclarationType.VARIABLE)
        {
            var stored = value.IsCopy ? value.Copy() : value;
            Fields[name] = stored;
            FieldPublicity[name] = isPublic;
            FieldDeclarationTypes[name] = declarationType;
            int idx = Definition.GetFieldSlotIndex(name);
            if ((uint)idx < (uint)FieldSlots.Length) FieldSlots[idx] = stored;
        }

        public bool HasField(string name) => Fields.ContainsKey(name);

        public bool IsFieldPublic(string name) => FieldPublicity.TryGetValue(name, out var p) && p;

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
            int idx = Definition.GetFieldSlotIndex(name);
            if ((uint)idx < (uint)FieldSlots.Length) FieldSlots[idx] = stored;
        }

        public override ValueResult AddedTo(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.PLUS, other, (l, r) => l.AddedTo(other));

        public override ValueResult SubbedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.MINUS, other, (l, r) => l.SubbedBy(other));

        public override ValueResult MultedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.MUL, other, (l, r) => l.MultedBy(other));

        public override ValueResult DivedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.DIV, other, (l, r) => l.DivedBy(other));

        public override ValueResult PowedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.POW, other, (l, r) => l.PowedBy(other));

        public override ValueResult ModuledBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.MODULO, other, (l, r) => l.ModuledBy(other));

        public override ValueResult BitwiseLeftShiftedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_LEFT_SHIFT, other, (l, r) => l.BitwiseLeftShiftedBy(other));

        public override ValueResult BitwiseRightShiftedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_RIGHT_SHIFT, other, (l, r) => l.BitwiseRightShiftedBy(other));

        public override ValueResult BitwiseAndedBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_AND, other, (l, r) => l.BitwiseAndedBy(other));

        public override ValueResult BitwiseOredBy(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.BITWISE_OR, other, (l, r) => l.BitwiseOredBy(other));

        public override ValueResult GetComparisonEq(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.EE, other, (l, r) => l.GetComparisonEq(other));

        public override ValueResult GetComparisonNe(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.NE, other, (l, r) => l.GetComparisonNe(other));

        public override ValueResult GetComparisonLt(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.LT, other, (l, r) => l.GetComparisonLt(other));

        public override ValueResult GetComparisonGt(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.GT, other, (l, r) => l.GetComparisonGt(other));

        public override ValueResult GetComparisonLte(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.LTE, other, (l, r) => l.GetComparisonLte(other));

        public override ValueResult GetComparisonGte(RuntimeValue other) =>
            TryOperatorDispatch(TokenType.GTE, other, (l, r) => l.GetComparisonGte(other));

        public override RuntimeValue Copy()
        {
            var copy = new StructInstanceValue(Definition);
            foreach (var kv in Fields)
            {
                copy.Fields[kv.Key] = kv.Value.IsCopy ? kv.Value.Copy() : kv.Value;
                copy.FieldPublicity[kv.Key] = FieldPublicity.TryGetValue(kv.Key, out var p) && p;
                copy.FieldDeclarationTypes[kv.Key] = FieldDeclarationTypes.TryGetValue(kv.Key, out var dt) ? dt : VariableDeclarationType.VARIABLE;
            }

            return copy.SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public (string value, bool hasCustomToString) TryCallToString()
        {
            var toStringMethod = Definition.Methods
                .FirstOrDefault(m => string.Equals(m.NameTok.Value?.ToString(), "to_string", StringComparison.Ordinal) 
                                  && m.ArgNameToks.Count == 0);

            if (toStringMethod == null)
            {
                return (ToString(), false);
            }

            try
            {
                var boundMethod = new BoundStructMethodValue(Definition, this, toStringMethod)
                    .SetContext(Context)
                    .SetPos(PositionStart, PositionEnd);

                var result = SyncAwait.Get(boundMethod.Execute(new List<RuntimeValue>()));

                if (result.Error != null || result.Value == null)
                {
                    return (ToString(), false);
                }

                if (result.Value.Type == RuntimeValueType.String)
                {
                    return (((RaLanguage.Interpreter.Values.Primitives.StringValue)result.Value).Value, true);
                }

                return (ToString(), false);
            }
            catch
            {
                return (ToString(), false);
            }
        }

        public override string ToString()
            => $"{Definition.StructName}{{{string.Join(", ", Fields.Select(kv => $"{kv.Key}: {kv.Value}"))}}}";

        // Extension indexer dispatch on struct receivers. See the
        // matching override on ClassInstanceValue for the contract;
        // the only difference is the surrounding type wrapper used
        // for diagnostics.
        public override ValueResult ListAccess(RuntimeValue other)
        {
            // Native struct indexer declared in the body — `fn op_index(i): T { ret … }`.
            // Body indexer takes precedence over an extension indexer.
            var ownGet = Definition.GetMethod("op_index");
            if (ownGet != null)
            {
                var bound = new BoundStructMethodValue(Definition, this, ownGet)
                    .SetContext(Context).SetPos(PositionStart, PositionEnd);
                var r = SyncAwait.Get(bound.Execute(new System.Collections.Generic.List<RuntimeValue> { other }));
                if (r.Error != null) return (null, r.Error);
                return (r.FuncReturnValue ?? r.Value, null);
            }

            if (Context?.Extensions != null)
            {
                var entry = Context.Extensions.ResolveIndexerEntry(this, isAssignment: false, out var amb);
                if (entry != null && amb != null)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd,
                        $"ambiguous extension indexer (get) on '{Definition.StructName}' — declared in two imported modules:\n  - {entry.FormatSource()}\n  - {amb.FormatSource()}",
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
                $"type '{Definition.StructName}' has no indexer: '{Definition.StructName}[...]' is not defined",
                Context,
                code: RaLanguage.Errors.DiagnosticCode.RuntimeGeneric,
                primaryLabel: "no readable indexer on this type",
                help: "define `fn op_index(index): T { ret … }` in the struct body or an `extend` block (add `fn op_index_set(index, value)` to also allow `obj[index] = value`)"));
        }

        public override ValueResult ListSet(RuntimeValue index, RuntimeValue value)
        {
            // Native struct indexer setter declared in the body — `fn op_index_set(i, v) { … }`.
            var ownSet = Definition.GetMethod("op_index_set");
            if (ownSet != null)
            {
                var bound = new BoundStructMethodValue(Definition, this, ownSet)
                    .SetContext(Context).SetPos(PositionStart, PositionEnd);
                var r = SyncAwait.Get(bound.Execute(new System.Collections.Generic.List<RuntimeValue> { index, value }));
                if (r.Error != null) return (null, r.Error);
                return (r.FuncReturnValue ?? r.Value ?? value, null);
            }

            if (Context?.Extensions != null)
            {
                var entry = Context.Extensions.ResolveIndexerEntry(this, isAssignment: true, out var amb);
                if (entry != null && amb != null)
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd,
                        $"ambiguous extension indexer (set) on '{Definition.StructName}' — declared in two imported modules:\n  - {entry.FormatSource()}\n  - {amb.FormatSource()}",
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
                $"type '{Definition.StructName}' has no assignable indexer: '{Definition.StructName}[...] = value' is not defined",
                Context,
                code: RaLanguage.Errors.DiagnosticCode.RuntimeGeneric,
                primaryLabel: "no writable indexer on this type",
                help: "define `fn op_index_set(index, value) { … }` in the struct body or an `extend` block"));
        }
    }
}
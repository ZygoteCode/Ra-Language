using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    public abstract class BaseFunctionValue : RuntimeValue
    {
        public string Name { get; }
        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BaseFunctionValue(string name)
        {
            Name = name ?? "<anonymous>";
        }

        protected virtual string? ParameterOwnerForMetadata => Name;

        public Context? BindingContext { get; private set; }

        // Mirrors FunctionDefinitionNode.CaptureList. Null means "no explicit
        // capture clause" — the function uses the legacy implicit lexical
        // closure (every parent binding reachable through BindingContext).
        // Non-null means the listed names are materialised once into
        // `_capturedValues` at FreezeCaptures time and bound per-call into
        // the execution scope by GenerateNewContext.
        public List<CaptureSpec>? CaptureList { get; set; }

        // Concrete snapshot / borrow / moved value per explicit capture.
        // Populated by FreezeCaptures and consulted by GenerateNewContext.
        protected Dictionary<string, RuntimeValue>? _capturedValues;

        // Read-only projection so cross-cutting passes (SpawnNodeVisitor,
        // borrow-checker integration) can inspect captures without exposing
        // the internal dictionary for mutation.
        public IReadOnlyDictionary<string, RuntimeValue>? CapturedValues => _capturedValues;

        public void FreezeBindingContext(Context ctx)
        {
            if (BindingContext == null) BindingContext = ctx;
        }

        // Materialises every entry in CaptureList against the definition-time
        // context. Should be called immediately after FreezeBindingContext on
        // a freshly constructed function value. Returns a diagnostic Error if
        // any capture references an unknown / moved binding or attempts to
        // move out of a currently borrowed one.
        public Error? FreezeCaptures(Context definitionContext)
        {
            if (CaptureList == null || CaptureList.Count == 0) return null;

            _capturedValues = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
            foreach (var spec in CaptureList)
            {
                var entry = definitionContext.SymbolTable.GetEntry(spec.Name);
                if (entry == null)
                    return new RuntimeError(spec.PositionStart, spec.PositionEnd,
                        $"capture '{spec.Name}' is not defined in the enclosing scope",
                        definitionContext,
                        code: DiagnosticCode.RuntimeUndefinedSymbol,
                        primaryLabel: "unknown capture target",
                        help: "the capture list of a closure can only name bindings visible in the surrounding scope");

                if (entry.IsMoved)
                    return new RuntimeError(spec.PositionStart, spec.PositionEnd,
                        $"capture '{spec.Name}' was already moved out of the enclosing scope",
                        definitionContext,
                        code: DiagnosticCode.RuntimeMovedValue);

                switch (spec.Mode)
                {
                    case CaptureMode.ByValue:
                        // Snapshot semantics. Aliased() respects the memory model:
                        // primitives identity-copy, containers/instances alias the
                        // captured graph. The captured value is frozen against
                        // later rebinding of `spec.Name` in the outer scope.
                        _capturedValues[spec.Name] = entry.Value.Aliased();
                        break;

                    case CaptureMode.ByRef:
                    {
                        var ownerTable = FindOwnerTable(definitionContext.SymbolTable, spec.Name);
                        if (ownerTable == null) ownerTable = definitionContext.SymbolTable;

                        if (spec.IsMutableBorrow)
                        {
                            if (entry.SharedBorrowCount > 0)
                                return new RuntimeError(spec.PositionStart, spec.PositionEnd,
                                    $"cannot take '&mut {spec.Name}' into closure: shared borrows are alive",
                                    definitionContext,
                                    code: DiagnosticCode.RuntimeBorrowViolation,
                                    primaryLabel: "mutable capture while shared borrows live",
                                    help: "drop the outstanding '&' borrows before forming an '&mut' capture");

                            if (entry.HasMutableBorrow)
                                return new RuntimeError(spec.PositionStart, spec.PositionEnd,
                                    $"cannot take '&mut {spec.Name}' into closure: another mutable borrow is alive",
                                    definitionContext,
                                    code: DiagnosticCode.RuntimeBorrowViolation,
                                    primaryLabel: "second mutable capture",
                                    help: "only one '&mut' may be live at a time");

                            entry.HasMutableBorrow = true;
                        }
                        else
                        {
                            if (entry.HasMutableBorrow)
                                return new RuntimeError(spec.PositionStart, spec.PositionEnd,
                                    $"cannot take '&{spec.Name}' into closure: a mutable borrow is alive",
                                    definitionContext,
                                    code: DiagnosticCode.RuntimeBorrowViolation);
                            entry.SharedBorrowCount++;
                        }

                        var borrow = new BorrowValue(entry, ownerTable, spec.Name, spec.IsMutableBorrow, null)
                            .SetContext(definitionContext)
                            .SetPos(spec.PositionStart, spec.PositionEnd);
                        _capturedValues[spec.Name] = borrow;
                        break;
                    }

                    case CaptureMode.ByMove:
                        if (entry.IsBorrowed)
                            return new RuntimeError(spec.PositionStart, spec.PositionEnd,
                                $"cannot 'move {spec.Name}' into closure: it is currently borrowed",
                                definitionContext,
                                code: DiagnosticCode.RuntimeBorrowViolation,
                                primaryLabel: entry.HasMutableBorrow
                                    ? "binding is exclusively borrowed (&mut)"
                                    : $"binding has {entry.SharedBorrowCount} shared borrow(s) alive",
                                help: "let outstanding borrows drop before moving the value into a closure");

                        _capturedValues[spec.Name] = entry.Value;
                        entry.IsMoved = true;
                        break;
                }
            }
            return null;
        }

        private static SymbolTable? FindOwnerTable(SymbolTable start, string name)
        {
            SymbolTable? st = start;
            while (st != null)
            {
                if (st.GetLocalEntry(name) != null) return st;
                st = st.Parent;
            }
            return null;
        }

        public Context GenerateNewContext()
        {
            var closure = BindingContext ?? Context;
            // PERF: the Context constructor already allocates
            // `new SymbolTable(parent?.SymbolTable)` with parent == closure, so
            // the execution scope is fully formed here. The previous code
            // immediately overwrote it with a second, IDENTICAL SymbolTable
            // (newCtx.Parent IS closure ⇒ same parent chain) — allocating a
            // table + its eager backing dictionary per call only to discard
            // them. Context.Copy() already avoids this double-alloc; do the same
            // here. One SymbolTable per call instead of two.
            var newCtx = new Context(Name, closure, PositionStart);

            // Explicit captures shadow the lexical chain. The remaining free
            // variables of the body still resolve through the parent (so
            // sibling top-level functions, builtins, namespace members keep
            // working) — only the listed names are forcibly rebound to
            // their captured representation.
            if (_capturedValues != null)
            {
                foreach (var kv in _capturedValues)
                {
                    newCtx.SymbolTable.SetLocal(kv.Key, kv.Value);
                }
            }

            return newCtx;
        }

        public RuntimeResult CheckArgs(List<string> argNames, List<RuntimeValue> args)
        {
            var res = new RuntimeResult();
            if (args.Count > argNames.Count)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{args.Count - argNames.Count} too many args passed into {Name}", Context));

            if (args.Count < argNames.Count)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{argNames.Count - args.Count} too few args passed into {Name}", Context));

            return res.Success(null);
        }

        public void PopulateArgs(List<string> argNames, List<RuntimeValue> args, Context execCtx)
        {
            for (int i = 0; i < args.Count; i++)
            {
                var argValue = args[i];
                argValue.SetContext(execCtx);
                execCtx.SymbolTable.SetLocal(argNames[i], argValue);
            }
        }

        public virtual async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            return await ExecuteWithNamedArgs(positionalArgs, namedArgs, null);
        }

        public virtual async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs)
        {
            return await Execute(positionalArgs);
        }

        public async ValueTask<(Context? execCtx, Error? error)> PrepareExecutionContextForCall(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<string> formalNames,
            List<TypeDescriptor?>? argTypes,
            List<AstNode?>? paramDefaults,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType)
        {
            var execCtx = GenerateNewContext();

            int formalCountFast = formalNames?.Count ?? 0;

            // PERF fast path: the overwhelmingly common call shape — purely
            // positional arguments, exact arity, no varargs, and no parameter
            // annotations in the program. Bind each arg straight into the
            // execution scope and run the declared type checks, skipping the
            // `finalAssigned` dictionary, the `extras` list, the defaults pass,
            // the missing-arg scan, and the metadata coercion loop entirely.
            // Any deviation (named args, wrong arity needing defaults, varargs,
            // or a non-empty annotation registry) falls through to the original
            // general path below, byte-for-byte unchanged.
            if (positionalArgs.Count == formalCountFast
                && (namedArgs == null || namedArgs.Count == 0)
                && !hasVarArgs
                && MetadataRegistry.Global.IsEmpty)
            {
                for (int i = 0; i < formalCountFast; i++)
                {
                    var v = positionalArgs[i];
                    v.SetContext(execCtx);
                    execCtx.SymbolTable.SetLocal(formalNames![i], v);
                }
                if (argTypes != null)
                {
                    for (int i = 0; i < formalCountFast; i++)
                    {
                        var expected = i < argTypes.Count ? argTypes[i] : null;
                        if (expected != null && !expected.IsTypeParameter())
                        {
                            var actual = positionalArgs[i];
                            if (!TypeSystem.IsAssignable(execCtx, expected, actual))
                                return (null, new RuntimeError(PositionStart, PositionEnd, $"Type mismatch for argument '{formalNames![i]}': expected '{expected}', got '{actual.Type}'", Context));
                        }
                    }
                }
                return (execCtx, null);
            }

            var finalAssigned = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
            var extras = new List<RuntimeValue>();

            int formalCount = formalNames?.Count ?? 0;

            for (int i = 0; i < positionalArgs.Count; i++)
            {
                if (i < formalCount)
                {
                    var name = formalNames[i];
                    if (finalAssigned.ContainsKey(name))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Argument for parameter '{name}' provided multiple times", Context));
                    }
                    finalAssigned[name] = positionalArgs[i];
                }
                else
                {
                    if (!hasVarArgs)
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"{positionalArgs.Count - formalCount} too many args passed into {Name}", Context));

                    extras.Add(positionalArgs[i]);
                }
            }

            if (namedArgs != null)
            {
                foreach (var kv in namedArgs)
                {
                    var name = kv.Key;
                    var value = kv.Value;

                    if (hasVarArgs && varArgNameTok != null && name == varArgNameTok.Value.ToString())
                    {
                        if (value.Type != RuntimeValueType.List)
                        {
                            return (null, new RuntimeError(PositionStart, PositionEnd, $"Variadic named argument '{name}' must be a list", Context));
                        }

                        ListValue provided = (ListValue)value;
                        extras.AddRange(provided.Elements);
                        continue;
                    }

                    if (!formalNames.Contains(name))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Unknown named argument '{name}'", Context));
                    }

                    if (finalAssigned.ContainsKey(name))
                    {
                        return (null, new RuntimeError(PositionStart, PositionEnd, $"Argument for parameter '{name}' provided multiple times", Context));
                    }

                    finalAssigned[name] = value;
                }
            }

            foreach (var kv in finalAssigned)
            {
                var v = kv.Value;
                v.SetContext(execCtx);
                execCtx.SymbolTable.SetLocal(kv.Key, v);
            }

            if (paramDefaults != null)
            {
                var interpreter = new Interpreter();

                for (int i = 0; i < formalNames.Count; i++)
                {
                    var name = formalNames[i];
                    if (finalAssigned.ContainsKey(name)) continue;

                    AstNode? defAst = i < paramDefaults.Count ? paramDefaults[i] : null;
                    if (defAst != null)
                    {
                        var innerRes = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(defAst, execCtx, interpreter);
                        if (innerRes.Error != null) return (null, innerRes.Error);
                        var val = innerRes.Value;
                        if (val == null) val = new RaLanguage.Interpreter.Values.Primitives.NullValue().SetContext(execCtx).SetPos(defAst.PositionStart, defAst.PositionEnd);
                        val.SetContext(execCtx);
                        execCtx.SymbolTable.SetLocal(name, val);
                        finalAssigned[name] = val;
                    }
                }
            }

            for (int i = 0; i < formalNames.Count; i++)
            {
                var name = formalNames[i];
                if (!finalAssigned.ContainsKey(name))
                {
                    return (null, new RuntimeError(PositionStart, PositionEnd, $"Missing required argument '{name}' for function '{Name}'", Context));
                }
            }

            if (argTypes != null)
            {
                for (int i = 0; i < formalNames.Count; i++)
                {
                    var expected = i < argTypes.Count ? argTypes[i] : null;
                    if (expected != null)
                    {
                        if (expected.IsTypeParameter()) continue;

                        var actual = finalAssigned[formalNames[i]];
                        if (!TypeSystem.IsAssignable(execCtx, expected, actual))
                        {
                            return (null, new RuntimeError(PositionStart, PositionEnd, $"Type mismatch for argument '{formalNames[i]}': expected '{expected}', got '{actual.Type}'", Context));
                        }
                    }
                }
            }

            if (hasVarArgs)
            {
                var listVal = new ListValue(extras).SetContext(execCtx).SetPos(PositionStart, PositionEnd);

                if (varArgType != null)
                {
                    if (!varArgType.IsTypeParameter())
                    {
                        foreach (var e in extras)
                        {
                            if (!TypeSystem.IsAssignable(execCtx, varArgType, e))
                            {
                                string vname = varArgNameTok?.Value?.ToString() ?? "<vararg>";
                                return (null, new RuntimeError(PositionStart, PositionEnd, $"Type mismatch for variadic argument '{vname}': expected '{varArgType}', got '{e.Type}'", Context));
                            }
                        }
                    }
                }

                var varname = varArgNameTok?.Value?.ToString() ?? "params";
                execCtx.SymbolTable.SetLocal(varname, listVal);
            }

            // PERF: the per-parameter coercion / validation pass builds a
            // MetadataTarget key string per parameter and probes the registry.
            // When the program declares no annotations the whole pass is a
            // guaranteed no-op — skip it (and its `keys` list allocation).
            var owner = ParameterOwnerForMetadata;
            if (owner != null && !MetadataRegistry.Global.IsEmpty)
            {
                var keys = new List<string>(finalAssigned.Keys);
                foreach (var key in keys)
                {
                    var paramKey = MetadataTarget.BuildKey(AnnotationTargetKind.Parameter, owner, key);
                    var (newVal, verr) = AnnotationValidator.CoerceAndValidate(paramKey, finalAssigned[key], $"parameter '{key}'", execCtx);
                    if (verr != null) return (null, verr);
                    if (!ReferenceEquals(newVal, finalAssigned[key]))
                    {
                        finalAssigned[key] = newVal;
                        newVal.SetContext(execCtx);
                        execCtx.SymbolTable.SetLocal(key, newVal);
                    }
                }
                if (hasVarArgs)
                {
                    var varname = varArgNameTok?.Value?.ToString() ?? "params";
                    var paramKey = MetadataTarget.BuildKey(AnnotationTargetKind.Parameter, owner, varname);
                    var listVal = execCtx.SymbolTable.Get(varname);
                    if (listVal != null)
                    {
                        var (newVal, verr) = AnnotationValidator.CoerceAndValidate(paramKey, listVal, $"variadic '{varname}'", execCtx);
                        if (verr != null) return (null, verr);
                        if (!ReferenceEquals(newVal, listVal))
                        {
                            execCtx.SymbolTable.SetLocal(varname, newVal);
                        }
                    }
                }

                var contractKey = ContractMetadataKey(owner);
                if (contractKey != null)
                {
                    var preErr = ContractEvaluator.CheckPreconditions(contractKey, execCtx);
                    if (preErr != null) return (null, preErr);
                }
            }

            return (execCtx, null);
        }

        protected virtual string? ContractMetadataKey(string owner)
        {
            return owner.Contains('.', System.StringComparison.Ordinal)
                ? MetadataTarget.BuildKey(AnnotationTargetKind.Method, owner.Substring(0, owner.IndexOf('.')), owner.Substring(owner.IndexOf('.') + 1))
                : MetadataTarget.BuildKey(AnnotationTargetKind.Function, null, owner);
        }

        // Delegate combination operators. `+` produces a flat
        // MulticastDelegateValue (singleton fast path collapses to the
        // raw callable; existing multicasts splice). `-` removes the
        // last occurrence of `other` (identity first, then by name) and
        // returns either the surviving multicast, the lone handler, or
        // NullValue when every handler was removed.
        public sealed override ValueResult AddedTo(RuntimeValue other)
        {
            if (other == null || other.Type == RuntimeValueType.Null) return (this, null);
            if (other is BaseFunctionValue ofn)
            {
                var combined = new List<BaseFunctionValue>(4);
                AppendHandlers(this, combined);
                AppendHandlers(ofn, combined);
                if (combined.Count == 1) return (combined[0], null);
                return (new MulticastDelegateValue(combined).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            return (null, IllegalOperation(other));
        }

        public sealed override ValueResult SubbedBy(RuntimeValue other)
        {
            if (other == null || other.Type == RuntimeValueType.Null) return (this, null);
            if (other is BaseFunctionValue ofn)
            {
                var current = new List<BaseFunctionValue>(4);
                AppendHandlers(this, current);

                var targets = new List<BaseFunctionValue>(2);
                AppendHandlers(ofn, targets);

                for (int t = targets.Count - 1; t >= 0; t--)
                {
                    var tgt = targets[t];
                    int idx = -1;
                    for (int i = current.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(current[i], tgt)
                            || string.Equals(current[i].Name, tgt.Name, System.StringComparison.Ordinal))
                        {
                            idx = i; break;
                        }
                    }
                    if (idx >= 0) current.RemoveAt(idx);
                }

                if (current.Count == 0)
                    return (RaLanguage.Interpreter.Values.Primitives.NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd), null);
                if (current.Count == 1) return (current[0], null);
                return (new MulticastDelegateValue(current).SetContext(Context).SetPos(PositionStart, PositionEnd), null);
            }
            return (null, IllegalOperation(other));
        }

        private static void AppendHandlers(BaseFunctionValue v, List<BaseFunctionValue> sink)
        {
            if (v is MulticastDelegateValue mc)
            {
                for (int i = 0; i < mc.Handlers.Count; i++) sink.Add(mc.Handlers[i]);
                return;
            }
            sink.Add(v);
        }

        public RuntimeResult CheckAndPopulateArgs(
            List<string> argNames,
            List<RuntimeValue> args,
            Context execCtx,
            List<TypeDescriptor?>? argTypes = null,
            bool hasVarArgs = false,
            Token? varArgNameTok = null,
            TypeDescriptor? varArgType = null)
        {
            var res = new RuntimeResult();
            if (!hasVarArgs)
            {
                if (args.Count > argNames.Count)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{args.Count - argNames.Count} too many args passed into {Name}", Context));

                if (args.Count < argNames.Count)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{argNames.Count - args.Count} too few args passed into {Name}", Context));
            }
            else
            {
                if (args.Count < argNames.Count)
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"{argNames.Count - args.Count} too few args passed into {Name}", Context));
            }

            if (argTypes != null)
            {
                for (int i = 0; i < argNames.Count; i++)
                {
                    var expected = (i < argTypes.Count) ? argTypes[i] : null;
                    if (expected != null)
                    {
                        if (!expected.IsTypeParameter() && !TypeSystem.IsAssignable(execCtx, expected, args[i]))
                        {
                            return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                                $"Type mismatch for argument '{argNames[i]}': expected '{expected}', got '{args[i].Type}'", Context));
                        }
                    }
                }

                if (hasVarArgs && varArgType != null && !varArgType.IsTypeParameter())
                {
                    for (int i = argNames.Count; i < args.Count; i++)
                    {
                        if (!TypeSystem.IsAssignable(execCtx, varArgType, args[i]))
                        {
                            string varArgName = varArgNameTok?.Value?.ToString() ?? "<varargs>";
                            return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                                $"Type mismatch for variadic argument '{varArgName}': expected '{varArgType}', got '{args[i].Type}'", Context));
                        }
                    }
                }
            }

            for (int i = 0; i < argNames.Count; i++)
            {
                var argValue = args[i];
                argValue.SetContext(execCtx);
                execCtx.SymbolTable.SetLocal(argNames[i], argValue);
            }

            if (hasVarArgs)
            {
                var extras = new List<RuntimeValue>();
                for (int i = argNames.Count; i < args.Count; i++)
                {
                    var a = args[i];
                    a.SetContext(execCtx);
                    extras.Add(a);
                }

                var listVal = new ListValue(extras)
                    .SetContext(execCtx)
                    .SetPos(PositionStart, PositionEnd);

                var varName = varArgNameTok?.Value?.ToString() ?? "params";
                execCtx.SymbolTable.SetLocal(varName, listVal);
            }

            return res.Success(null);
        }
    }

    internal static class TypeDescriptorExtensions
    {
        public static bool IsTypeParameter(this TypeDescriptor? td)
        {
            if (td == null) return false;
            try
            {
                return td != null && td.IsTypeParameter;
            }
            catch { }
            return !string.IsNullOrEmpty(td.Name) && char.IsUpper(td.Name[0]) && td.GenericArgs.Count == 0;
        }

        public static TypeDescriptor? SubstituteBindings(this TypeDescriptor? td, Dictionary<string, TypeDescriptor> bindings)
        {
            if (td == null) return null;
            try
            {
                return td.Substitute(bindings);
            }
            catch { }
            if (td.IsTypeParameter() && bindings.TryGetValue(td.Name, out var bound)) return bound;
            if (td.GenericArgs == null || td.GenericArgs.Count == 0) return td;
            var src = td.GenericArgs;
            var newArgs = new List<TypeDescriptor>(src.Count);
            for (int i = 0; i < src.Count; i++)
                newArgs.Add(src[i].SubstituteBindings(bindings)!);

            return new TypeDescriptor(td.Name, newArgs);
        }
    }
}
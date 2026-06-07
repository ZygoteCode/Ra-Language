using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Runtime.Properties
{
    // Single source of truth for property get/set semantics. Called from
    // MemberAccessHelper (read) and MemberAssignmentHelper (write) once
    // they detect that the named member is a property rather than a
    // field/method.
    //
    // Operates on instance targets only (ClassInstanceValue,
    // StructInstanceValue, RecordInstanceValue). Static properties go
    // through a separate static branch via the ClassType target.
    //
    // Accessor body execution: bodies are AstNodes; we visit them
    // through a freshly-instantiated `Interpreter` because the running
    // dispatcher does not carry a back-reference to the interpreter
    // through Context. The construction is cheap (the visitor table
    // is built once and immutable), and the AnnotationValidator /
    // ContractEvaluator paths follow the same pattern.
    public static class PropertyAccessOps
    {
        // Reads `desc` from `instance`, applying lazy initialisation,
        // computed-getter dispatch, abstract guard, and the
        // visibility check (caller is responsible for is-inside-type;
        // it passes `isInsideDeclaringType=true` to skip the public
        // gate).
        public static RuntimeResult Get(
            RuntimeValue instance,
            PropertyDescriptor desc,
            Context context,
            Position posStart,
            Position posEnd,
            bool isInsideDeclaringType)
        {
            var res = new RuntimeResult();

            // Abstract properties have no concrete behaviour. Hitting
            // one at runtime means the override chain didn't supply a
            // concrete property — programmer error.
            if (desc.IsAbstract)
            {
                return res.Failure(new RuntimeError(posStart, posEnd,
                    $"cannot read abstract property '{desc.DeclaringTypeName}.{desc.Name}': no concrete override is in scope",
                    context));
            }

            if (!desc.HasGetter)
            {
                return res.Failure(new RuntimeError(posStart, posEnd,
                    $"property '{desc.DeclaringTypeName}.{desc.Name}' is write-only (no get accessor)",
                    context));
            }

            if (!isInsideDeclaringType && !desc.IsAccessorPublic(desc.Getter!))
            {
                return res.Failure(new RuntimeError(posStart, posEnd,
                    $"property '{desc.DeclaringTypeName}.{desc.Name}' is not readable here (get accessor is private)",
                    context));
            }

            // Lazy: first-touch initialisation. Re-entrant access during
            // the initializer raises a dedicated error.
            if (desc.IsLazy)
            {
                if (IsLazyInitializing(instance, desc.Name))
                {
                    return res.Failure(new RuntimeError(posStart, posEnd,
                        $"recursive access to lazy property '{desc.DeclaringTypeName}.{desc.Name}' during its own initialization",
                        context));
                }

                if (!IsLazyInitialized(instance, desc.Name))
                {
                    if (desc.DefaultValueNode == null)
                    {
                        return res.Failure(new RuntimeError(posStart, posEnd,
                            $"lazy property '{desc.DeclaringTypeName}.{desc.Name}' has no initializer",
                            context));
                    }

                    MarkLazyInitializing(instance, desc.Name, true);
                    try
                    {
                        // Lazy initialiser bodies see `self`, just like
                        // accessor bodies. Build a child context for
                        // the initialiser and bind `self` so the
                        // initialiser can reference other properties
                        // and fields on the instance.
                        var lazyCtx = context.Copy();
                        lazyCtx.SymbolTable!.Set("self", instance);

                        // L10: run the IR-compiled initializer thunk if the property
                        // lowered (GetOrCompilePropertyDefault); otherwise AST-walk
                        // the initializer. desc.SourceNode is the PropertyDefinitionNode.
                        RuntimeResult initRes;
                        if (desc.SourceNode.DefaultCompiledBody != null)
                        {
                            initRes = SyncAwait.Get(RunCompiledThunk(desc.SourceNode.DefaultCompiledBody, lazyCtx));
                        }
                        else
                        {
                            var initResVt = new Interpreter().Visit(desc.DefaultValueNode, lazyCtx);
                            initRes = initResVt.IsCompletedSuccessfully ? initResVt.Result : SyncAwait.Get(initResVt);
                        }
                        if (initRes.Error != null) return res.Failure(initRes.Error);

                        var value = initRes.Value ?? NullValue.Null;
                        var fieldKey = MetadataTarget.BuildKey(AnnotationTargetKind.Field, desc.DeclaringTypeName, desc.Name);
                        var (coerced, verr) = AnnotationValidator.CoerceAndValidate(fieldKey, value, $"lazy property '{desc.DeclaringTypeName}.{desc.Name}'", context);
                        if (verr != null) return res.Failure(verr);

                        WriteBackingSlot(instance, desc.Name, coerced);
                        MarkLazyInitialized(instance, desc.Name);
                    }
                    finally
                    {
                        MarkLazyInitializing(instance, desc.Name, false);
                    }
                }
            }

            // Computed get (custom body, no backing).
            if (!desc.HasBacking && desc.Getter!.Body != null)
            {
                return ExecuteGetterBody(instance, desc, context, posStart, posEnd);
            }

            // Stored auto get (body == null) — direct slot read.
            if (desc.Getter!.IsAuto)
            {
                var v = ReadBackingSlot(instance, desc.Name) ?? NullValue.Null;
                var aliased = v.IsCopy ? v.Copy() : v;
                return res.Success(aliased.SetContext(context).SetPos(posStart, posEnd));
            }

            // Mixed: explicit get body BUT has backing (the user wrote
            // a custom getter that may also read `field`). Execute the
            // body, expose `field`.
            return ExecuteGetterBody(instance, desc, context, posStart, posEnd);
        }

        // Writes `value` into `desc` on `instance`. Honors readonly /
        // init-only / lazy-without-setter, runs annotation validation,
        // dispatches custom set bodies, and fires the observe hook.
        public static async ValueTask<RuntimeResult> Set(
            RuntimeValue instance,
            PropertyDescriptor desc,
            RuntimeValue value,
            Context context,
            Position posStart,
            Position posEnd,
            bool isInsideDeclaringType,
            bool isInitContext)
        {
            var res = new RuntimeResult();

            if (desc.IsAbstract)
            {
                return res.Failure(new RuntimeError(posStart, posEnd,
                    $"cannot write abstract property '{desc.DeclaringTypeName}.{desc.Name}'",
                    context));
            }

            // Which accessor handles the write? Order:
            //   - init context + has Initter → init
            //   - has Setter → setter
            //   - readonly (only getter) → error
            PropertyAccessorRuntime? writer;
            bool isInitWrite = false;

            if (isInitContext && desc.HasInitter)
            {
                writer = desc.Initter;
                isInitWrite = true;
            }
            else if (desc.HasSetter)
            {
                writer = desc.Setter;
            }
            else if (desc.HasInitter)
            {
                // init-only, but we're past construction
                return res.Failure(new RuntimeError(posStart, posEnd,
                    $"property '{desc.DeclaringTypeName}.{desc.Name}' is init-only and cannot be assigned after construction",
                    context));
            }
            else if (desc.IsLazy)
            {
                return res.Failure(new RuntimeError(posStart, posEnd,
                    $"lazy property '{desc.DeclaringTypeName}.{desc.Name}' cannot be assigned (declare an explicit set accessor to allow it)",
                    context));
            }
            else
            {
                return res.Failure(new RuntimeError(posStart, posEnd,
                    $"property '{desc.DeclaringTypeName}.{desc.Name}' is read-only",
                    context));
            }

            if (!isInsideDeclaringType && !desc.IsAccessorPublic(writer!))
            {
                return res.Failure(new RuntimeError(posStart, posEnd,
                    $"property '{desc.DeclaringTypeName}.{desc.Name}' setter is not accessible here (private)",
                    context));
            }

            // Validation / coercion via annotation pipeline. The key
            // mirrors the field convention so existing validators
            // (`@min`, `@range`, etc.) apply transparently when placed
            // on a property.
            var fieldKey = MetadataTarget.BuildKey(AnnotationTargetKind.Field, desc.DeclaringTypeName, desc.Name);
            var (coerced, verr) = AnnotationValidator.CoerceAndValidate(fieldKey, value, $"property '{desc.DeclaringTypeName}.{desc.Name}'", context);
            if (verr != null) return res.Failure(verr);
            value = coerced;

            // Auto-shape writer? Write the slot directly.
            if (writer!.IsAuto)
            {
                var oldValue = ReadBackingSlot(instance, desc.Name);
                WriteBackingSlot(instance, desc.Name, value);

                if (isInitWrite)
                {
                    // init writes do not fire observers (notification
                    // makes no sense during construction).
                    return res.Success(value.SetContext(context).SetPos(posStart, posEnd));
                }

                if (desc.HasObserver)
                {
                    var observerErr = await ExecuteObserverBody(instance, desc, oldValue ?? NullValue.Null, value, context, posStart, posEnd);
                    if (observerErr != null) return res.Failure(observerErr);
                }

                return res.Success(value.SetContext(context).SetPos(posStart, posEnd));
            }

            // Custom setter / initter body. The body runs with `value`,
            // `field` (when backing exists), and `self` in scope.
            var setRes = await ExecuteWriterBody(instance, desc, writer!, value, context, posStart, posEnd);
            if (setRes.Error != null) return res.Failure(setRes.Error);

            // Observer (if present) fires after the user's setter body
            // returned. We re-read the slot to capture the post-body
            // value.
            if (desc.HasObserver && !isInitWrite)
            {
                var oldValue = ReadBackingSlot(instance, desc.Name);
                var observerErr = await ExecuteObserverBody(instance, desc, oldValue ?? NullValue.Null, value, context, posStart, posEnd);
                if (observerErr != null) return res.Failure(observerErr);
            }

            return res.Success(value.SetContext(context).SetPos(posStart, posEnd));
        }

        // -----------------------------------------------------------
        //  Slot helpers
        // -----------------------------------------------------------

        public static RuntimeValue? ReadBackingSlot(RuntimeValue instance, string name)
        {
            if (instance is ClassInstanceValue cls)
            {
                if (cls.Fields.TryGetValue(name, out var v)) return v;
                return null;
            }
            if (instance is StructInstanceValue st)
            {
                if (st.Fields.TryGetValue(name, out var v)) return v;
                return null;
            }
            return null;
        }

        public static void WriteBackingSlot(RuntimeValue instance, string name, RuntimeValue value)
        {
            if (instance is ClassInstanceValue cls)
            {
                bool isPub = true; // properties expose their own visibility; backing slot publicity is bookkeeping
                if (cls.FieldPublicity.TryGetValue(name, out var p)) isPub = p;
                if (!cls.HasField(name))
                {
                    cls.SetField(name, value, isPub);
                }
                else
                {
                    cls.SetMember(name, value);
                }
                return;
            }
            if (instance is StructInstanceValue st)
            {
                bool isPub = true;
                if (st.FieldPublicity.TryGetValue(name, out var p)) isPub = p;
                if (!st.HasField(name))
                {
                    st.SetField(name, value, isPub);
                }
                else
                {
                    st.SetMember(name, value);
                }
            }
        }

        // -----------------------------------------------------------
        //  Lazy bookkeeping
        // -----------------------------------------------------------

        private static bool IsLazyInitialized(RuntimeValue instance, string name)
        {
            if (instance is ClassInstanceValue cls)
                return cls.LazyInitialized != null && cls.LazyInitialized.Contains(name);
            if (instance is StructInstanceValue st)
                return st.LazyInitialized != null && st.LazyInitialized.Contains(name);
            return false;
        }

        private static void MarkLazyInitialized(RuntimeValue instance, string name)
        {
            if (instance is ClassInstanceValue cls)
            {
                cls.LazyInitialized ??= new HashSet<string>(StringComparer.Ordinal);
                cls.LazyInitialized.Add(name);
                return;
            }
            if (instance is StructInstanceValue st)
            {
                st.LazyInitialized ??= new HashSet<string>(StringComparer.Ordinal);
                st.LazyInitialized.Add(name);
            }
        }

        private static bool IsLazyInitializing(RuntimeValue instance, string name)
        {
            if (instance is ClassInstanceValue cls)
                return cls.LazyInitializing != null && cls.LazyInitializing.Contains(name);
            if (instance is StructInstanceValue st)
                return st.LazyInitializing != null && st.LazyInitializing.Contains(name);
            return false;
        }

        private static void MarkLazyInitializing(RuntimeValue instance, string name, bool flag)
        {
            if (instance is ClassInstanceValue cls)
            {
                if (flag)
                {
                    cls.LazyInitializing ??= new HashSet<string>(StringComparer.Ordinal);
                    cls.LazyInitializing.Add(name);
                }
                else
                {
                    cls.LazyInitializing?.Remove(name);
                }
                return;
            }
            if (instance is StructInstanceValue st)
            {
                if (flag)
                {
                    st.LazyInitializing ??= new HashSet<string>(StringComparer.Ordinal);
                    st.LazyInitializing.Add(name);
                }
                else
                {
                    st.LazyInitializing?.Remove(name);
                }
            }
        }

        // -----------------------------------------------------------
        //  Accessor body execution
        // -----------------------------------------------------------

        private static RuntimeResult ExecuteGetterBody(
            RuntimeValue instance,
            PropertyDescriptor desc,
            Context context,
            Position posStart,
            Position posEnd)
        {
            var res = new RuntimeResult();

            var body = desc.Getter!.Body!;
            var inner = context.Copy();
            inner.SymbolTable!.Set("self", instance);
            if (desc.HasBacking)
            {
                var slotVal = ReadBackingSlot(instance, desc.Name) ?? NullValue.Null;
                inner.SymbolTable.Set("field", slotVal);
            }

            var vt = RunAccessorBody(desc.Getter!, body, inner);
            var bodyRes = vt.IsCompletedSuccessfully ? vt.Result : SyncAwait.Get(vt);

            // After the body runs, mirror `field` back into the slot —
            // a custom getter is allowed to lazy-init storage by
            // writing to `field`. This is rarely used but cheap.
            if (desc.HasBacking)
            {
                var fieldEntry = inner.SymbolTable.GetEntry("field");
                if (fieldEntry != null && fieldEntry.Value != null)
                {
                    WriteBackingSlot(instance, desc.Name, fieldEntry.Value);
                }
            }

            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);
            return res.Success((bodyRes.Value ?? NullValue.Null).SetContext(context).SetPos(posStart, posEnd));
        }

        private static async ValueTask<RuntimeResult> ExecuteWriterBody(
            RuntimeValue instance,
            PropertyDescriptor desc,
            PropertyAccessorRuntime writer,
            RuntimeValue value,
            Context context,
            Position posStart,
            Position posEnd)
        {
            var res = new RuntimeResult();
            var body = writer.Body!;
            var inner = context.Copy();
            inner.SymbolTable!.Set("self", instance);
            inner.SymbolTable.Set("value", value);
            if (desc.HasBacking)
            {
                var slotVal = ReadBackingSlot(instance, desc.Name) ?? NullValue.Null;
                inner.SymbolTable.Set("field", slotVal);
            }

            var bodyRes = await RunAccessorBody(writer, body, inner);
            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);

            // Mirror `field` back. For the common `field = value`
            // idiom this is the line that makes the assignment stick.
            if (desc.HasBacking)
            {
                var fieldEntry = inner.SymbolTable.GetEntry("field");
                if (fieldEntry != null && fieldEntry.Value != null)
                {
                    WriteBackingSlot(instance, desc.Name, fieldEntry.Value);
                }
            }

            return res.Success(value.SetContext(context).SetPos(posStart, posEnd));
        }

        // L10: run an accessor body. When the IR compiled it (GetOrCompileAccessor),
        // execute the RaFunction via the pooled VM — self/field/value/old are read
        // from `inner.SymbolTable` (LoadLocalS lazy-resolves the empty slot by name
        // and caches the SAME SymbolEntry, so the body's `field = value` mutates the
        // entry the callers mirror back). Otherwise AST-walk the body. The result is
        // normalised so the getter's `bodyRes.Value` reads the body's return.
        private static ValueTask<RuntimeResult> RunAccessorBody(PropertyAccessorRuntime accessor, RaLanguage.Parser.Nodes.AstNode body, Context inner)
        {
            var compiled = RaLanguage.Interpreter.Runtime.FunctionDefinitionHelper.GetOrCompileAccessor(accessor.SourceNode);
            if (compiled == null)
                return new Interpreter().Visit(body, inner);
            return RunCompiledAccessor(compiled, inner);
        }

        private static async ValueTask<RuntimeResult> RunCompiledAccessor(RaLanguage.Interpreter.IR.RaFunction compiled, Context inner)
        {
            var host = Vm.VmHostPool.Rent();
            var frame = Vm.VmFrame.Rent(compiled);
            RuntimeResult bodyRes;
            var execTask = host.Executor.Execute(frame, inner);
            if (execTask.IsCompletedSuccessfully)
            {
                bodyRes = execTask.Result;
                Vm.VmHostPool.Return(host);
            }
            else
            {
                bodyRes = await execTask.ConfigureAwait(false);
            }
            Vm.VmFrame.Return(frame);
            // Surface the OP_RET value through `.Value` so the getter (which reads
            // bodyRes.Value) matches the visitor path; setters/observers ignore it.
            if (bodyRes.Error == null && bodyRes.FuncReturnValue != null)
                return new RuntimeResult().Success(bodyRes.FuncReturnValue);
            return bodyRes;
        }

        // L10: run an IR-compiled default-init thunk (a self-bound 0-arg RaFunction).
        // Shared by the LAZY property first-touch path (self bound in `inner`) AND by
        // eager struct/class FIELD construction (which passes the construction context
        // verbatim, matching the AST-walk path). Mirrors RunCompiledAccessor — rent the
        // pooled VM, run, return the host, normalise the OP_RET value through `.Value`
        // so the caller reads the initializer's result.
        internal static async ValueTask<RuntimeResult> RunCompiledThunk(RaLanguage.Interpreter.IR.RaFunction compiled, Context inner)
        {
            var host = Vm.VmHostPool.Rent();
            var frame = Vm.VmFrame.Rent(compiled);
            RuntimeResult bodyRes;
            var execTask = host.Executor.Execute(frame, inner);
            if (execTask.IsCompletedSuccessfully)
            {
                bodyRes = execTask.Result;
                Vm.VmHostPool.Return(host);
            }
            else
            {
                bodyRes = await execTask.ConfigureAwait(false);
            }
            Vm.VmFrame.Return(frame);
            if (bodyRes.Error == null && bodyRes.FuncReturnValue != null)
                return new RuntimeResult().Success(bodyRes.FuncReturnValue);
            return bodyRes;
        }

        private static async ValueTask<Error?> ExecuteObserverBody(
            RuntimeValue instance,
            PropertyDescriptor desc,
            RuntimeValue oldValue,
            RuntimeValue newValue,
            Context context,
            Position posStart,
            Position posEnd)
        {
            var body = desc.Observer!.Body!;
            var inner = context.Copy();
            inner.SymbolTable!.Set("self", instance);
            inner.SymbolTable.Set("old", oldValue);
            inner.SymbolTable.Set("value", newValue);
            inner.SymbolTable.Set("field", newValue);

            var bodyRes = await RunAccessorBody(desc.Observer!, body, inner);
            if (bodyRes.Error != null) return bodyRes.Error;
            return null;
        }
    }
}

using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Events;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Records;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Lexer;
using System.Numerics;

namespace RaLanguage.Interpreter.Runtime.Events
{
    // Single source of truth for the subscribe / unsubscribe / clear /
    // count / raise pipeline. Called from BoundEventMethodValue.Execute
    // (synthetic methods) and from EventSubscriptionValue.Execute (raise).
    //
    // Owner is one of:
    //   - ClassInstanceValue / RecordInstanceValue (StructInstanceValue
    //     for record class) — non-static events
    //   - ClassTypeValue / RecordTypeValue          — static events
    //
    // For instance events the subscriber list is allocated lazily in
    // ClassInstanceValue.EventSubs / StructInstanceValue.EventSubs.
    // For static events it lives in ClassTypeValue.StaticEventSubs /
    // StructTypeValue.StaticEventSubs.
    public static class EventAccessOps
    {
        // ----------------------------------------------------------------
        //  Subscriber list resolution
        // ----------------------------------------------------------------

        // Get-or-create the subscriber list for (owner, event). Creates
        // both the dictionary (if null) and the list (if missing). The
        // returned list is the *live* list; emission takes its own
        // snapshot.
        public static EventSubscriberList GetOrCreateList(RuntimeValue owner, EventDescriptor desc)
        {
            if (desc.IsStatic)
            {
                if (owner is ClassTypeValue ct)
                {
                    ct.StaticEventSubs ??= new Dictionary<string, EventSubscriberList>(StringComparer.Ordinal);
                    if (!ct.StaticEventSubs.TryGetValue(desc.Name, out var list))
                    {
                        list = new EventSubscriberList();
                        ct.StaticEventSubs[desc.Name] = list;
                    }
                    return list;
                }
                if (owner is StructTypeValue st)
                {
                    st.StaticEventSubs ??= new Dictionary<string, EventSubscriberList>(StringComparer.Ordinal);
                    if (!st.StaticEventSubs.TryGetValue(desc.Name, out var list))
                    {
                        list = new EventSubscriberList();
                        st.StaticEventSubs[desc.Name] = list;
                    }
                    return list;
                }
                // Defensive: descriptor says static but owner is an instance.
                // Should never happen; treat as instance.
            }

            if (owner is ClassInstanceValue ci)
            {
                ci.EventSubs ??= new Dictionary<string, EventSubscriberList>(StringComparer.Ordinal);
                if (!ci.EventSubs.TryGetValue(desc.Name, out var list))
                {
                    list = new EventSubscriberList();
                    ci.EventSubs[desc.Name] = list;
                }
                return list;
            }
            if (owner is StructInstanceValue si)
            {
                si.EventSubs ??= new Dictionary<string, EventSubscriberList>(StringComparer.Ordinal);
                if (!si.EventSubs.TryGetValue(desc.Name, out var list))
                {
                    list = new EventSubscriberList();
                    si.EventSubs[desc.Name] = list;
                }
                return list;
            }
            // Synthetic fallback — never write back, treat as empty.
            return new EventSubscriberList();
        }

        // Read-only lookup (does not allocate). Returns null if no list
        // has been created yet.
        public static EventSubscriberList? GetListOrNull(RuntimeValue owner, EventDescriptor desc)
        {
            if (desc.IsStatic)
            {
                if (owner is ClassTypeValue ct)
                {
                    if (ct.StaticEventSubs == null) return null;
                    return ct.StaticEventSubs.TryGetValue(desc.Name, out var l) ? l : null;
                }
                if (owner is StructTypeValue st)
                {
                    if (st.StaticEventSubs == null) return null;
                    return st.StaticEventSubs.TryGetValue(desc.Name, out var l) ? l : null;
                }
            }
            if (owner is ClassInstanceValue ci)
            {
                if (ci.EventSubs == null) return null;
                return ci.EventSubs.TryGetValue(desc.Name, out var l) ? l : null;
            }
            if (owner is StructInstanceValue si)
            {
                if (si.EventSubs == null) return null;
                return si.EventSubs.TryGetValue(desc.Name, out var l) ? l : null;
            }
            return null;
        }

        // ----------------------------------------------------------------
        //  Public surface — invoked by BoundEventMethodValue.Execute
        // ----------------------------------------------------------------

        public static async ValueTask<RuntimeResult> InvokeAccessor(
            EventSubscriptionValue source,
            EventMethodKind method,
            List<RuntimeValue> args,
            Context context,
            Dictionary<string, RuntimeValue>? namedArgs = null)
        {
            var res = new RuntimeResult();
            switch (method)
            {
                case EventMethodKind.On:
                    return Subscribe(source, args, context, namedArgs);
                case EventMethodKind.Off:
                    return Unsubscribe(source, args, context);
                case EventMethodKind.Clear:
                    return ClearAll(source, context);
                case EventMethodKind.Count:
                    return CountSubscribers(source, context);
            }
            return res.Failure(new RuntimeError(source.PositionStart, source.PositionEnd,
                $"unknown event accessor '{method}'", context));
        }

        public static RuntimeResult InvokeSubscriptionAccessor(
            SubscriptionValue source,
            SubscriptionMethodKind method,
            List<RuntimeValue> args,
            Context context)
        {
            var res = new RuntimeResult();
            switch (method)
            {
                case SubscriptionMethodKind.Dispose:
                    return Dispose(source, context);
                case SubscriptionMethodKind.IsActive:
                    return IsActive(source, context);
            }
            return res.Failure(new RuntimeError(source.PositionStart, source.PositionEnd,
                $"unknown subscription accessor '{method}'", context));
        }

        // ----------------------------------------------------------------
        //  Subscribe
        // ----------------------------------------------------------------

        private static RuntimeResult Subscribe(
            EventSubscriptionValue source,
            List<RuntimeValue> args,
            Context context,
            Dictionary<string, RuntimeValue>? namedArgs)
        {
            var res = new RuntimeResult();

            if (args.Count == 0)
            {
                return res.Failure(new RuntimeError(source.PositionStart, source.PositionEnd,
                    $"event.on(...) requires a handler function as the first argument",
                    context));
            }

            // Visibility check is enforced by MemberAccessHelper on the
            // initial obj.Event read. Subscribers can always call on() if
            // they could obtain the EventSubscriptionValue.

            var handler = args[0] as BaseFunctionValue;
            if (handler == null)
            {
                return res.Failure(new RuntimeError(source.PositionStart, source.PositionEnd,
                    $"event.on(handler) — first argument must be a function (got {args[0].Type})",
                    context));
            }

            bool once = false, weak = false;
            int priority = 0;

            // Positional options follow handler. Order: once, priority, weak.
            // Named-args override positional.
            if (args.Count > 1 && args[1] is BooleanValue b1) once = b1.Value;
            if (args.Count > 2 && args[2] is NumberValue n2) priority = ToInt(n2);
            if (args.Count > 3 && args[3] is BooleanValue b3) weak = b3.Value;

            if (namedArgs != null)
            {
                if (namedArgs.TryGetValue("once", out var v1) && v1 is BooleanValue b) once = b.Value;
                if (namedArgs.TryGetValue("priority", out var v2) && v2 is NumberValue n) priority = ToInt(n);
                if (namedArgs.TryGetValue("weak", out var v3) && v3 is BooleanValue bw) weak = bw.Value;
            }

            var sub = new EventSubscription
            {
                Once = once,
                Priority = priority,
            };
            if (weak)
                sub.WeakHandler = new WeakReference<BaseFunctionValue>(handler);
            else
                sub.StrongHandler = handler;

            var list = GetOrCreateList(source.Owner, source.Descriptor);
            long token = list.Add(sub);

            var handle = new SubscriptionValue(source, token)
                .SetContext(context)
                .SetPos(source.PositionStart, source.PositionEnd);
            return res.Success(handle);
        }

        // ----------------------------------------------------------------
        //  Unsubscribe
        // ----------------------------------------------------------------

        private static RuntimeResult Unsubscribe(
            EventSubscriptionValue source,
            List<RuntimeValue> args,
            Context context)
        {
            var res = new RuntimeResult();
            if (args.Count == 0)
            {
                return res.Failure(new RuntimeError(source.PositionStart, source.PositionEnd,
                    "event.off(sub_or_handler) requires a Subscription handle or handler function",
                    context));
            }

            var list = GetListOrNull(source.Owner, source.Descriptor);
            if (list == null)
            {
                return res.Success(BooleanValue.Of(false)
                    .SetContext(context).SetPos(source.PositionStart, source.PositionEnd));
            }

            bool removed = false;
            if (args[0] is SubscriptionValue sv)
            {
                removed = list.RemoveByToken(sv.Token);
                if (removed) sv.Disposed = true;
            }
            else if (args[0] is BaseFunctionValue handler)
            {
                removed = list.RemoveByHandler(handler);
            }
            else
            {
                return res.Failure(new RuntimeError(source.PositionStart, source.PositionEnd,
                    $"event.off(...) expects a Subscription or function (got {args[0].Type})",
                    context));
            }

            return res.Success(BooleanValue.Of(removed)
                .SetContext(context).SetPos(source.PositionStart, source.PositionEnd));
        }

        // ----------------------------------------------------------------
        //  Clear / count
        // ----------------------------------------------------------------

        private static RuntimeResult ClearAll(EventSubscriptionValue source, Context context)
        {
            var list = GetListOrNull(source.Owner, source.Descriptor);
            int n = list?.ClearAll() ?? 0;
            return new RuntimeResult().Success(MakeInt(n)
                .SetContext(context).SetPos(source.PositionStart, source.PositionEnd));
        }

        private static RuntimeResult CountSubscribers(EventSubscriptionValue source, Context context)
        {
            var list = GetListOrNull(source.Owner, source.Descriptor);
            int n = list?.LiveCount() ?? 0;
            return new RuntimeResult().Success(MakeInt(n)
                .SetContext(context).SetPos(source.PositionStart, source.PositionEnd));
        }

        // ----------------------------------------------------------------
        //  Subscription methods
        // ----------------------------------------------------------------

        private static RuntimeResult Dispose(SubscriptionValue sub, Context context)
        {
            if (sub.Disposed)
            {
                return new RuntimeResult().Success(BooleanValue.Of(false)
                    .SetContext(context).SetPos(sub.PositionStart, sub.PositionEnd));
            }
            var list = GetListOrNull(sub.Source.Owner, sub.Source.Descriptor);
            bool ok = list != null && list.RemoveByToken(sub.Token);
            sub.Disposed = true;
            return new RuntimeResult().Success(BooleanValue.Of(ok)
                .SetContext(context).SetPos(sub.PositionStart, sub.PositionEnd));
        }

        private static RuntimeResult IsActive(SubscriptionValue sub, Context context)
        {
            return new RuntimeResult().Success(BooleanValue.Of(!sub.Disposed)
                .SetContext(context).SetPos(sub.PositionStart, sub.PositionEnd));
        }

        // ----------------------------------------------------------------
        //  Raise
        // ----------------------------------------------------------------

        // Snapshot-and-fire semantics:
        //   1. Take a stable snapshot of the live list (filtered by
        //      weak-liveness, sorted by descending priority — stable).
        //   2. For each handler: if `once`, remove from live list first
        //      (so reentry sees the unsubscribed state), then call.
        //   3. Cancellable events short-circuit on first true return.
        //   4. Non-cancellable: first error aborts the loop.
        // Public entry point for emission. Behaviour grid:
        //
        //   cancellable | tolerant | async  | return value
        //   -----------+----------+--------+----------------------
        //   false      | false    | false  | null
        //   true       | false    | false  | bool  (cancelled)
        //   false      | true     | false  | list[string]  (errors)
        //   true       | true     | false  | tuple(bool, list[string])
        //   *          | *        | true   | task wrapping the value above
        //
        // For async events, handlers MAY return a TaskValue; emit awaits
        // the underlying Core before moving to the next handler. The
        // outer return is wrapped in TaskValue.Completed so the user
        // can write `await obj.E(args)`.
        public static async ValueTask<RuntimeResult> RaiseDirect(
            EventSubscriptionValue source,
            List<RuntimeValue> args,
            Context context)
        {
            var res = new RuntimeResult();
            var desc = source.Descriptor;

            if (desc.IsAbstract)
            {
                return res.Failure(new RuntimeError(source.PositionStart, source.PositionEnd,
                    $"cannot raise abstract event '{desc.DeclaringTypeName}.{desc.Name}'",
                    context));
            }

            if (!desc.RaiseIsPublic)
            {
                bool isInside = IsCallerInsideDeclaringType(context, desc.DeclaringTypeName);
                if (!isInside)
                {
                    return res.Failure(new RuntimeError(source.PositionStart, source.PositionEnd,
                        $"event '{desc.DeclaringTypeName}.{desc.Name}' cannot be raised from outside the declaring type (raise is private)",
                        context));
                }
            }

            // Annotation hooks (@deprecated warning, @intercept before/after).
            // Look up by metadata key built from the descriptor's
            // declaring type + name. Static events use a different key
            // prefix so callers can register interceptors per-flavour.
            var metaKey = desc.IsStatic
                ? MetadataTarget.BuildKey(AnnotationTargetKind.StaticEvent, desc.DeclaringTypeName, desc.Name)
                : MetadataTarget.BuildKey(AnnotationTargetKind.Event, desc.DeclaringTypeName, desc.Name);
            EmitDeprecationWarning(metaKey, desc, source.PositionStart);
            var beforeErr = AnnotationInterceptors.RunBefore(metaKey, desc.Name, args, context);
            if (beforeErr != null) return res.Failure(beforeErr);

            // Build snapshot once. Weak-dead refs pruned in-place from
            // the live list so subsequent emits skip them.
            var snapshot = BuildSnapshot(source);

            bool cancelled = false;
            List<string>? errors = desc.IsTolerant ? new List<string>() : null;

            foreach (var (sub, handler) in snapshot)
            {
                if (sub.Once)
                {
                    var live = GetListOrNull(source.Owner, source.Descriptor);
                    live?.RemoveByToken(sub.Token);
                }

                RuntimeResult callRes;
                try
                {
                    callRes = await handler.Execute(args);
                }
                catch (System.Exception ex)
                {
                    if (errors != null)
                    {
                        errors.Add(ex.Message ?? ex.GetType().Name);
                        continue;
                    }
                    throw;
                }

                // Resolve TaskValue returns when async event — sequential
                // await; preserves handler ordering across async boundary.
                if (desc.IsAsync && callRes.Error == null && callRes.Value is TaskValue tv)
                {
                    if (!tv.Core.IsCompleted)
                    {
                        await tv.Core.WaitAsync().ConfigureAwait(false);
                    }
                    if (tv.Core.Error != null)
                    {
                        callRes = new RuntimeResult().Failure(tv.Core.Error);
                    }
                    else
                    {
                        callRes = new RuntimeResult().Success(tv.Core.Result ?? NullValue.Null);
                    }
                }

                if (callRes.Error != null)
                {
                    if (errors != null)
                    {
                        errors.Add(callRes.Error.Details ?? "<handler error>");
                        continue;
                    }
                    return res.Failure(callRes.Error);
                }

                if (desc.IsCancellable)
                {
                    if (callRes.Value is BooleanValue bv && bv.Value)
                    {
                        cancelled = true;
                        break;
                    }
                    if (callRes.Value is not BooleanValue && callRes.Value is not NullValue)
                    {
                        var sigErr = new RuntimeError(source.PositionStart, source.PositionEnd,
                            $"handler of cancellable event '{desc.DeclaringTypeName}.{desc.Name}' must return bool (got {callRes.Value?.Type})",
                            context);
                        if (errors != null)
                        {
                            errors.Add(sigErr.Details ?? "<handler signature error>");
                            continue;
                        }
                        return res.Failure(sigErr);
                    }
                }
            }

            RuntimeValue rawResult = BuildResultValue(desc, cancelled, errors, source, context);

            var afterErr = AnnotationInterceptors.RunAfter(metaKey, desc.Name, rawResult, context);
            if (afterErr != null) return res.Failure(afterErr);

            // Async wrap — caller can `await` the result regardless of
            // whether any handler was async. NB: cannot use
            // `TaskValue.Completed(rawResult)` here — that helper races
            // with TryAutoRecycle (the core has zero TaskValue refs at
            // Complete-time, so the recycle fires and resets Status
            // back to Pending before the wrapper constructor takes
            // ownership). Construct the wrapper first, then complete.
            if (desc.IsAsync)
            {
                var core = RaTaskCore.Rent(new CancellationScope(), null, $"<event:{desc.Name}>");
                var task = new TaskValue(core)
                    .SetContext(context).SetPos(source.PositionStart, source.PositionEnd);
                core.TrySetRunning();
                core.Complete(rawResult);
                return res.Success(task);
            }
            return res.Success(rawResult);
        }

        private static List<(EventSubscription sub, BaseFunctionValue handler)> BuildSnapshot(EventSubscriptionValue source)
        {
            var list = GetListOrNull(source.Owner, source.Descriptor);
            if (list == null || list.Items.Count == 0)
                return new List<(EventSubscription, BaseFunctionValue)>();

            var snapshot = new List<(EventSubscription sub, BaseFunctionValue handler)>(list.Items.Count);
            foreach (var s in list.Items)
            {
                if (s.Disposed) continue;
                var h = s.ResolveHandler();
                if (h == null)
                {
                    s.Disposed = true;
                    continue;
                }
                snapshot.Add((s, h));
            }
            list.Items.RemoveAll(x => x.Disposed);

            if (snapshot.Count > 1)
                snapshot.Sort((a, b) => b.sub.Priority.CompareTo(a.sub.Priority));
            return snapshot;
        }

        private static RuntimeValue BuildResultValue(
            EventDescriptor desc, bool cancelled, List<string>? errors,
            EventSubscriptionValue source, Context context)
        {
            if (desc.IsTolerant)
            {
                var errList = new List<RuntimeValue>(errors!.Count);
                foreach (var msg in errors) errList.Add(new StringValue(msg));
                var listVal = (RuntimeValue)new ListValue(errList);
                if (desc.IsCancellable)
                {
                    var tup = new List<RuntimeValue> { BooleanValue.Of(cancelled), listVal };
                    return new TupleValue(tup)
                        .SetContext(context).SetPos(source.PositionStart, source.PositionEnd);
                }
                return listVal
                    .SetContext(context).SetPos(source.PositionStart, source.PositionEnd);
            }
            if (desc.IsCancellable)
            {
                return BooleanValue.Of(cancelled)
                    .SetContext(context).SetPos(source.PositionStart, source.PositionEnd);
            }
            return NullValue.Null
                .SetContext(context).SetPos(source.PositionStart, source.PositionEnd);
        }

        // Emit a deprecation warning on raise if the event carries
        // @deprecated. Matches the existing convention used by callables.
        private static void EmitDeprecationWarning(string metaKey, EventDescriptor desc, Position pos)
        {
            var anns = MetadataRegistry.Global.GetByKey(metaKey);
            if (anns == null) return;
            foreach (var a in anns)
            {
                if (string.Equals(a.DefinitionName, BuiltInAnnotations.Deprecated, StringComparison.Ordinal))
                {
                    string reason = "";
                    if (a.NamedArgs.TryGetValue("reason", out var r) && r is StringValue sv)
                        reason = $" — {sv.Value}";
                    System.Console.Error.WriteLine($"warning: event '{desc.DeclaringTypeName}.{desc.Name}' is deprecated{reason}");
                    return;
                }
            }
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        private static int ToInt(NumberValue n)
        {
            try
            {
                if (!n.Value.Scale.IsZero) return 0;
                return (int)n.Value.Unscaled;
            }
            catch { return 0; }
        }

        private static NumberValue MakeInt(int v)
            => new NumberValue(new BigNumber(new BigInteger(v), BigInteger.Zero));

        // Mirrors MemberAccessHelper.IsInsideSameType +
        // IsInsideClassHierarchy. Returns true when the caller's `self`
        // is an instance of (or derives from) the event's declaring
        // type, OR when the caller is a static method on the declaring
        // type (the static-context probe uses ClassTypeValue on `self`
        // — there is no static `self`, so this branch returns false for
        // static contexts; static raise is still permitted because the
        // caller goes through MyType.Event(args) and MemberAccessHelper
        // does its own visibility check via IsInsideClassHierarchy).
        private static bool IsCallerInsideDeclaringType(Context context, string declaringTypeName)
        {
            // Three checks, in order of cheapness:
            //   1. CurrentClassMethodOwner (lexical) — set when entering
            //      a class method body, instance or static. Inherited
            //      across Context.Copy, so nested scopes within a method
            //      body keep the lexical owner. This covers static raise
            //      from MyClass.fn().
            //   2. Current `self` — typical instance-method case.
            //   3. Walk parent contexts for `self` — handles lambdas
            //      executing inside a method body.
            var ctx = context;
            while (ctx != null)
            {
                if (ctx.CurrentClassMethodOwner != null)
                {
                    if (string.Equals(ctx.CurrentClassMethodOwner.ClassName, declaringTypeName, StringComparison.Ordinal))
                        return true;
                    if (ctx.CurrentClassMethodOwner.InheritsFrom(declaringTypeName)) return true;
                }
                ctx = ctx.Parent;
            }

            var selfEntry = context.SymbolTable?.GetEntry("self");
            if (selfEntry != null && Match(selfEntry.Value, declaringTypeName)) return true;

            var parent = context.Parent;
            while (parent != null)
            {
                var pself = parent.SymbolTable?.GetEntry("self");
                if (pself != null && Match(pself.Value, declaringTypeName)) return true;
                parent = parent.Parent;
            }
            return false;

            static bool Match(RuntimeValue v, string typeName)
            {
                if (v.Type == RuntimeValueType.StructInstance || v.Type == RuntimeValueType.RecordInstance)
                    return string.Equals(((StructInstanceValue)v).Definition.StructName, typeName, StringComparison.Ordinal);
                if (v.Type == RuntimeValueType.ClassInstance)
                {
                    var inst = (ClassInstanceValue)v;
                    if (string.Equals(inst.Definition.ClassName, typeName, StringComparison.Ordinal)) return true;
                    return inst.Definition.InheritsFrom(typeName);
                }
                return false;
            }
        }
    }
}

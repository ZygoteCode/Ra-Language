using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Runtime.Properties;
using RaLanguage.Interpreter.Values;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Runtime
{
    // Cross-cutting helpers for routing member access / assignment
    // through the extension registry. Centralises the priority order
    // (native member → extension property → extension method) and the
    // visibility rules that apply when the member came from an
    // extension declaration rather than the type body.
    public static class ExtensionDispatch
    {
        // Attempts a read on an extension property registered for
        // `target`. Returns false when no matching property exists,
        // so the caller can continue down the dispatch chain (e.g.
        // to extension method group).
        public static bool TryGetProperty(
            RuntimeValue target,
            string memberName,
            Context context,
            Position posStart,
            Position posEnd,
            out RuntimeResult result)
        {
            var entry = context.Extensions.ResolvePropertyEntry(target, memberName);
            if (entry == null)
            {
                result = default!;
                return false;
            }

            // Local extensions are treated as "inside" so per-accessor
            // privacy gates (priv get/priv set) only fire against
            // imported ones — matching the intuition that the
            // declaring module is privileged.
            bool isInside = entry.IsLocal;
            result = PropertyAccessOps.Get(target, entry.Descriptor, context, posStart, posEnd, isInside);
            return true;
        }

        // Attempts a write on an extension property. Mirrors
        // TryGetProperty. Async because PropertyAccessOps.Set is
        // async (custom setters may await).
        public static async ValueTask<(bool handled, RuntimeResult result)> TrySetPropertyAsync(
            RuntimeValue target,
            string memberName,
            RuntimeValue value,
            Context context,
            Position posStart,
            Position posEnd)
        {
            var entry = context.Extensions.ResolvePropertyEntry(target, memberName);
            if (entry == null)
                return (false, default!);

            bool isInside = entry.IsLocal;
            var r = await PropertyAccessOps.Set(target, entry.Descriptor, value, context, posStart, posEnd, isInside, isInitContext: false);
            return (true, r);
        }

        // Sync-projection variant used by call-sites that already
        // collapse async via SyncAwait. Matches the existing
        // MemberAssignmentHelper pattern.
        public static bool TrySetProperty(
            RuntimeValue target,
            string memberName,
            RuntimeValue value,
            Context context,
            Position posStart,
            Position posEnd,
            out RuntimeResult result)
        {
            var entry = context.Extensions.ResolvePropertyEntry(target, memberName);
            if (entry == null)
            {
                result = default!;
                return false;
            }

            bool isInside = entry.IsLocal;
            var setTask = PropertyAccessOps.Set(target, entry.Descriptor, value, context, posStart, posEnd, isInside, isInitContext: false);
            result = setTask.IsCompletedSuccessfully ? setTask.Result : SyncAwait.Get(setTask);
            return true;
        }

        // Probes the registry for an event declared via `extend T {
        // event Name(...) }`. Returns an EventSubscriptionValue
        // wrapping (target, descriptor) — subscriber storage piggy-
        // backs on `instance.EventSubs[name]`, the same dict used by
        // native events; the names just have to not collide with a
        // native one (which would have shadowed this probe).
        // Reads an extension field. On first touch the descriptor's
        // default-value expression is evaluated lazily (with `self`
        // bound to the receiver) and the slot is populated. Subsequent
        // reads are pure array-index lookups — no dictionary, no
        // hashing.
        public static bool TryGetField(
            RuntimeValue target,
            string memberName,
            Context context,
            Position posStart,
            Position posEnd,
            out RuntimeResult result)
        {
            var entry = context.Extensions.ResolveFieldEntry(target, memberName);
            if (entry == null)
            {
                result = default!;
                return false;
            }
            result = DispatchFieldGet(target, entry, context, posStart, posEnd);
            return true;
        }

        // Same as TryGetField but starts from an already-resolved
        // entry. Used by the IC-priming slow path in MemberAccessHelper
        // so we don't pay the registry walk twice (once to detect the
        // hit, once to dispatch). Public so it can be exercised by
        // tests and future IC variants on other receiver shapes.
        public static RuntimeResult DispatchFieldGet(
            RuntimeValue target,
            ExtensionFieldEntry entry,
            Context context,
            Position posStart,
            Position posEnd)
        {
            var desc = entry.Descriptor;

            bool isInside = entry.IsLocal;
            if (!isInside && !desc.IsPublic)
            {
                var resPriv = new RuntimeResult();
                return resPriv.Failure(new RuntimeError(posStart, posEnd,
                    $"extension field '{desc.DeclaringTypeName}.{desc.Name}' is private to its declaring module",
                    context));
            }

            var slots = ExtensionFieldStorage.GetSlotsOrNull(target);
            RuntimeValue? stored = null;
            if (slots != null && desc.SlotIndex < slots.Length)
                stored = slots[desc.SlotIndex];

            bool initialized = ExtensionFieldStorage.IsInitialized(target, desc.SlotIndex);

            if (initialized && stored != null)
            {
                var ret = stored.IsCopy ? stored.Copy() : stored;
                var resOk = new RuntimeResult();
                return resOk.Success(ret.SetContext(context).SetPos(posStart, posEnd));
            }

            // Lazy default evaluation. `self` is bound for the eval so
            // the default expression can read instance state.
            if (desc.DefaultValueNode != null)
            {
                // Re-entrant access during a lazy initialiser raises a
                // dedicated error so the user sees the cycle (mirrors
                // PropertyAccessOps lazy gating).
                if (desc.IsLazy)
                {
                    if (ExtensionFieldStorage.IsLazyInitializing(target, desc.SlotIndex))
                    {
                        var resCycle = new RuntimeResult();
                        return resCycle.Failure(new RuntimeError(posStart, posEnd,
                            $"recursive access to lazy extension field '{desc.DeclaringTypeName}.{desc.Name}' during its own initialization",
                            context));
                    }
                    ExtensionFieldStorage.MarkLazyInitializing(target, desc.SlotIndex, true);
                }

                var inner = context.Copy();
                inner.SymbolTable!.Set("self", target);
                var vt = new Interpreter().Visit(desc.DefaultValueNode, inner);
                var bodyRes = vt.IsCompletedSuccessfully ? vt.Result : SyncAwait.Get(vt);

                if (desc.IsLazy)
                    ExtensionFieldStorage.MarkLazyInitializing(target, desc.SlotIndex, false);

                if (bodyRes.Error != null)
                {
                    var resE = new RuntimeResult();
                    return resE.Failure(bodyRes.Error);
                }
                var v = bodyRes.Value ?? Values.Primitives.NullValue.Null;
                WriteSlot(target, desc.SlotIndex, v);
                ExtensionFieldStorage.MarkInitialized(target, desc.SlotIndex);

                var ret = v.IsCopy ? v.Copy() : v;
                var resOk = new RuntimeResult();
                return resOk.Success(ret.SetContext(context).SetPos(posStart, posEnd));
            }

            // No default + no prior write → null.
            var nullV = Values.Primitives.NullValue.Null.SetContext(context).SetPos(posStart, posEnd);
            var resN = new RuntimeResult();
            return resN.Success(nullV);
        }

        // Writes an extension field. Honors const / let / final
        // single-shot rules and runs IsAssignable against the declared
        // type when present.
        public static bool TrySetField(
            RuntimeValue target,
            string memberName,
            RuntimeValue value,
            Context context,
            Position posStart,
            Position posEnd,
            out RuntimeResult result)
        {
            var entry = context.Extensions.ResolveFieldEntry(target, memberName);
            if (entry == null)
            {
                result = default!;
                return false;
            }

            var desc = entry.Descriptor;
            bool isInside = entry.IsLocal;
            if (!isInside && !desc.IsPublic)
            {
                var resPriv = new RuntimeResult();
                result = resPriv.Failure(new RuntimeError(posStart, posEnd,
                    $"extension field '{desc.DeclaringTypeName}.{desc.Name}' is private to its declaring module",
                    context));
                return true;
            }

            if (desc.IsConst)
            {
                var resE = new RuntimeResult();
                result = resE.Failure(new RuntimeError(posStart, posEnd,
                    $"extension field '{desc.DeclaringTypeName}.{desc.Name}' is const and cannot be reassigned",
                    context));
                return true;
            }

            if ((desc.IsLet || desc.IsFinal) && ExtensionFieldStorage.IsInitialized(target, desc.SlotIndex))
            {
                var resE = new RuntimeResult();
                result = resE.Failure(new RuntimeError(posStart, posEnd,
                    $"extension field '{desc.DeclaringTypeName}.{desc.Name}' is {(desc.IsLet ? "let" : "final")} and was already assigned",
                    context));
                return true;
            }

            if (desc.FieldType != null && !Types.TypeSystem.IsAssignable(context, desc.FieldType, value))
            {
                var resE = new RuntimeResult();
                result = resE.Failure(new RuntimeError(posStart, posEnd,
                    $"type mismatch assigning to extension field '{desc.DeclaringTypeName}.{desc.Name}': expected {desc.FieldType}, got {value.Type}",
                    context));
                return true;
            }

            WriteSlot(target, desc.SlotIndex, value);
            ExtensionFieldStorage.MarkInitialized(target, desc.SlotIndex);

            var resOk = new RuntimeResult();
            result = resOk.Success(value.SetContext(context).SetPos(posStart, posEnd));
            return true;
        }

        private static void WriteSlot(RuntimeValue target, int slot, RuntimeValue value)
        {
            var slots = ExtensionFieldStorage.GetOrCreateSlots(target, slot);
            slots[slot] = value.IsCopy ? value.Copy() : value;
        }

        public static bool TryGetEvent(
            RuntimeValue target,
            string memberName,
            Context context,
            Position posStart,
            Position posEnd,
            out RuntimeResult result)
        {
            var entry = context.Extensions.ResolveEventEntry(target, memberName);
            if (entry == null)
            {
                result = default!;
                return false;
            }

            // Visibility: imported events with non-public subscribe
            // are filtered at import time; local events honour
            // descriptor.SubscribeIsPublic. The result is the same as
            // calling: "if this entry made it into the registry, it's
            // visible here."
            var subscription = new Values.Events.EventSubscriptionValue(target, entry.Descriptor)
                .SetContext(context)
                .SetPos(posStart, posEnd);
            var res = new RuntimeResult();
            result = res.Success(subscription);
            return true;
        }
    }
}

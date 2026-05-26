using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Events;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Values.Events
{
    // Runtime value returned by `obj.MyEvent` (and `MyClass.MyEvent` for
    // statics). Carries the owner (instance OR type value) and the
    // resolved EventDescriptor; the value itself is *callable* — its
    // Execute(args) performs the raise, gated by raise-visibility.
    //
    // The synthetic accessor methods (`on`, `off`, `clear`, `count`)
    // are not exposed as bound methods on this value directly — the
    // MemberAccessHelper recognises the (EventSubscription, methodName)
    // pair and returns a BoundEventMethodValue. That keeps the runtime
    // routing dense and avoids a per-event allocation of four bound
    // method handles.
    //
    // Aliasing: this value is a *handle* to per-instance subscriber
    // state; reads must alias (return `this`), never copy. IsCopy=false.
    public sealed class EventSubscriptionValue : BaseFunctionValue
    {
        // Either a ClassInstance / RecordInstance (instance events) or a
        // ClassType / RecordType (static events). The MemberAccess
        // pipeline guarantees we never reach here with anything else.
        public RuntimeValue Owner { get; }
        public EventDescriptor Descriptor { get; }

        public override RuntimeValueType Type => RuntimeValueType.EventSubscription;
        public override bool IsCopy => false;

        public EventSubscriptionValue(RuntimeValue owner, EventDescriptor descriptor)
            : base(descriptor.Name)
        {
            Owner = owner;
            Descriptor = descriptor;
        }

        public override RuntimeValue Copy() => this;

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            // Raise visibility is enforced inside EventAccessOps.Raise
            // using the caller's Context (passed via context-sensitive
            // dispatch in MemberAccessHelper). For a direct Execute call
            // (no Context surface — e.g. when stored in a variable and
            // invoked via FunctionCall on a non-member receiver) we
            // assume the caller has already passed the visibility check;
            // if raise is private and the call site is outside the
            // declaring type, MemberAccessHelper would have rejected the
            // initial read.
            //
            // This is a defensive design: the *primary* visibility check
            // happens in the call path that produced this value.
            return await RaLanguage.Interpreter.Runtime.Events.EventAccessOps.RaiseDirect(
                this, args, Context);
        }

        public override string ToString()
            => $"<event {Descriptor.DeclaringTypeName}.{Descriptor.Name}>";
    }
}

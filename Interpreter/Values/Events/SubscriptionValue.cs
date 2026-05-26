using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Events;

namespace RaLanguage.Interpreter.Values.Events
{
    // Stable token returned by `event.on(handler)`. The user passes it
    // back to `event.off(sub)` to remove the handler — works even when
    // the handler is a lambda whose identity is otherwise irrecoverable.
    //
    // The value is a plain handle. It exposes two synthetic methods
    // (`dispose`, `is_active`) routed through the member-access pipeline
    // exactly like the four EventSubscriptionValue methods, so the
    // surface is uniform across the events runtime.
    public sealed class SubscriptionValue : RuntimeValue
    {
        public EventSubscriptionValue Source { get; }
        public long Token { get; }
        // Mirror of EventSubscription.Disposed updated when the user
        // calls .dispose() or when the underlying entry is removed by
        // off-by-handler / clear.
        public bool Disposed { get; set; }

        public override RuntimeValueType Type => RuntimeValueType.Subscription;
        public override bool IsCopy => false;

        public SubscriptionValue(EventSubscriptionValue source, long token)
        {
            Source = source;
            Token = token;
        }

        public override RuntimeValue Copy() => this;

        public override string ToString()
            => $"<subscription {Source.Descriptor.DeclaringTypeName}.{Source.Descriptor.Name}#{Token}>";
    }
}

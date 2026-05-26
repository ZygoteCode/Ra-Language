using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Events;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Events
{
    public enum EventMethodKind
    {
        On,
        Off,
        Clear,
        Count
    }

    public enum SubscriptionMethodKind
    {
        Dispose,
        IsActive
    }

    // Synthetic bound method value returned by member access on
    // EventSubscriptionValue for one of the four accessor methods.
    // Execution routes to EventAccessOps.
    public sealed class BoundEventMethodValue : BaseFunctionValue
    {
        public EventSubscriptionValue Source { get; }
        public EventMethodKind Method { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;
        public override bool IsCopy => false;

        public BoundEventMethodValue(EventSubscriptionValue source, EventMethodKind method)
            : base(method.ToString().ToLowerInvariant())
        {
            Source = source;
            Method = method;
        }

        public override RuntimeValue Copy() => this;

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            return await EventAccessOps.InvokeAccessor(Source, Method, args, Context);
        }

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs)
        {
            return await EventAccessOps.InvokeAccessor(Source, Method, positionalArgs, Context, namedArgs);
        }

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs)
        {
            return await EventAccessOps.InvokeAccessor(Source, Method, positionalArgs, Context, namedArgs);
        }
    }

    // Same idea for the SubscriptionValue's two methods.
    public sealed class BoundSubscriptionMethodValue : BaseFunctionValue
    {
        public SubscriptionValue Source { get; }
        public SubscriptionMethodKind Method { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;
        public override bool IsCopy => false;

        public BoundSubscriptionMethodValue(SubscriptionValue source, SubscriptionMethodKind method)
            : base(method.ToString().ToLowerInvariant())
        {
            Source = source;
            Method = method;
        }

        public override RuntimeValue Copy() => this;

        public override ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            return new ValueTask<RuntimeResult>(
                EventAccessOps.InvokeSubscriptionAccessor(Source, Method, args, Context));
        }
    }
}

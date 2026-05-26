using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Events;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Records;
using RaLanguage.Interpreter.Values.Structs;

namespace RaLanguage.Interpreter.Values.Events
{
    // First-class unbound event reference, produced by `Class.Event` /
    // `RecordClass.Event` syntax when Event is an instance event (not
    // static). Callable: `ref(instance)` returns the bound
    // EventSubscriptionValue for that instance.
    //
    // The owner-type pointer carried alongside the descriptor lets us
    // check at bind time that the instance actually inherits/implements
    // the type — guards against `Button.Click(otherInstance)` where
    // otherInstance is unrelated.
    //
    // Use cases:
    //   - Generic subscribers that take an EventRef + handler:
    //       fn add_log<T>(r: EventRef, w: T, msg: string) {
    //           r(w).on(fn() { print(msg) })
    //       }
    //   - MVC binding patterns where a parent component holds refs to
    //     a set of named events on children.
    public sealed class EventRefValue : BaseFunctionValue
    {
        public RuntimeValue OwnerType { get; }    // ClassTypeValue or RecordTypeValue
        public EventDescriptor Descriptor { get; }

        public override RuntimeValueType Type => RuntimeValueType.EventRef;
        public override bool IsCopy => false;

        public EventRefValue(RuntimeValue ownerType, EventDescriptor descriptor)
            : base($"{descriptor.DeclaringTypeName}::{descriptor.Name}")
        {
            OwnerType = ownerType;
            Descriptor = descriptor;
        }

        public override RuntimeValue Copy() => this;

        // ref(instance) → EventSubscriptionValue. Other arg counts /
        // mismatched instance types are runtime errors so the failure
        // mode is loud (rather than silently returning a no-op handle).
        public override ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();
            if (args.Count != 1)
            {
                return new ValueTask<RuntimeResult>(res.Failure(new RuntimeError(
                    PositionStart, PositionEnd,
                    $"event reference '{Name}' expects exactly one argument (the instance); got {args.Count}",
                    Context)));
            }

            var inst = args[0];
            string declType = Descriptor.DeclaringTypeName;

            bool ok = inst.Type switch
            {
                RuntimeValueType.ClassInstance =>
                    string.Equals(((ClassInstanceValue)inst).Definition.ClassName, declType, System.StringComparison.Ordinal)
                    || ((ClassInstanceValue)inst).Definition.InheritsFrom(declType),
                RuntimeValueType.StructInstance =>
                    string.Equals(((StructInstanceValue)inst).Definition.StructName, declType, System.StringComparison.Ordinal),
                RuntimeValueType.RecordInstance =>
                    string.Equals(((RecordInstanceValue)inst).Definition.StructName, declType, System.StringComparison.Ordinal),
                _ => false
            };

            if (!ok)
            {
                return new ValueTask<RuntimeResult>(res.Failure(new RuntimeError(
                    PositionStart, PositionEnd,
                    $"event reference '{Name}' cannot be bound to value of type {inst.Type} — the instance must be (or inherit from) '{declType}'",
                    Context)));
            }

            var sub = new EventSubscriptionValue(inst, Descriptor)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
            return new ValueTask<RuntimeResult>(res.Success(sub));
        }

        public override string ToString()
            => $"<event-ref {Descriptor.DeclaringTypeName}::{Descriptor.Name}>";
    }
}

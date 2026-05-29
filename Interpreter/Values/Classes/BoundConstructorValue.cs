using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    // A first-class, callable handle to a NAMED constructor (generative or
    // factory) — what member access yields for `Type.name`. It is a thin
    // thunk: it carries the owning class and the constructor name, captures the
    // call-site context (set by MemberAccessHelper via SetContext, used for
    // private-constructor visibility), and on invocation delegates straight to
    // ClassTypeValue.Construct, which performs overload resolution + visibility
    // + generative/factory dispatch. No instance is held — the receiver of a
    // constructor is the type itself.
    //
    // Deliberately NOT inline-cached: overload resolution and visibility both
    // depend on the live arguments and the live call site, and construction
    // dominates the cost anyway, so member access re-creates the thunk on each
    // hit rather than pinning a stale context.
    public sealed class BoundConstructorValue : BaseFunctionValue
    {
        public ClassTypeValue Definition { get; }
        public string CtorName { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BoundConstructorValue(ClassTypeValue definition, string ctorName)
            : base($"{definition.ClassName}.{ctorName}")
        {
            Definition = definition;
            CtorName = ctorName;
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal));

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
            => await ExecuteWithNamedArgs(positionalArgs, namedArgs, null);

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs)
            => await Definition.Construct(positionalArgs, namedArgs, explicitTypeArgs, CtorName, Context!, PositionStart, PositionEnd);

        public override bool IsCopy => false;

        public override RuntimeValue Copy()
            => new BoundConstructorValue(Definition, CtorName)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<constructor {Definition.ClassName}.{CtorName}>";
    }
}

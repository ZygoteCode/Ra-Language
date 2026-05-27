using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    // Function composition `g . f`, equivalent to the Ra pipeline shape
    // `x |> f |> g`. Calling `compose(f, g)(x)` runs `f(x)` first, then
    // pipes the single result into `g(result)`.
    //
    // Stateless beyond the two refs. One allocation at construction.
    // Per-call cost is two Execute dispatches. The composed value is
    // itself a BaseFunctionValue, so it can be passed wherever an
    // `fn(...) -> R` is expected; chaining compose() multiple times is
    // legal and just builds a deeper tree.
    public sealed class ComposedFunctionValue : BaseFunctionValue
    {
        public BaseFunctionValue Inner { get; }   // applied first
        public BaseFunctionValue Outer { get; }   // applied to inner's result

        public override RuntimeValueType Type => RuntimeValueType.ComposedFunction;
        public override bool IsCopy => false;

        public ComposedFunctionValue(BaseFunctionValue inner, BaseFunctionValue outer)
            : base($"compose({inner?.Name ?? "?"}, {outer?.Name ?? "?"})")
        {
            Inner = inner;
            Outer = outer;
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();
            var innerRes = await Inner.Execute(args);
            if (innerRes.Error != null) return res.Failure(innerRes.Error);
            var mid = innerRes.Value ?? NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);
            return await Outer.Execute(new List<RuntimeValue> { mid });
        }

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs)
        {
            var res = new RuntimeResult();
            var innerRes = await Inner.ExecuteWithNamedArgs(positionalArgs, namedArgs, explicitTypeArgs);
            if (innerRes.Error != null) return res.Failure(innerRes.Error);
            var mid = innerRes.Value
                ?? innerRes.FuncReturnValue
                ?? NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);
            return await Outer.Execute(new List<RuntimeValue> { mid });
        }

        public override RuntimeValue Copy() => this;

        public override string ToString() => $"<compose {Inner.Name} >> {Outer.Name}>";
    }
}

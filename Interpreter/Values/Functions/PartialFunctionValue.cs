using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    // Captures a target callable plus a prefix of bound positional args.
    // Calling the partial value forwards: bound_args ++ user_args.
    //
    //   let add = fn(a: int, b: int) -> int { ret a + b }
    //   let inc = partial(add, 1)      // PartialFunctionValue(add, [1])
    //   inc(5)                          // -> 6
    //
    // Named-arg merging follows the same rule: bound named args win at
    // construction; user-side named args fill the remaining slots.
    //
    // Allocates once at construction; per-call cost is one extra Execute
    // hop. The inner CALL goes through the standard CALL path so any
    // optimisation already there (IC, overload PIC, frame pooling)
    // applies for free.
    public sealed class PartialFunctionValue : BaseFunctionValue
    {
        public BaseFunctionValue Target { get; }
        public List<RuntimeValue> BoundPositional { get; }
        public Dictionary<string, RuntimeValue> BoundNamed { get; }

        public override RuntimeValueType Type => RuntimeValueType.PartialFunction;
        public override bool IsCopy => false;

        public PartialFunctionValue(
            BaseFunctionValue target,
            List<RuntimeValue> boundPositional,
            Dictionary<string, RuntimeValue>? boundNamed = null)
            : base($"partial({target?.Name ?? "?"})")
        {
            Target = target;
            BoundPositional = boundPositional ?? new List<RuntimeValue>();
            BoundNamed = boundNamed ?? new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal);
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            var combined = new List<RuntimeValue>(BoundPositional.Count + (args?.Count ?? 0));
            combined.AddRange(BoundPositional);
            if (args != null) combined.AddRange(args);
            return await Target.Execute(combined);
        }

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs)
        {
            var combinedPos = new List<RuntimeValue>(BoundPositional.Count + (positionalArgs?.Count ?? 0));
            combinedPos.AddRange(BoundPositional);
            if (positionalArgs != null) combinedPos.AddRange(positionalArgs);

            Dictionary<string, RuntimeValue> combinedNamed;
            if (BoundNamed.Count == 0)
            {
                combinedNamed = namedArgs ?? new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal);
            }
            else
            {
                combinedNamed = new Dictionary<string, RuntimeValue>(BoundNamed, System.StringComparer.Ordinal);
                if (namedArgs != null)
                {
                    foreach (var kv in namedArgs) combinedNamed[kv.Key] = kv.Value;
                }
            }

            return await Target.ExecuteWithNamedArgs(combinedPos, combinedNamed, explicitTypeArgs);
        }

        public override RuntimeValue Copy() => this;

        public override string ToString() => $"<partial {Target.Name} +{BoundPositional.Count} bound>";
    }
}

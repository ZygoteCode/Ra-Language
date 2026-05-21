using System.Threading.Tasks;
using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Values.Functions
{
    // Callable value behind a payload-carrying enum variant. `Result.Ok(x)`
    // resolves the access to one of these, then invokes it with the call args.
    //
    // Constructed lazily by EnumTypeValue.GetMember; one-shot, no caching
    // required because the lifecycle is bounded by the call expression's
    // evaluation scope.
    public sealed class EnumVariantConstructor : BaseFunctionValue
    {
        public EnumTypeValue OwnerType { get; }
        public EnumVariantInfo Variant { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public EnumVariantConstructor(EnumTypeValue ownerType, EnumVariantInfo variant)
            : base($"{ownerType.EnumName}.{variant.Name}")
        {
            OwnerType = ownerType;
            Variant = variant;
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();

            int expected = Variant.Arity;
            if (args.Count != expected)
            {
                return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                    $"variant '{OwnerType.EnumName}.{Variant.Name}' expects {expected} payload value(s), got {args.Count}",
                    Context,
                    code: DiagnosticCode.RuntimeTypeMismatch,
                    primaryLabel: "wrong number of payload values",
                    help: expected == 0
                        ? "this variant takes no payload; reference it as just '" + OwnerType.EnumName + "." + Variant.Name + "'"
                        : $"call as '{OwnerType.EnumName}.{Variant.Name}(...)' with exactly {expected} value(s)"));
            }

            // Defensive copy: variants are immutable, so we snapshot the
            // payload list. Each entry is .Copy()ed to keep ownership clean
            // (callers may mutate locals after construction).
            var payload = new RuntimeValue[expected];
            for (int i = 0; i < expected; i++)
            {
                payload[i] = args[i].Copy();
            }

            var value = new EnumValue(
                OwnerType.EnumName,
                Variant.Name,
                Variant.Index,
                Variant.UnderlyingValue,
                payload)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            return res.Success(value);
        }

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<RaLanguage.Types.TypeDescriptor?>? explicitTypeArgs)
        {
            if (namedArgs != null && namedArgs.Count > 0)
            {
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd,
                    $"variant '{OwnerType.EnumName}.{Variant.Name}' does not accept named arguments",
                    Context,
                    code: DiagnosticCode.RuntimeTypeMismatch,
                    primaryLabel: "named arguments not supported for enum variants",
                    help: "pass payload positionally, e.g. 'Result.Ok(value)'"));
            }
            return await Execute(positionalArgs);
        }

        public override RuntimeValue Copy() => this;

        public override string ToString() => $"<variant-ctor {Name}/{Variant.Arity}>";
    }
}

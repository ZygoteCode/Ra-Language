using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions.Predicates
{
    // The callable produced by member access on a predicate — `p.xor`,
    // `p.implies`, `p.iff`, `p.negate`, `p.test`. MemberAccessHelper returns
    // one of these (a BaseFunctionValue), then the call site applies its
    // argument list, exactly like a bound struct / class method.
    //
    // `and` / `or` / `not` are Ra keywords and so cannot be member names —
    // those three are the operators `&` / `|` / `!`. The method surface
    // therefore covers the operator-less combinators plus `negate()` (the
    // discoverable spelling of `!p`) and `test(x)` (the explicit spelling of
    // `p(x)`).
    public sealed class BoundPredicateMethodValue : BaseFunctionValue
    {
        public PredicateValue Receiver { get; }
        public string Method { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;
        public override bool IsCopy => false;

        public BoundPredicateMethodValue(PredicateValue receiver, string method)
            : base($"{receiver.Name}.{method}")
        {
            Receiver = receiver;
            Method = method;
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();

            switch (Method)
            {
                case "negate":
                    if (args.Count != 0) return res.Failure(Arity("negate", 0, args.Count));
                    return res.Success(Receiver.Not());

                case "test":
                    // Explicit application: `p.test(x)` == `p(x)`. Forwards the
                    // full argument list so multi-arg predicates still work.
                    return await Receiver.Execute(args);

                case "xor":
                case "implies":
                case "iff":
                {
                    if (args.Count != 1) return res.Failure(Arity(Method, 1, args.Count));
                    var other = PredicateValue.Lift(args[0]);
                    if (other == null) return res.Failure(NotCallable(args[0]));
                    PredicateValue result = Method switch
                    {
                        "xor" => Receiver.Xor(other),
                        "implies" => Receiver.Implies(other),
                        _ => Receiver.Iff(other),
                    };
                    return res.Success(result);
                }
            }

            return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                $"unknown predicate method '{Method}'", Context!));
        }

        public override ValueTask<RuntimeResult> ExecuteWithNamedArgs(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs)
            => Execute(positionalArgs);

        private Error Arity(string method, int expected, int got) =>
            new RuntimeError(PositionStart, PositionEnd,
                $"predicate method '{method}' expects {expected} argument(s), got {got}",
                Context!,
                code: DiagnosticCode.RuntimeGeneric,
                primaryLabel: "wrong number of arguments");

        private Error NotCallable(RuntimeValue v) =>
            new RuntimeError(PositionStart, PositionEnd,
                $"predicate method '{Method}' expects a predicate or `fn(T) -> bool`, got '{v.Type}'",
                Context!,
                code: DiagnosticCode.RuntimeGeneric,
                primaryLabel: "argument is not callable");

        public override RuntimeValue Copy() => this;

        public override string ToString() => $"<pred-method {Name}>";
    }
}

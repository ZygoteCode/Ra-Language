using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    // The callable produced by fluent method access on a collection —
    // `xs.map(f)`, `xs.filter(p)`, `xs.sort_by(k)`, … . Member access returns
    // one of these; the call site then applies the argument list. It is a thin
    // adapter that prepends the receiver and dispatches to the corresponding
    // free-function built-in (`map`, `filter`, `list_reverse`, …), so the
    // method form and the free form share ONE implementation and never drift.
    //
    // Fluent methods resolve only AFTER user `extend` methods, so a
    // BoundCollectionMethodValue never shadows a user-defined extension.
    public sealed class BoundCollectionMethodValue : BaseFunctionValue
    {
        public RuntimeValue Receiver { get; }
        public string Builtin { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;
        public override bool IsCopy => false;

        public BoundCollectionMethodValue(RuntimeValue receiver, string method, string builtin)
            : base(method)
        {
            Receiver = receiver;
            Builtin = builtin;
        }

        public override ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            var full = new List<RuntimeValue>(args.Count + 1) { Receiver };
            full.AddRange(args);
            return new ValueTask<RuntimeResult>(
                BuiltInRegistry.Invoke(Builtin, Context!, full, PositionStart, PositionEnd));
        }

        public override ValueTask<RuntimeResult> ExecuteWithNamedArgs(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs)
            => Execute(positionalArgs);

        public override RuntimeValue Copy() => this;
        public override string ToString() => $"<collection-method {Name}>";
    }
}

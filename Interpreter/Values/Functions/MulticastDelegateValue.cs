using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    // A flat ordered list of BaseFunctionValue handlers that all fire on
    // every Execute. The return value of the call is the return value of
    // the LAST handler — matches the "final-word-wins" rule documented
    // in RA_DELEGATES_DESIGN.md.
    //
    // Singleton fast path: BaseFunctionValue.AddedTo / SubbedBy collapse
    // single-element multicasts back into the raw handler so a normal
    // function value stays a normal function value, no wrapper alloc.
    //
    // Aliasing: a multicast handle is mutable state by reference — IsCopy
    // is false. Execute path keeps no per-call allocation beyond the
    // forwarded arg list.
    public sealed class MulticastDelegateValue : BaseFunctionValue
    {
        public List<BaseFunctionValue> Handlers { get; }

        public override RuntimeValueType Type => RuntimeValueType.MulticastDelegate;
        public override bool IsCopy => false;

        public MulticastDelegateValue(List<BaseFunctionValue> handlers)
            : base("<multicast>")
        {
            Handlers = handlers ?? new List<BaseFunctionValue>();
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();
            RuntimeValue? last = null;
            for (int i = 0; i < Handlers.Count; i++)
            {
                var h = Handlers[i];
                var inner = await h.Execute(args);
                if (inner.Error != null) return res.Failure(inner.Error);
                if (inner.Value != null) last = inner.Value;
            }
            if (last == null) last = NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);
            return res.Success(last);
        }

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs)
        {
            var res = new RuntimeResult();
            RuntimeValue? last = null;
            for (int i = 0; i < Handlers.Count; i++)
            {
                var h = Handlers[i];
                var inner = await h.ExecuteWithNamedArgs(positionalArgs, namedArgs, explicitTypeArgs);
                if (inner.Error != null) return res.Failure(inner.Error);
                if (inner.Value != null) last = inner.Value;
                else if (inner.FuncReturnValue != null) last = inner.FuncReturnValue;
            }
            if (last == null) last = NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);
            return res.Success(last);
        }

        public override RuntimeValue Copy() => this;

        public override string ToString() => $"<multicast {Handlers.Count} handler{(Handlers.Count == 1 ? "" : "s")}>";
    }
}

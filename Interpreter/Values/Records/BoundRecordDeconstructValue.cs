using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;

namespace RaLanguage.Interpreter.Values.Records
{
    // Callable wrapper produced by `record_instance.deconstruct` member
    // access. Captures the receiver so the invocation site can write
    // `point.deconstruct()` and observe a TupleValue of the primary
    // fields without paying the overhead of synthesizing an AST method
    // body at definition time.
    //
    // Returned only when the user did NOT define their own
    // `deconstruct` method on the record body — user methods always
    // win via the regular BoundStructMethodValue path. Pattern: the
    // member-access helper checks Definition.Methods first, falls
    // through to this synthetic when nothing matched.
    public sealed class BoundRecordDeconstructValue : RuntimeValue
    {
        public RecordInstanceValue Receiver { get; }

        public BoundRecordDeconstructValue(RecordInstanceValue receiver)
        {
            Receiver = receiver;
        }

        public override RuntimeValueType Type => RuntimeValueType.Function;
        public override bool IsCopy => false;
        public override RuntimeValue Copy() => this;

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            await Task.CompletedTask;
            var res = new RuntimeResult();
            if (args.Count != 0)
            {
                return res.Failure(new RuntimeError(
                    PositionStart, PositionEnd,
                    $"deconstruct() on record '{Receiver.Definition.StructName}' takes no arguments, got {args.Count}",
                    Context));
            }

            var tuple = Receiver.Deconstruct().SetContext(Context).SetPos(PositionStart, PositionEnd);
            return res.Success(tuple);
        }

        public override string ToString() => $"<bound-deconstruct {Receiver.Definition.StructName}>";
    }
}

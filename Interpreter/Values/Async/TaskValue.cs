using System.Threading.Tasks;
using System;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Async
{
    // M70: every TaskValue acquires an owner reference on its
    // backing `RaTaskCore`. The finalizer releases the reference; if
    // it was the last owner AND the task completed, the core
    // recycles into the M70 pool for the next `RaTaskCore.Rent`.
    // `Copy` constructs a new wrapper which itself bumps the count,
    // so multiple in-flight wrappers keep the core alive until the
    // last one is collected.
    public sealed class TaskValue : RuntimeValue
    {
        public RaTaskCore Core { get; }
        public TypeDescriptor? ElementType
        {
            get => Core.ElementType;
            set => Core.ElementType = value;
        }
        public override RuntimeValueType Type => RuntimeValueType.Task;
        public override bool IsCopy => true;

        public TaskValue(RaTaskCore core)
        {
            Core = core;
            core.AcquireOwner();
        }

        ~TaskValue()
        {
            try { Core?.ReleaseOwner(); } catch { /* finalizer must not throw */ }
        }

        public static TaskValue Completed(RuntimeValue? value) => new TaskValue(RaTaskCore.FromCompletedValue(value));
        public static TaskValue Faulted(Error error) => new TaskValue(RaTaskCore.FromError(error));

        public override RuntimeValue Copy()
        {
            return new TaskValue(Core).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other is TaskValue tv) return (new RaLanguage.Interpreter.Values.Primitives.BooleanValue(ReferenceEquals(Core, tv.Core)), null);
            return base.GetComparisonEq(other);
        }

        public override string ToString() => $"<task {Core.DebugName}#{Core.Id} {Core.Status}>";
        public override bool IsTrue() => !Core.IsCompleted;
    }
}

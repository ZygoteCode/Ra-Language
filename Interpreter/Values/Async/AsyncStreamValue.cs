using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Async
{
    public sealed class AsyncStreamValue : RuntimeValue
    {
        public AsyncStreamCore Core { get; }
        public TypeDescriptor? ElementType
        {
            get => Core.ElementType;
            set => Core.ElementType = value;
        }
        public override RuntimeValueType Type => RuntimeValueType.AsyncStream;
        public override bool IsCopy => true;

        public AsyncStreamValue(AsyncStreamCore core)
        {
            Core = core;
        }

        public override RuntimeValue Copy()
        {
            return new AsyncStreamValue(Core).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other is AsyncStreamValue sv) return (new RaLanguage.Interpreter.Values.Primitives.BooleanValue(ReferenceEquals(Core, sv.Core)), null);
            return base.GetComparisonEq(other);
        }

        public override string ToString() => "<async-stream>";
        public override bool IsTrue() => true;
    }
}

using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Async
{
    public sealed class ChannelValue : RuntimeValue
    {
        public AsyncChannel Channel { get; }
        // Surfaces the element type stored on the underlying channel so it
        // survives the IsCopy=true wrapping each variable access performs.
        public TypeDescriptor? ElementType
        {
            get => Channel.ElementType;
            set => Channel.ElementType = value;
        }
        public override RuntimeValueType Type => RuntimeValueType.Channel;
        public override bool IsCopy => true;

        public ChannelValue(AsyncChannel channel)
        {
            Channel = channel;
        }

        public override RuntimeValue Copy()
        {
            return new ChannelValue(Channel).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override (RuntimeValue?, Error?) GetComparisonEq(RuntimeValue other)
        {
            if (other is ChannelValue cv) return (new RaLanguage.Interpreter.Values.Primitives.BooleanValue(ReferenceEquals(Channel, cv.Channel)), null);
            return base.GetComparisonEq(other);
        }

        public override string ToString() => $"<channel cap={Channel.Capacity} count={Channel.Count} closed={Channel.IsClosed}>";
        public override bool IsTrue() => !Channel.IsClosed;
    }
}

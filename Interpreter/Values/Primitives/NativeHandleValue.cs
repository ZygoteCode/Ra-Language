using System.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RaLanguage.Errors;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public enum NativeHandleKind
    {
        Library,
        Memory,
        Pointer,
        Symbol,
        ProcessHandle,
        Pinned,
        Generic
    }

    public enum NativeHandleOwnership
    {
        /// <summary>This handle owns its memory and is responsible for freeing it.</summary>
        Owned,
        /// <summary>Caller borrowed this handle; freeing it is forbidden.</summary>
        Borrowed,
        /// <summary>Loaned from a callee with a contract about who frees and when.</summary>
        Loaned
    }

    /// <summary>
    /// Shared mutable state across all NativeHandleValue copies that alias the same
    /// underlying native resource. The first MarkDispose flips Disposed=true and runs
    /// the actual native free; subsequent disposes hit DoubleFreesDetected.
    /// </summary>
    public sealed class NativeHandleSharedState
    {
        public bool Disposed;
        public int Generation;
    }

    public sealed class NativeHandleValue : RuntimeValue
    {
        public IntPtr Handle { get; private set; }
        public NativeHandleKind Kind { get; }
        public long ByteSize { get; }
        public string? Description { get; }
        public bool OwnsMemory => Ownership == NativeHandleOwnership.Owned && !IsDisposed;
        public NativeHandleOwnership Ownership { get; }
        public bool IsDisposed => SharedState.Disposed;
        public int Generation => SharedState.Generation;
        public GCHandle? PinnedGCHandle { get; }
        public NativeHandleSharedState SharedState { get; }

        private static long _idCounter;
        public long Id { get; }

        private static readonly ConcurrentDictionary<long, NativeHandleValue> _alive = new();

        public static int DoubleFreesDetected;

        public NativeHandleValue(IntPtr handle, NativeHandleKind kind, long byteSize, string? description, bool ownsMemory)
            : this(handle, kind, byteSize, description,
                   ownsMemory ? NativeHandleOwnership.Owned : NativeHandleOwnership.Borrowed,
                   null, new NativeHandleSharedState())
        {
        }

        public NativeHandleValue(IntPtr handle, NativeHandleKind kind)
            : this(handle, kind, 0, null, NativeHandleOwnership.Borrowed, null, new NativeHandleSharedState())
        {
        }

        public NativeHandleValue(IntPtr handle, NativeHandleKind kind, long byteSize, string? description, NativeHandleOwnership ownership, GCHandle? pinned)
            : this(handle, kind, byteSize, description, ownership, pinned, new NativeHandleSharedState())
        {
        }

        private NativeHandleValue(IntPtr handle, NativeHandleKind kind, long byteSize, string? description, NativeHandleOwnership ownership, GCHandle? pinned, NativeHandleSharedState sharedState)
        {
            Handle = handle;
            Kind = kind;
            ByteSize = byteSize;
            Description = description;
            Ownership = ownership;
            PinnedGCHandle = pinned;
            SharedState = sharedState;
            Id = System.Threading.Interlocked.Increment(ref _idCounter);
            _alive[Id] = this;
        }

        public sealed override RuntimeValueType Type => RuntimeValueType.NativeHandle;
        public sealed override bool IsCopy => false;

        /// <summary>
        /// Mark this handle as disposed. Idempotent for safety, but increments
        /// DoubleFreesDetected when called more than once on an Owned handle —
        /// debugging surface for use-after-free / double-free bugs.
        /// </summary>
        public void MarkDisposed()
        {
            lock (SharedState)
            {
                if (SharedState.Disposed)
                {
                    System.Threading.Interlocked.Increment(ref DoubleFreesDetected);
                    return;
                }
                SharedState.Disposed = true;
                SharedState.Generation++;
            }
            Handle = IntPtr.Zero;
            if (PinnedGCHandle.HasValue)
            {
                try { PinnedGCHandle.Value.Free(); } catch { }
            }
            _alive.TryRemove(Id, out _);
        }

        public sealed override RuntimeValue Copy()
        {
            var copy = new NativeHandleValue(Handle, Kind, ByteSize, Description, Ownership, null, SharedState);
            return copy.SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public sealed override bool IsTrue() => Handle != IntPtr.Zero && !IsDisposed;

        public sealed override ValueResult GetComparisonEq(RuntimeValue other)
        {
            if (other is NativeHandleValue nh)
                return (BooleanValue.Of(Handle == nh.Handle && Kind == nh.Kind).SetContext(Context), null);
            return base.GetComparisonEq(other);
        }

        public sealed override ValueResult GetComparisonNe(RuntimeValue other)
        {
            var eq = GetComparisonEq(other).Item1;
            if (eq is BooleanValue b)
                return (BooleanValue.Of(!b.Value).SetContext(Context), null);
            return base.GetComparisonNe(other);
        }

        public sealed override string ToString()
        {
            var addr = Handle == IntPtr.Zero ? "null" : $"0x{Handle.ToInt64():X}";
            var desc = string.IsNullOrEmpty(Description) ? "" : $" \"{Description}\"";
            var size = ByteSize > 0 ? $" size={ByteSize}" : "";
            var own = Ownership == NativeHandleOwnership.Owned ? " owned" : Ownership == NativeHandleOwnership.Borrowed ? "" : " loaned";
            var disp = IsDisposed ? " disposed" : "";
            return $"<native:{Kind} {addr}{size}{desc}{own}{disp}>";
        }

        public static int AliveCount => _alive.Count;
    }
}

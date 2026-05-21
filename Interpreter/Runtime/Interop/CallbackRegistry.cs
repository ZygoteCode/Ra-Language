using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    /// <summary>
    /// Outbound callback support: convert a Ra function into a function pointer
    /// callable from native code. Each delegate is rooted in a dictionary keyed by
    /// the produced handle id so the GC cannot reclaim it while the native side
    /// still holds the pointer. Release via release_callback().
    /// </summary>
    public static class CallbackRegistry
    {
        private static long _idSeq;
        private static readonly ConcurrentDictionary<long, RegisteredCallback> _alive = new();

        private sealed class RegisteredCallback
        {
            public BaseFunctionValue Function;
            public Delegate Delegate;
            public IntPtr Pointer;
            public string Signature;
            public Context Context;

            public RegisteredCallback(BaseFunctionValue fn, Delegate d, IntPtr p, string sig, Context ctx)
            {
                Function = fn; Delegate = d; Pointer = p; Signature = sig; Context = ctx;
            }
        }

        /// <summary>
        /// Variant that pre-binds a `user_data` IntPtr/handle and prepends it to the
        /// arguments passed to the Ra function on each native invocation. Mirrors the
        /// common C idiom `cb(void* user, ...)`.
        /// </summary>
        public static (NativeHandleValue handle, string? error) CreateWithContext(BaseFunctionValue fn, string signature, RuntimeValue userData, Context context)
        {
            IntPtr captured = userData switch
            {
                NativeHandleValue nh => nh.Handle,
                NullValue => IntPtr.Zero,
                IntegerValue iv => new IntPtr(iv.Value),
                LongValue lv => new IntPtr(lv.Value),
                _ => IntPtr.Zero
            };

            Delegate? d = signature switch
            {
                "void(ptr,ptr)"     => (Delegate)new VoidPtrPtr((u, p) => InvokeRa(fn, context, new RuntimeValue[] { Wrap(u), Wrap(p) })),
                "int(ptr,ptr)"      => (Delegate)new IntPtrPtr((u, p) => UnwrapInt(InvokeRa(fn, context, new RuntimeValue[] { Wrap(u), Wrap(p) }))),
                "int(ptr,ptr,ptr)"  => (Delegate)new IntPtrPtrPtr((u, a, b) => UnwrapInt(InvokeRa(fn, context, new RuntimeValue[] { Wrap(u), Wrap(a), Wrap(b) }))),
                _ => null
            };
            if (d == null)
            {
                return (null!, $"as_callback_with: unsupported signature '{signature}'. Supported: void(ptr,ptr), int(ptr,ptr), int(ptr,ptr,ptr)");
            }

            var ptr = Marshal.GetFunctionPointerForDelegate(d);
            long id = System.Threading.Interlocked.Increment(ref _idSeq);
            _alive[id] = new RegisteredCallback(fn, d, ptr, signature, context);
            var handle = new NativeHandleValue(ptr, NativeHandleKind.Symbol, 0, "callback_ctx#" + id + ":" + signature, false);
            return (handle, null);
        }

        public static (NativeHandleValue handle, string? error) Create(BaseFunctionValue fn, string signature, Context context)
        {
            Delegate? d = signature switch
            {
                "void()"           => (Delegate)new VoidNoArg(() => InvokeRa(fn, context, Array.Empty<RuntimeValue>())),
                "void(ptr)"        => (Delegate)new VoidPtr(p => InvokeRa(fn, context, new RuntimeValue[] { Wrap(p) })),
                "int()"            => (Delegate)new IntNoArg(() => UnwrapInt(InvokeRa(fn, context, Array.Empty<RuntimeValue>()))),
                "int(int)"         => (Delegate)new IntInt(x => UnwrapInt(InvokeRa(fn, context, new RuntimeValue[] { new IntegerValue(x) }))),
                "int(ptr)"         => (Delegate)new IntPtrArg(p => UnwrapInt(InvokeRa(fn, context, new RuntimeValue[] { Wrap(p) }))),
                "int(int,int)"     => (Delegate)new IntIntInt((a, b) => UnwrapInt(InvokeRa(fn, context, new RuntimeValue[] { new IntegerValue(a), new IntegerValue(b) }))),
                "int(ptr,ptr)"     => (Delegate)new IntPtrPtr((a, b) => UnwrapInt(InvokeRa(fn, context, new RuntimeValue[] { Wrap(a), Wrap(b) }))),
                "long(long,long)"  => (Delegate)new LongLongLong((a, b) => UnwrapLong(InvokeRa(fn, context, new RuntimeValue[] { new LongValue(a), new LongValue(b) }))),
                "ptr(ptr)"         => (Delegate)new PtrPtr(p => UnwrapPtr(InvokeRa(fn, context, new RuntimeValue[] { Wrap(p) }))),
                "ptr(ptr,ptr)"     => (Delegate)new PtrPtrPtr((a, b) => UnwrapPtr(InvokeRa(fn, context, new RuntimeValue[] { Wrap(a), Wrap(b) }))),
                "bool(ptr,ptr)"    => (Delegate)new BoolPtrPtr((a, b) => UnwrapBool(InvokeRa(fn, context, new RuntimeValue[] { Wrap(a), Wrap(b) }))),
                "double(double)"   => (Delegate)new DoubleDouble(x => UnwrapDouble(InvokeRa(fn, context, new RuntimeValue[] { new DoubleValue(x) }))),
                "double(double,double)" => (Delegate)new DoubleDoubleDouble((a, b) => UnwrapDouble(InvokeRa(fn, context, new RuntimeValue[] { new DoubleValue(a), new DoubleValue(b) }))),
                _ => null
            };

            if (d == null)
            {
                return (null!, $"as_callback: unsupported signature '{signature}'. Supported: void()/void(ptr), int()/int(int)/int(ptr)/int(int,int)/int(ptr,ptr), long(long,long), ptr(ptr)/ptr(ptr,ptr), bool(ptr,ptr), double(double)/double(double,double)");
            }

            var ptr = Marshal.GetFunctionPointerForDelegate(d);
            long id = System.Threading.Interlocked.Increment(ref _idSeq);
            _alive[id] = new RegisteredCallback(fn, d, ptr, signature, context);

            var handle = new NativeHandleValue(ptr, NativeHandleKind.Symbol, 0, "callback#" + id + ":" + signature, false);
            return (handle, null);
        }

        public static bool Release(IntPtr pointer)
        {
            foreach (var kv in _alive)
            {
                if (kv.Value.Pointer == pointer)
                {
                    return _alive.TryRemove(kv.Key, out _);
                }
            }
            return false;
        }

        public static int Count => _alive.Count;

        private static RuntimeValue Wrap(IntPtr p) =>
            p == IntPtr.Zero ? (RuntimeValue)NullValue.Null : new NativeHandleValue(p, NativeHandleKind.Pointer);

        private static RuntimeValue InvokeRa(BaseFunctionValue fn, Context ctx, RuntimeValue[] args)
        {
            try
            {
                var list = new List<RuntimeValue>(args.Length);
                for (int i = 0; i < args.Length; i++) list.Add(args[i]);
                var res = SyncAwait.Get(fn.Execute(list));
                if (res.Error != null) return NullValue.Null;
                return res.Value ?? NullValue.Null;
            }
            catch
            {
                return NullValue.Null;
            }
        }

        private static int UnwrapInt(RuntimeValue v)
        {
            if (v is IntegerValue iv) return iv.Value;
            if (v is LongValue lv) return (int)lv.Value;
            if (v is BooleanValue bv) return bv.Value ? 1 : 0;
            if (v is NullValue) return 0;
            try { return (int)Convert.ToInt64(v.ToString()); } catch { return 0; }
        }

        private static long UnwrapLong(RuntimeValue v)
        {
            if (v is LongValue lv) return lv.Value;
            if (v is IntegerValue iv) return iv.Value;
            if (v is NullValue) return 0;
            try { return Convert.ToInt64(v.ToString()); } catch { return 0; }
        }

        private static IntPtr UnwrapPtr(RuntimeValue v)
        {
            if (v is NativeHandleValue nh) return nh.Handle;
            if (v is NullValue) return IntPtr.Zero;
            return new IntPtr(UnwrapLong(v));
        }

        private static bool UnwrapBool(RuntimeValue v)
        {
            if (v is BooleanValue bv) return bv.Value;
            if (v is NullValue) return false;
            return v.IsTrue();
        }

        private static double UnwrapDouble(RuntimeValue v)
        {
            if (v is DoubleValue dv) return dv.Value;
            if (v is FloatValue fv) return fv.Value;
            if (v is IntegerValue iv) return iv.Value;
            if (v is LongValue lv) return lv.Value;
            if (v is NullValue) return 0;
            try { return Convert.ToDouble(v.ToString()); } catch { return 0; }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidNoArg();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidPtr(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidPtrPtr(IntPtr u, IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntPtrPtrPtr(IntPtr u, IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntNoArg();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntInt(int a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntPtrArg(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntIntInt(int a, int b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntPtrPtr(IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate long LongLongLong(long a, long b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr PtrPtr(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr PtrPtrPtr(IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate bool BoolPtrPtr(IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate double DoubleDouble(double a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate double DoubleDoubleDouble(double a, double b);
    }
}

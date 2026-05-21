using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    public sealed class NativeFunctionValue : BaseFunctionValue
    {
        public NativeBinding Binding { get; }
        public IReadOnlyList<string> ParameterNames { get; }

        public override RuntimeValueType Type => RuntimeValueType.Function;
        public override bool IsCopy => false;

        public NativeFunctionValue(string name, IReadOnlyList<string> parameterNames, NativeBinding binding)
            : base(name)
        {
            ParameterNames = parameterNames;
            Binding = binding;
        }

        public sealed override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();

            if (args.Count != Binding.Parameters.Count)
            {
                return res.Failure(new RuntimeError(
                    PositionStart, PositionEnd,
                    $"native fn '{Name}' expects {Binding.Parameters.Count} argument(s), got {args.Count}",
                    Context));
            }

            var bindError = EnsureBound();
            if (bindError != null)
            {
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, bindError, Context));
            }

            var paramKinds = new List<NativeTypeKind>(Binding.Parameters.Count);
            var marshalled = new List<NativeMarshaller.MarshalledArg>(Binding.Parameters.Count);
            var owned = new List<IntPtr>();
            var refSlots = new List<(int paramIndex, ReferenceValue refVal, IntPtr innerBuffer, IntPtr canaryBuffer, NativeTypeKind underlyingKind)>();

            bool canary = Binding.AbiCanary;

            try
            {
                for (int i = 0; i < Binding.Parameters.Count; i++)
                {
                    var kind = Binding.Parameters[i].Kind;
                    var spec = Binding.Parameters[i];
                    var argValue = args[i];

                    if (spec.IsRef && argValue is ReferenceValue rv)
                    {
                        int width = NativeWidthOf(kind);
                        IntPtr innerBuf;
                        IntPtr canaryOuter = IntPtr.Zero;
                        if (canary)
                        {
                            canaryOuter = AbiCanary.Wrap(width, out innerBuf);
                        }
                        else
                        {
                            innerBuf = Marshal.AllocHGlobal(width);
                        }
                        WritePrimitive(innerBuf, kind, rv.Value);
                        refSlots.Add((i, rv, innerBuf, canaryOuter, kind));
                        paramKinds.Add(NativeTypeKind.IntPtr);
                        marshalled.Add(new NativeMarshaller.MarshalledArg(innerBuf.ToInt64(), 0, false, IntPtr.Zero, 0));
                    }
                    else
                    {
                        paramKinds.Add(kind);
                        marshalled.Add(NativeMarshaller.ToNative(kind, argValue, Binding.Charset, owned));
                    }
                }

                if (Binding.SetLastError)
                {
                    _ = Marshal.GetLastWin32Error();
                }

                long intResult = 0;
                double doubleResult = 0;
                Stopwatch? sw = Binding.Trace || FfiTracer.Enabled ? Stopwatch.StartNew() : null;

                try
                {
                    Func<(long, double)> invoke = () => NativeInvoker.Invoke(
                        Binding.FunctionPointer,
                        Binding.ReturnKind,
                        paramKinds,
                        marshalled,
                        Binding.CallingConvention);

                    if (Binding.StaThread && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        (intResult, doubleResult) = StaThreadDispatcher.Invoke(invoke);
                    }
                    else
                    {
                        (intResult, doubleResult) = invoke();
                    }
                }
                catch (StaThreadDispatcher.InvocationException stx)
                {
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                        $"native fn '{Name}' STA dispatch failed: {stx.InnerException?.Message ?? stx.Message}", Context));
                }
                catch (SEHException sehx)
                {
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                        $"native fn '{Name}' raised SEH exception 0x{sehx.ErrorCode:X}: {sehx.Message}", Context));
                }
                catch (AccessViolationException avx)
                {
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                        $"native fn '{Name}' access violation: {avx.Message}", Context));
                }
                catch (Exception ex)
                {
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                        $"native fn '{Name}' invocation failed: {ex.Message}", Context));
                }

                if (canary)
                {
                    for (int i = 0; i < refSlots.Count; i++)
                    {
                        var slot = refSlots[i];
                        if (slot.canaryBuffer == IntPtr.Zero) continue;
                        int width = NativeWidthOf(slot.underlyingKind);
                        if (!AbiCanary.Verify(slot.canaryBuffer, width, out var msg))
                        {
                            return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                                $"native fn '{Name}' ref-param[{slot.paramIndex}] {msg}", Context));
                        }
                    }
                }

                var result = MaterializeReturn(intResult, doubleResult);

                foreach (var slot in refSlots)
                {
                    var newValue = ReadPrimitive(slot.innerBuffer, slot.underlyingKind);
                    slot.refVal.Value = newValue;
                }

                int lastErrorCode = 0;
                if (Binding.SetLastError)
                {
                    lastErrorCode = Marshal.GetLastWin32Error();
                    if (Context?.SymbolTable != null)
                    {
                        Context.SymbolTable.Set("__last_native_error", new IntegerValue(lastErrorCode));
                    }
                }

                if (sw != null && (Binding.Trace || FfiTracer.Enabled))
                {
                    sw.Stop();
                    var argRepr = new List<string>(args.Count);
                    for (int i = 0; i < args.Count; i++) argRepr.Add(ReprShort(args[i]));
                    FfiTracer.Emit(Binding.Library, Binding.EntryPoint, argRepr, ReprShort(result), sw.Elapsed.Ticks / 10, lastErrorCode);
                }

                return res.Success(result.SetContext(Context).SetPos(PositionStart, PositionEnd));
            }
            finally
            {
                foreach (var slot in refSlots)
                {
                    if (slot.canaryBuffer != IntPtr.Zero) AbiCanary.Free(slot.canaryBuffer);
                    else if (slot.innerBuffer != IntPtr.Zero) Marshal.FreeHGlobal(slot.innerBuffer);
                }
                NativeMarshaller.FreeOwnedBuffers(owned, paramKinds, marshalled);
            }
        }

        private static string ReprShort(RuntimeValue v)
        {
            if (v == null) return "null";
            switch (v)
            {
                case StringValue sv:
                    var s = sv.Value;
                    if (s.Length > 60) s = s.Substring(0, 57) + "...";
                    return "\"" + s + "\"";
                case NativeHandleValue nh:
                    return nh.ToString();
                case NullValue: return "null";
                default: return v.ToString() ?? "";
            }
        }

        private RuntimeValue MaterializeReturn(long intResult, double doubleResult)
        {
            var k = Binding.ReturnKind;
            if (k == NativeTypeKind.StringUtf16 || k == NativeTypeKind.StringUtf8 || k == NativeTypeKind.StringAnsi)
            {
                IntPtr p = new IntPtr(intResult);
                string s = k switch
                {
                    NativeTypeKind.StringUtf16 => Marshal.PtrToStringUni(p) ?? "",
                    NativeTypeKind.StringUtf8 => Marshal.PtrToStringUTF8(p) ?? "",
                    _ => Marshal.PtrToStringAnsi(p) ?? ""
                };
                FreeReturnedString(p);
                return new StringValue(s);
            }
            return NativeMarshaller.FromNative(k, intResult, doubleResult, Binding.Charset);
        }

        private void FreeReturnedString(IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            switch (Binding.ReturnStringFree)
            {
                case StringFreePolicy.None: return;
                case StringFreePolicy.CoTaskMem: Marshal.FreeCoTaskMem(p); return;
                case StringFreePolicy.HGlobal: Marshal.FreeHGlobal(p); return;
                case StringFreePolicy.LocalFree:
                {
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
                    try
                    {
                        var k32 = NativeLibraryResolver.Load("kernel32", null, out _, out _);
                        if (NativeLibraryResolver.TryGetExport(k32, "LocalFree", true, out var lf))
                            Marshal.GetDelegateForFunctionPointer<NativeInvoker.Fn_P1>(lf)(p);
                    }
                    catch { }
                    return;
                }
                case StringFreePolicy.FreeLibcStyle:
                {
                    string libcName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "msvcrt" : "libc";
                    try
                    {
                        var libc = NativeLibraryResolver.Load(libcName, null, out _, out _);
                        if (NativeLibraryResolver.TryGetExport(libc, "free", true, out var f))
                            Marshal.GetDelegateForFunctionPointer<NativeInvoker.Fn_V1>(f)(p);
                    }
                    catch { }
                    return;
                }
                case StringFreePolicy.CustomSymbol:
                {
                    if (string.IsNullOrEmpty(Binding.CustomFreeSymbol)) return;
                    try
                    {
                        if (NativeLibraryResolver.TryGetExport(Binding.LibraryHandle, Binding.CustomFreeSymbol!, true, out var f))
                            Marshal.GetDelegateForFunctionPointer<NativeInvoker.Fn_V1>(f)(p);
                    }
                    catch { }
                    return;
                }
            }
        }

        private static int NativeWidthOf(NativeTypeKind kind) => kind switch
        {
            NativeTypeKind.Bool => 4,
            NativeTypeKind.Int8 or NativeTypeKind.UInt8 => 1,
            NativeTypeKind.Int16 or NativeTypeKind.UInt16 => 2,
            NativeTypeKind.Int32 or NativeTypeKind.UInt32 or NativeTypeKind.Float => 4,
            NativeTypeKind.Int64 or NativeTypeKind.UInt64 or NativeTypeKind.Double => 8,
            NativeTypeKind.IntPtr or NativeTypeKind.Handle or NativeTypeKind.Pointer
                or NativeTypeKind.StringUtf16 or NativeTypeKind.StringUtf8 or NativeTypeKind.StringAnsi
                or NativeTypeKind.Buffer => IntPtr.Size,
            _ => 8
        };

        private static void WritePrimitive(IntPtr buf, NativeTypeKind kind, RuntimeValue v)
        {
            long l = v switch
            {
                IntegerValue iv => iv.Value,
                LongValue lv => lv.Value,
                BooleanValue bv => bv.Value ? 1 : 0,
                NullValue => 0,
                _ => Convert.ToInt64(v?.ToString() ?? "0")
            };
            unsafe
            {
                byte* p = (byte*)buf;
                switch (kind)
                {
                    case NativeTypeKind.Bool:
                    case NativeTypeKind.Int32:
                    case NativeTypeKind.UInt32:
                        *(int*)p = (int)l;
                        break;
                    case NativeTypeKind.Int8:
                    case NativeTypeKind.UInt8:
                        *p = (byte)(l & 0xff);
                        break;
                    case NativeTypeKind.Int16:
                    case NativeTypeKind.UInt16:
                        *(short*)p = (short)l;
                        break;
                    case NativeTypeKind.Int64:
                    case NativeTypeKind.UInt64:
                        *(long*)p = l;
                        break;
                    case NativeTypeKind.Float:
                        *(float*)p = (v is FloatValue fv ? fv.Value : v is DoubleValue dv ? (float)dv.Value : (float)l);
                        break;
                    case NativeTypeKind.Double:
                        *(double*)p = (v is DoubleValue dv2 ? dv2.Value : v is FloatValue fv2 ? fv2.Value : l);
                        break;
                    default:
                        *(IntPtr*)p = v is NativeHandleValue nh ? nh.Handle : new IntPtr(l);
                        break;
                }
            }
        }

        private static RuntimeValue ReadPrimitive(IntPtr buf, NativeTypeKind kind)
        {
            unsafe
            {
                byte* p = (byte*)buf;
                return kind switch
                {
                    NativeTypeKind.Bool => (RuntimeValue)BooleanValue.Of(*(int*)p != 0),
                    NativeTypeKind.Int8 => new IntegerValue(*(sbyte*)p),
                    NativeTypeKind.UInt8 => new IntegerValue(*p),
                    NativeTypeKind.Int16 => new IntegerValue(*(short*)p),
                    NativeTypeKind.UInt16 => new IntegerValue(*(ushort*)p),
                    NativeTypeKind.Int32 => new IntegerValue(*(int*)p),
                    NativeTypeKind.UInt32 => new UnsignedIntegerValue(*(uint*)p),
                    NativeTypeKind.Int64 => new LongValue(*(long*)p),
                    NativeTypeKind.UInt64 => new UnsignedLongValue(*(ulong*)p),
                    NativeTypeKind.Float => new FloatValue(*(float*)p),
                    NativeTypeKind.Double => new DoubleValue(*(double*)p),
                    _ => new NativeHandleValue(*(IntPtr*)p, NativeHandleKind.Pointer)
                };
            }
        }

        public string? EnsureBound()
        {
            if (Binding.IsResolved && Binding.FunctionPointer != IntPtr.Zero) return null;

            lock (Binding.BindingLock)
            {
                if (Binding.IsResolved && Binding.FunctionPointer != IntPtr.Zero) return null;

                var handle = NativeLibraryResolver.Load(Binding.Library, Binding.SearchPaths, out var resolved, out var loadError);
                if (handle == IntPtr.Zero)
                {
                    Binding.LastResolutionError = loadError;
                    return loadError ?? $"Cannot load library '{Binding.Library}'";
                }

                Binding.LibraryHandle = handle;
                Binding.ResolvedLibraryName = resolved;

                if (!NativeLibraryResolver.TryGetExport(handle, Binding.EntryPoint, Binding.ExactSpelling, out var fnPtr))
                {
                    Binding.LastResolutionError = $"Entry point '{Binding.EntryPoint}' not found in '{Binding.Library}'";
                    return Binding.LastResolutionError;
                }

                Binding.FunctionPointer = fnPtr;
                Binding.IsResolved = true;
                return null;
            }
        }

        public sealed override RuntimeValue Copy()
            => new NativeFunctionValue(Name, ParameterNames, Binding)
                .SetContext(Context).SetPos(PositionStart, PositionEnd);

        public sealed override string ToString()
        {
            var sig = $"{Name}({string.Join(", ", ParameterNames)})";
            return $"<native fn {sig} -> {Binding.Library}!{Binding.EntryPoint}>";
        }
    }
}

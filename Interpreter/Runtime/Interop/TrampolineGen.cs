using System.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    /// <summary>
    /// Builds and caches per-signature trampolines via DynamicMethod + calli IL instruction.
    ///
    /// Supports arbitrary mixed int/float signatures, struct-by-pointer parameters,
    /// arbitrarily many parameters (no fixed 12-int / 4-double limit), and ABI-correct
    /// register selection (the JIT honours the parameter types when emitting the calli).
    ///
    /// Each signature shape is hashed and a trampoline is JIT-compiled exactly once.
    /// Subsequent calls go through the cached delegate dispatch path.
    ///
    /// AOT note: DynamicMethod is not AOT-compatible. Under PublishAot=true the trampoline
    /// generator is not used (the call site falls back to the pre-declared delegate matrix).
    /// </summary>
    public static class TrampolineGen
    {
        private static readonly ConcurrentDictionary<string, TrampolineDelegate> _cache =
            new();
        private static volatile bool _disabled;

        public delegate (long intRet, double doubleRet) TrampolineDelegate(IntPtr fnPtr, long[] intArgs, double[] doubleArgs);

        public static bool IsEnabled => !_disabled && !RuntimeFeature.IsAotPublished;
        public static void Disable() => _disabled = true;

        public static TrampolineDelegate? GetOrCreate(NativeTypeKind returnKind, IReadOnlyList<NativeTypeKind> paramKinds, NativeCallingConvention callConv)
        {
            if (!IsEnabled) return null;
            var key = BuildKey(returnKind, paramKinds, callConv);
            if (_cache.TryGetValue(key, out var d)) return d;
            try
            {
                var created = Build(returnKind, paramKinds, callConv);
                _cache.TryAdd(key, created);
                return created;
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                Disable();
                return null;
            }
        }

        public static string? LastError { get; private set; }

        private static string BuildKey(NativeTypeKind ret, IReadOnlyList<NativeTypeKind> p, NativeCallingConvention cc)
        {
            var sb = new System.Text.StringBuilder(64);
            sb.Append((int)cc).Append('|').Append((int)ret).Append('(');
            for (int i = 0; i < p.Count; i++) { if (i > 0) sb.Append(','); sb.Append((int)p[i]); }
            sb.Append(')');
            return sb.ToString();
        }

        private static TrampolineDelegate Build(NativeTypeKind returnKind, IReadOnlyList<NativeTypeKind> paramKinds, NativeCallingConvention callConv)
        {
            var nativeReturnType = NativeClrType(returnKind);
            var nativeParamTypes = new Type[paramKinds.Count];
            for (int i = 0; i < paramKinds.Count; i++) nativeParamTypes[i] = NativeClrType(paramKinds[i]);

            var dm = new DynamicMethod(
                "ra_trampoline",
                typeof(ValueTuple<long, double>),
                new[] { typeof(IntPtr), typeof(long[]), typeof(double[]) },
                typeof(TrampolineGen).Module,
                skipVisibility: true);

            var il = dm.GetILGenerator();

            int intSlot = 0, floatSlot = 0;
            for (int i = 0; i < paramKinds.Count; i++)
            {
                var k = paramKinds[i];
                if (IsFloatKind(k))
                {
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldc_I4, floatSlot++);
                    il.Emit(OpCodes.Ldelem_R8);
                    if (k == NativeTypeKind.Float) il.Emit(OpCodes.Conv_R4);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4, intSlot++);
                    il.Emit(OpCodes.Ldelem_I8);
                    EmitNarrow(il, k);
                }
            }

            il.Emit(OpCodes.Ldarg_0);

            var cc = MapCallConvUnmanaged(callConv);
            il.EmitCalli(OpCodes.Calli, cc, nativeReturnType, nativeParamTypes);

            if (returnKind == NativeTypeKind.Void)
            {
                il.Emit(OpCodes.Ldc_I8, 0L);
                il.Emit(OpCodes.Ldc_R8, 0.0);
            }
            else if (IsFloatKind(returnKind))
            {
                il.DeclareLocal(typeof(double));
                if (returnKind == NativeTypeKind.Float) il.Emit(OpCodes.Conv_R8);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldc_I8, 0L);
                il.Emit(OpCodes.Ldloc_0);
            }
            else
            {
                EmitWidenToI64(il, returnKind);
                il.Emit(OpCodes.Ldc_R8, 0.0);
            }

            var ctor = typeof(ValueTuple<long, double>).GetConstructor(new[] { typeof(long), typeof(double) });
            il.Emit(OpCodes.Newobj, ctor!);
            il.Emit(OpCodes.Ret);

            return (TrampolineDelegate)dm.CreateDelegate(typeof(TrampolineDelegate));
        }

        private static void EmitNarrow(ILGenerator il, NativeTypeKind k)
        {
            switch (k)
            {
                case NativeTypeKind.Int8: il.Emit(OpCodes.Conv_I1); break;
                case NativeTypeKind.UInt8: il.Emit(OpCodes.Conv_U1); break;
                case NativeTypeKind.Int16: il.Emit(OpCodes.Conv_I2); break;
                case NativeTypeKind.UInt16: il.Emit(OpCodes.Conv_U2); break;
                case NativeTypeKind.Int32: il.Emit(OpCodes.Conv_I4); break;
                case NativeTypeKind.UInt32: il.Emit(OpCodes.Conv_U4); break;
                case NativeTypeKind.Bool: il.Emit(OpCodes.Conv_I4); break;
                case NativeTypeKind.Int64:
                case NativeTypeKind.UInt64:
                    break;
                case NativeTypeKind.IntPtr:
                case NativeTypeKind.Handle:
                case NativeTypeKind.Pointer:
                case NativeTypeKind.StringUtf16:
                case NativeTypeKind.StringUtf8:
                case NativeTypeKind.StringAnsi:
                case NativeTypeKind.Buffer:
                    il.Emit(OpCodes.Conv_I);
                    break;
                default: break;
            }
        }

        private static void EmitWidenToI64(ILGenerator il, NativeTypeKind k)
        {
            switch (k)
            {
                case NativeTypeKind.Int8: il.Emit(OpCodes.Conv_I8); break;
                case NativeTypeKind.UInt8: il.Emit(OpCodes.Conv_U8); break;
                case NativeTypeKind.Int16: il.Emit(OpCodes.Conv_I8); break;
                case NativeTypeKind.UInt16: il.Emit(OpCodes.Conv_U8); break;
                case NativeTypeKind.Int32: il.Emit(OpCodes.Conv_I8); break;
                case NativeTypeKind.UInt32: il.Emit(OpCodes.Conv_U8); break;
                case NativeTypeKind.Bool: il.Emit(OpCodes.Conv_I8); break;
                case NativeTypeKind.Int64: break;
                case NativeTypeKind.UInt64: break;
                case NativeTypeKind.IntPtr:
                case NativeTypeKind.Handle:
                case NativeTypeKind.Pointer:
                case NativeTypeKind.StringUtf16:
                case NativeTypeKind.StringUtf8:
                case NativeTypeKind.StringAnsi:
                case NativeTypeKind.Buffer:
                    il.Emit(OpCodes.Conv_I8);
                    break;
                default: il.Emit(OpCodes.Conv_I8); break;
            }
        }

        private static Type NativeClrType(NativeTypeKind k)
        {
            switch (k)
            {
                case NativeTypeKind.Void: return typeof(void);
                case NativeTypeKind.Bool: return typeof(int);
                case NativeTypeKind.Int8: return typeof(sbyte);
                case NativeTypeKind.UInt8: return typeof(byte);
                case NativeTypeKind.Int16: return typeof(short);
                case NativeTypeKind.UInt16: return typeof(ushort);
                case NativeTypeKind.Int32: return typeof(int);
                case NativeTypeKind.UInt32: return typeof(uint);
                case NativeTypeKind.Int64: return typeof(long);
                case NativeTypeKind.UInt64: return typeof(ulong);
                case NativeTypeKind.Float: return typeof(float);
                case NativeTypeKind.Double: return typeof(double);
                case NativeTypeKind.IntPtr:
                case NativeTypeKind.Handle:
                case NativeTypeKind.Pointer:
                case NativeTypeKind.StringUtf16:
                case NativeTypeKind.StringUtf8:
                case NativeTypeKind.StringAnsi:
                case NativeTypeKind.Buffer:
                    return typeof(IntPtr);
                default: return typeof(IntPtr);
            }
        }

        private static bool IsFloatKind(NativeTypeKind k) => k == NativeTypeKind.Float || k == NativeTypeKind.Double;

        private static CallingConvention MapCallConvUnmanaged(NativeCallingConvention c)
        {
            switch (c)
            {
                case NativeCallingConvention.Cdecl: return CallingConvention.Cdecl;
                case NativeCallingConvention.StdCall: return CallingConvention.StdCall;
                case NativeCallingConvention.FastCall: return CallingConvention.FastCall;
                case NativeCallingConvention.ThisCall: return CallingConvention.ThisCall;
                case NativeCallingConvention.WinApi: return CallingConvention.Winapi;
                default: return CallingConvention.Winapi;
            }
        }

        public static void ClearCache() => _cache.Clear();
    }

    internal static class RuntimeFeature
    {
        public static readonly bool IsAotPublished = DetectAot();
        public static string? DetectError;

        private static bool DetectAot()
        {
            try
            {
                var dm = new DynamicMethod("ra_probe", typeof(int), Type.EmptyTypes, typeof(RuntimeFeature).Module, skipVisibility: true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
                var d = (Func<int>)dm.CreateDelegate(typeof(Func<int>));
                return d() != 1;
            }
            catch (Exception ex)
            {
                DetectError = ex.ToString();
                return true;
            }
        }
    }
}

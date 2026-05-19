using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    public static class NativeInvoker
    {
        public delegate IntPtr Fn_P0();
        public delegate IntPtr Fn_P1(IntPtr a);
        public delegate IntPtr Fn_P2(IntPtr a, IntPtr b);
        public delegate IntPtr Fn_P3(IntPtr a, IntPtr b, IntPtr c);
        public delegate IntPtr Fn_P4(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        public delegate IntPtr Fn_P5(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
        public delegate IntPtr Fn_P6(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f);
        public delegate IntPtr Fn_P7(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g);
        public delegate IntPtr Fn_P8(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h);
        public delegate IntPtr Fn_P9(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h, IntPtr i);
        public delegate IntPtr Fn_P10(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h, IntPtr i, IntPtr j);
        public delegate IntPtr Fn_P11(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h, IntPtr i, IntPtr j, IntPtr k);
        public delegate IntPtr Fn_P12(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h, IntPtr i, IntPtr j, IntPtr k, IntPtr l);

        public delegate void Fn_V0();
        public delegate void Fn_V1(IntPtr a);
        public delegate void Fn_V2(IntPtr a, IntPtr b);
        public delegate void Fn_V3(IntPtr a, IntPtr b, IntPtr c);
        public delegate void Fn_V4(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        public delegate void Fn_V5(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
        public delegate void Fn_V6(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f);
        public delegate void Fn_V7(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g);
        public delegate void Fn_V8(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h);

        public delegate int Fn_I32_0();
        public delegate int Fn_I32_1(IntPtr a);
        public delegate int Fn_I32_2(IntPtr a, IntPtr b);
        public delegate int Fn_I32_3(IntPtr a, IntPtr b, IntPtr c);
        public delegate int Fn_I32_4(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        public delegate int Fn_I32_5(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
        public delegate int Fn_I32_6(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f);
        public delegate int Fn_I32_7(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g);
        public delegate int Fn_I32_8(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h);

        public delegate long Fn_I64_0();
        public delegate long Fn_I64_1(IntPtr a);
        public delegate long Fn_I64_2(IntPtr a, IntPtr b);
        public delegate long Fn_I64_3(IntPtr a, IntPtr b, IntPtr c);
        public delegate long Fn_I64_4(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        public delegate long Fn_I64_5(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
        public delegate long Fn_I64_6(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f);

        public delegate double Fn_D0_0();
        public delegate double Fn_D1(double a);
        public delegate double Fn_D2(double a, double b);
        public delegate double Fn_D3(double a, double b, double c);
        public delegate double Fn_D4(double a, double b, double c, double d);

        public static (long intResult, double doubleResult) Invoke(
            IntPtr fnPtr,
            NativeTypeKind returnKind,
            IReadOnlyList<NativeTypeKind> paramKinds,
            IReadOnlyList<NativeMarshaller.MarshalledArg> args,
            NativeCallingConvention callConv = NativeCallingConvention.WinApi)
        {
            if (args.Count != paramKinds.Count)
                throw new InvalidOperationException($"NativeInvoker: arg count mismatch (got {args.Count}, expected {paramKinds.Count})");

            var trampoline = TrampolineGen.GetOrCreate(returnKind, paramKinds, callConv);
            if (trampoline != null)
            {
                int intCount = 0, floatCount = 0;
                for (int i = 0; i < paramKinds.Count; i++)
                {
                    if (paramKinds[i] == NativeTypeKind.Float || paramKinds[i] == NativeTypeKind.Double) floatCount++;
                    else intCount++;
                }
                var intArr = new long[intCount];
                var floatArr = new double[floatCount];
                int iIdx = 0, fIdx = 0;
                for (int i = 0; i < paramKinds.Count; i++)
                {
                    if (paramKinds[i] == NativeTypeKind.Float || paramKinds[i] == NativeTypeKind.Double)
                        floatArr[fIdx++] = args[i].FloatValue;
                    else
                        intArr[iIdx++] = args[i].IntegralValue;
                }
                return trampoline(fnPtr, intArr, floatArr);
            }

            if (MixedDispatcher.TryInvoke(fnPtr, returnKind, paramKinds, args, out var mIntRes, out var mDoubleRes))
            {
                return (mIntRes, mDoubleRes);
            }

            bool allFloat = AllFloat(paramKinds);
            bool returnIsFloat = returnKind == NativeTypeKind.Float || returnKind == NativeTypeKind.Double;

            if (allFloat && returnIsFloat && args.Count <= 4)
            {
                return (0, InvokeAllDouble(fnPtr, args));
            }

            if (HasFloatParams(paramKinds))
                throw new InvalidOperationException("Mixed float/integer parameter ABI not supported for N>4 in AOT mode (trampoline generator disabled). Pre-declared matrix covers up to 4 params with arbitrary int/float layout.");

            switch (returnKind)
            {
                case NativeTypeKind.Void:
                    InvokeVoid(fnPtr, args);
                    return (0, 0);
                case NativeTypeKind.Int32:
                    return (InvokeInt32(fnPtr, args), 0);
                case NativeTypeKind.Int64:
                    return (InvokeInt64(fnPtr, args), 0);
                case NativeTypeKind.Float:
                case NativeTypeKind.Double:
                    throw new InvalidOperationException("Float return with non-float params requires the trampoline generator (AOT fallback cannot handle this).");
                default:
                    return (InvokePtr(fnPtr, args).ToInt64(), 0);
            }
        }

        private static bool AllFloat(IReadOnlyList<NativeTypeKind> p)
        {
            if (p.Count == 0) return false;
            for (int i = 0; i < p.Count; i++)
                if (p[i] != NativeTypeKind.Float && p[i] != NativeTypeKind.Double) return false;
            return true;
        }

        private static bool HasFloatParams(IReadOnlyList<NativeTypeKind> p)
        {
            for (int i = 0; i < p.Count; i++)
                if (p[i] == NativeTypeKind.Float || p[i] == NativeTypeKind.Double) return true;
            return false;
        }

        private static IntPtr InvokePtr(IntPtr fn, IReadOnlyList<NativeMarshaller.MarshalledArg> args)
        {
            switch (args.Count)
            {
                case 0: return Marshal.GetDelegateForFunctionPointer<Fn_P0>(fn)();
                case 1: return Marshal.GetDelegateForFunctionPointer<Fn_P1>(fn)(P(args, 0));
                case 2: return Marshal.GetDelegateForFunctionPointer<Fn_P2>(fn)(P(args, 0), P(args, 1));
                case 3: return Marshal.GetDelegateForFunctionPointer<Fn_P3>(fn)(P(args, 0), P(args, 1), P(args, 2));
                case 4: return Marshal.GetDelegateForFunctionPointer<Fn_P4>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3));
                case 5: return Marshal.GetDelegateForFunctionPointer<Fn_P5>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4));
                case 6: return Marshal.GetDelegateForFunctionPointer<Fn_P6>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5));
                case 7: return Marshal.GetDelegateForFunctionPointer<Fn_P7>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6));
                case 8: return Marshal.GetDelegateForFunctionPointer<Fn_P8>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6), P(args, 7));
                case 9: return Marshal.GetDelegateForFunctionPointer<Fn_P9>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6), P(args, 7), P(args, 8));
                case 10: return Marshal.GetDelegateForFunctionPointer<Fn_P10>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6), P(args, 7), P(args, 8), P(args, 9));
                case 11: return Marshal.GetDelegateForFunctionPointer<Fn_P11>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6), P(args, 7), P(args, 8), P(args, 9), P(args, 10));
                case 12: return Marshal.GetDelegateForFunctionPointer<Fn_P12>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6), P(args, 7), P(args, 8), P(args, 9), P(args, 10), P(args, 11));
                default: throw new InvalidOperationException($"NativeInvoker supports up to 12 integral parameters (got {args.Count}).");
            }
        }

        private static void InvokeVoid(IntPtr fn, IReadOnlyList<NativeMarshaller.MarshalledArg> args)
        {
            switch (args.Count)
            {
                case 0: Marshal.GetDelegateForFunctionPointer<Fn_V0>(fn)(); break;
                case 1: Marshal.GetDelegateForFunctionPointer<Fn_V1>(fn)(P(args, 0)); break;
                case 2: Marshal.GetDelegateForFunctionPointer<Fn_V2>(fn)(P(args, 0), P(args, 1)); break;
                case 3: Marshal.GetDelegateForFunctionPointer<Fn_V3>(fn)(P(args, 0), P(args, 1), P(args, 2)); break;
                case 4: Marshal.GetDelegateForFunctionPointer<Fn_V4>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3)); break;
                case 5: Marshal.GetDelegateForFunctionPointer<Fn_V5>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4)); break;
                case 6: Marshal.GetDelegateForFunctionPointer<Fn_V6>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5)); break;
                case 7: Marshal.GetDelegateForFunctionPointer<Fn_V7>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6)); break;
                case 8: Marshal.GetDelegateForFunctionPointer<Fn_V8>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6), P(args, 7)); break;
                default: throw new InvalidOperationException($"NativeInvoker supports up to 8 void parameters (got {args.Count}).");
            }
        }

        private static int InvokeInt32(IntPtr fn, IReadOnlyList<NativeMarshaller.MarshalledArg> args)
        {
            switch (args.Count)
            {
                case 0: return Marshal.GetDelegateForFunctionPointer<Fn_I32_0>(fn)();
                case 1: return Marshal.GetDelegateForFunctionPointer<Fn_I32_1>(fn)(P(args, 0));
                case 2: return Marshal.GetDelegateForFunctionPointer<Fn_I32_2>(fn)(P(args, 0), P(args, 1));
                case 3: return Marshal.GetDelegateForFunctionPointer<Fn_I32_3>(fn)(P(args, 0), P(args, 1), P(args, 2));
                case 4: return Marshal.GetDelegateForFunctionPointer<Fn_I32_4>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3));
                case 5: return Marshal.GetDelegateForFunctionPointer<Fn_I32_5>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4));
                case 6: return Marshal.GetDelegateForFunctionPointer<Fn_I32_6>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5));
                case 7: return Marshal.GetDelegateForFunctionPointer<Fn_I32_7>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6));
                case 8: return Marshal.GetDelegateForFunctionPointer<Fn_I32_8>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5), P(args, 6), P(args, 7));
                default: throw new InvalidOperationException($"NativeInvoker supports up to 8 int32-return parameters (got {args.Count}). Use IntPtr return type for more.");
            }
        }

        private static long InvokeInt64(IntPtr fn, IReadOnlyList<NativeMarshaller.MarshalledArg> args)
        {
            switch (args.Count)
            {
                case 0: return Marshal.GetDelegateForFunctionPointer<Fn_I64_0>(fn)();
                case 1: return Marshal.GetDelegateForFunctionPointer<Fn_I64_1>(fn)(P(args, 0));
                case 2: return Marshal.GetDelegateForFunctionPointer<Fn_I64_2>(fn)(P(args, 0), P(args, 1));
                case 3: return Marshal.GetDelegateForFunctionPointer<Fn_I64_3>(fn)(P(args, 0), P(args, 1), P(args, 2));
                case 4: return Marshal.GetDelegateForFunctionPointer<Fn_I64_4>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3));
                case 5: return Marshal.GetDelegateForFunctionPointer<Fn_I64_5>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4));
                case 6: return Marshal.GetDelegateForFunctionPointer<Fn_I64_6>(fn)(P(args, 0), P(args, 1), P(args, 2), P(args, 3), P(args, 4), P(args, 5));
                default: throw new InvalidOperationException($"NativeInvoker supports up to 6 int64-return parameters (got {args.Count}). Use IntPtr return type for more.");
            }
        }

        private static double InvokeAllDouble(IntPtr fn, IReadOnlyList<NativeMarshaller.MarshalledArg> args)
        {
            switch (args.Count)
            {
                case 0: return Marshal.GetDelegateForFunctionPointer<Fn_D0_0>(fn)();
                case 1: return Marshal.GetDelegateForFunctionPointer<Fn_D1>(fn)(args[0].FloatValue);
                case 2: return Marshal.GetDelegateForFunctionPointer<Fn_D2>(fn)(args[0].FloatValue, args[1].FloatValue);
                case 3: return Marshal.GetDelegateForFunctionPointer<Fn_D3>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue);
                case 4: return Marshal.GetDelegateForFunctionPointer<Fn_D4>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                default: throw new InvalidOperationException("NativeInvoker double-only path supports up to 4 parameters.");
            }
        }

        private static IntPtr P(IReadOnlyList<NativeMarshaller.MarshalledArg> args, int i) => new IntPtr(args[i].IntegralValue);
    }
}

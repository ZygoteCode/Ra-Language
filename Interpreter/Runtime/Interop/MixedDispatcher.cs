using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    public static class MixedDispatcher
    {
        public static bool TryInvoke(IntPtr fn, NativeTypeKind retKind, IReadOnlyList<NativeTypeKind> kinds, IReadOnlyList<NativeMarshaller.MarshalledArg> args, out long intResult, out double doubleResult)
        {
            intResult = 0; doubleResult = 0;
            if (kinds.Count > 4) return false;

            string pat = string.Empty;
            for (int i = 0; i < kinds.Count; i++)
            {
                pat += (kinds[i] == NativeTypeKind.Float || kinds[i] == NativeTypeKind.Double) ? "F" : "I";
            }
            if (pat.Length == 0) pat = "0";

            string r = retKind switch {
                NativeTypeKind.Void => "V",
                NativeTypeKind.Float => "F32",
                NativeTypeKind.Double => "F64",
                NativeTypeKind.Int32 => "I32",
                NativeTypeKind.Int64 => "I64",
                _ => "P"
            };

            string key = r + "_" + pat;
            switch (key)
            {
                case "V_0":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_0>(fn)();
                    return true;
                case "V_I":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_I>(fn)(new IntPtr(args[0].IntegralValue));
                    return true;
                case "V_F":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_F>(fn)(args[0].FloatValue);
                    return true;
                case "V_II":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_II>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue));
                    return true;
                case "V_IF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue);
                    return true;
                case "V_FI":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue));
                    return true;
                case "V_FF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FF>(fn)(args[0].FloatValue, args[1].FloatValue);
                    return true;
                case "V_III":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_III>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "V_IIF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "V_IFI":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "V_IFF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "V_FII":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "V_FIF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "V_FFI":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FFI>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "V_FFF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "V_IIII":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IIII>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "V_IIIF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IIIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "V_IIFI":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IIFI>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "V_IIFF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IIFF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "V_IFII":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IFII>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "V_IFIF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IFIF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "V_IFFI":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IFFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "V_IFFF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_IFFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "V_FIII":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FIII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "V_FIIF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FIIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "V_FIFI":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FIFI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "V_FIFF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FIFF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "V_FFII":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FFII>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "V_FFIF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FFIF>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "V_FFFI":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FFFI>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "V_FFFF":
                    Marshal.GetDelegateForFunctionPointer<MixedDelegates.V_FFFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "P_0":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_0>(fn)().ToInt64();
                    return true;
                case "P_I":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_I>(fn)(new IntPtr(args[0].IntegralValue)).ToInt64();
                    return true;
                case "P_F":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_F>(fn)(args[0].FloatValue).ToInt64();
                    return true;
                case "P_II":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_II>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue)).ToInt64();
                    return true;
                case "P_IF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue).ToInt64();
                    return true;
                case "P_FI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue)).ToInt64();
                    return true;
                case "P_FF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FF>(fn)(args[0].FloatValue, args[1].FloatValue).ToInt64();
                    return true;
                case "P_III":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_III>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue)).ToInt64();
                    return true;
                case "P_IIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue).ToInt64();
                    return true;
                case "P_IFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue)).ToInt64();
                    return true;
                case "P_IFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue).ToInt64();
                    return true;
                case "P_FII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue)).ToInt64();
                    return true;
                case "P_FIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue).ToInt64();
                    return true;
                case "P_FFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FFI>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue)).ToInt64();
                    return true;
                case "P_FFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue).ToInt64();
                    return true;
                case "P_IIII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IIII>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue)).ToInt64();
                    return true;
                case "P_IIIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IIIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue).ToInt64();
                    return true;
                case "P_IIFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IIFI>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue)).ToInt64();
                    return true;
                case "P_IIFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IIFF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue).ToInt64();
                    return true;
                case "P_IFII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IFII>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue)).ToInt64();
                    return true;
                case "P_IFIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IFIF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue).ToInt64();
                    return true;
                case "P_IFFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IFFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue)).ToInt64();
                    return true;
                case "P_IFFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_IFFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, args[3].FloatValue).ToInt64();
                    return true;
                case "P_FIII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FIII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue)).ToInt64();
                    return true;
                case "P_FIIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FIIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue).ToInt64();
                    return true;
                case "P_FIFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FIFI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue)).ToInt64();
                    return true;
                case "P_FIFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FIFF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue).ToInt64();
                    return true;
                case "P_FFII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FFII>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue)).ToInt64();
                    return true;
                case "P_FFIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FFIF>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue).ToInt64();
                    return true;
                case "P_FFFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FFFI>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue)).ToInt64();
                    return true;
                case "P_FFFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.P_FFFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, args[3].FloatValue).ToInt64();
                    return true;
                case "I32_0":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_0>(fn)();
                    return true;
                case "I32_I":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_I>(fn)(new IntPtr(args[0].IntegralValue));
                    return true;
                case "I32_F":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_F>(fn)(args[0].FloatValue);
                    return true;
                case "I32_II":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_II>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue));
                    return true;
                case "I32_IF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue);
                    return true;
                case "I32_FI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue));
                    return true;
                case "I32_FF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FF>(fn)(args[0].FloatValue, args[1].FloatValue);
                    return true;
                case "I32_III":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_III>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "I32_IIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "I32_IFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "I32_IFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "I32_FII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "I32_FIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "I32_FFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FFI>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "I32_FFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "I32_IIII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IIII>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "I32_IIIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IIIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "I32_IIFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IIFI>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "I32_IIFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IIFF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "I32_IFII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IFII>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "I32_IFIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IFIF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "I32_IFFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IFFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "I32_IFFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_IFFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "I32_FIII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FIII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "I32_FIIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FIIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "I32_FIFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FIFI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "I32_FIFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FIFF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "I32_FFII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FFII>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "I32_FFIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FFIF>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "I32_FFFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FFFI>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "I32_FFFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I32_FFFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "I64_0":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_0>(fn)();
                    return true;
                case "I64_I":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_I>(fn)(new IntPtr(args[0].IntegralValue));
                    return true;
                case "I64_F":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_F>(fn)(args[0].FloatValue);
                    return true;
                case "I64_II":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_II>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue));
                    return true;
                case "I64_IF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue);
                    return true;
                case "I64_FI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue));
                    return true;
                case "I64_FF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FF>(fn)(args[0].FloatValue, args[1].FloatValue);
                    return true;
                case "I64_III":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_III>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "I64_IIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "I64_IFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "I64_IFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "I64_FII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "I64_FIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "I64_FFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FFI>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "I64_FFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "I64_IIII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IIII>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "I64_IIIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IIIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "I64_IIFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IIFI>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "I64_IIFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IIFF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "I64_IFII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IFII>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "I64_IFIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IFIF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "I64_IFFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IFFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "I64_IFFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_IFFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "I64_FIII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FIII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "I64_FIIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FIIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "I64_FIFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FIFI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "I64_FIFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FIFF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "I64_FFII":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FFII>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "I64_FFIF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FFIF>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "I64_FFFI":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FFFI>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "I64_FFFF":
                    intResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.I64_FFFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "F32_0":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_0>(fn)();
                    return true;
                case "F32_I":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_I>(fn)(new IntPtr(args[0].IntegralValue));
                    return true;
                case "F32_F":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_F>(fn)(args[0].FloatValue);
                    return true;
                case "F32_II":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_II>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue));
                    return true;
                case "F32_IF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue);
                    return true;
                case "F32_FI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue));
                    return true;
                case "F32_FF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FF>(fn)(args[0].FloatValue, args[1].FloatValue);
                    return true;
                case "F32_III":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_III>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "F32_IIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "F32_IFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "F32_IFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "F32_FII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "F32_FIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "F32_FFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FFI>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "F32_FFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "F32_IIII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IIII>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "F32_IIIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IIIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "F32_IIFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IIFI>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "F32_IIFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IIFF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "F32_IFII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IFII>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "F32_IFIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IFIF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "F32_IFFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IFFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "F32_IFFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_IFFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "F32_FIII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FIII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "F32_FIIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FIIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "F32_FIFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FIFI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "F32_FIFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FIFF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "F32_FFII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FFII>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "F32_FFIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FFIF>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "F32_FFFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FFFI>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "F32_FFFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F32_FFFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "F64_0":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_0>(fn)();
                    return true;
                case "F64_I":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_I>(fn)(new IntPtr(args[0].IntegralValue));
                    return true;
                case "F64_F":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_F>(fn)(args[0].FloatValue);
                    return true;
                case "F64_II":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_II>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue));
                    return true;
                case "F64_IF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue);
                    return true;
                case "F64_FI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue));
                    return true;
                case "F64_FF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FF>(fn)(args[0].FloatValue, args[1].FloatValue);
                    return true;
                case "F64_III":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_III>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "F64_IIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "F64_IFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "F64_IFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "F64_FII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue));
                    return true;
                case "F64_FIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue);
                    return true;
                case "F64_FFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FFI>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue));
                    return true;
                case "F64_FFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue);
                    return true;
                case "F64_IIII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IIII>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "F64_IIIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IIIF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "F64_IIFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IIFI>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "F64_IIFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IIFF>(fn)(new IntPtr(args[0].IntegralValue), new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "F64_IFII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IFII>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "F64_IFIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IFIF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "F64_IFFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IFFI>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "F64_IFFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_IFFF>(fn)(new IntPtr(args[0].IntegralValue), args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "F64_FIII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FIII>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "F64_FIIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FIIF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "F64_FIFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FIFI>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "F64_FIFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FIFF>(fn)(args[0].FloatValue, new IntPtr(args[1].IntegralValue), args[2].FloatValue, args[3].FloatValue);
                    return true;
                case "F64_FFII":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FFII>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), new IntPtr(args[3].IntegralValue));
                    return true;
                case "F64_FFIF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FFIF>(fn)(args[0].FloatValue, args[1].FloatValue, new IntPtr(args[2].IntegralValue), args[3].FloatValue);
                    return true;
                case "F64_FFFI":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FFFI>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, new IntPtr(args[3].IntegralValue));
                    return true;
                case "F64_FFFF":
                    doubleResult = Marshal.GetDelegateForFunctionPointer<MixedDelegates.F64_FFFF>(fn)(args[0].FloatValue, args[1].FloatValue, args[2].FloatValue, args[3].FloatValue);
                    return true;
                default: return false;
            }
        }
    }
}

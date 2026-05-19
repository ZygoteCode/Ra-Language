using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    public static class NativeMarshaller
    {
        public static NativeTypeKind ResolveKind(TypeDescriptor? td, NativeCharset charset)
        {
            if (td == null) return NativeTypeKind.IntPtr;
            var name = td.Name?.ToLowerInvariant() ?? "";
            switch (name)
            {
                case "void": return NativeTypeKind.Void;
                case "bool": return NativeTypeKind.Bool;
                case "byte": return NativeTypeKind.UInt8;
                case "sbyte": return NativeTypeKind.Int8;
                case "short": return NativeTypeKind.Int16;
                case "ushort": return NativeTypeKind.UInt16;
                case "int": return NativeTypeKind.Int32;
                case "uint": return NativeTypeKind.UInt32;
                case "long": return NativeTypeKind.Int64;
                case "ulong": return NativeTypeKind.UInt64;
                case "float": return NativeTypeKind.Float;
                case "double": return NativeTypeKind.Double;
                case "string":
                    return charset switch
                    {
                        NativeCharset.Utf8 => NativeTypeKind.StringUtf8,
                        NativeCharset.Ansi => NativeTypeKind.StringAnsi,
                        NativeCharset.Utf16 => NativeTypeKind.StringUtf16,
                        NativeCharset.Native => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? NativeTypeKind.StringUtf16 : NativeTypeKind.StringUtf8,
                        _ => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? NativeTypeKind.StringUtf16 : NativeTypeKind.StringUtf8
                    };
                case "ptr":
                case "pointer":
                case "native_ptr":
                case "handle":
                case "intptr": return NativeTypeKind.IntPtr;
                case "buffer":
                case "bytes":
                case "list": return NativeTypeKind.Buffer;
                default:
                    return NativeTypeKind.IntPtr;
            }
        }

        public readonly struct MarshalledArg
        {
            public long IntegralValue { get; }
            public double FloatValue { get; }
            public bool IsFloat { get; }
            public IntPtr OwnedBuffer { get; }
            public int OwnedBufferKind { get; }

            public MarshalledArg(long intValue, double floatValue, bool isFloat, IntPtr ownedBuffer, int ownedBufferKind)
            {
                IntegralValue = intValue;
                FloatValue = floatValue;
                IsFloat = isFloat;
                OwnedBuffer = ownedBuffer;
                OwnedBufferKind = ownedBufferKind;
            }
        }

        public static MarshalledArg ToNative(NativeTypeKind kind, RuntimeValue value, NativeCharset charset, List<IntPtr> ownedBuffers)
        {
            switch (kind)
            {
                case NativeTypeKind.Void:
                    return new MarshalledArg(0, 0, false, IntPtr.Zero, 0);

                case NativeTypeKind.Bool:
                    return new MarshalledArg(BoolFrom(value) ? 1 : 0, 0, false, IntPtr.Zero, 0);

                case NativeTypeKind.Int8:
                case NativeTypeKind.Int16:
                case NativeTypeKind.Int32:
                case NativeTypeKind.Int64:
                case NativeTypeKind.UInt8:
                case NativeTypeKind.UInt16:
                case NativeTypeKind.UInt32:
                case NativeTypeKind.UInt64:
                case NativeTypeKind.IntPtr:
                    return new MarshalledArg(IntegralFrom(value), 0, false, IntPtr.Zero, 0);

                case NativeTypeKind.Float:
                case NativeTypeKind.Double:
                    return new MarshalledArg(0, FloatFrom(value), true, IntPtr.Zero, 0);

                case NativeTypeKind.StringUtf16:
                {
                    if (value is NullValue || value == null) return new MarshalledArg(0, 0, false, IntPtr.Zero, 0);
                    var s = StringFrom(value);
                    var buf = Marshal.StringToCoTaskMemUni(s);
                    ownedBuffers.Add(buf);
                    return new MarshalledArg(buf.ToInt64(), 0, false, buf, 1);
                }

                case NativeTypeKind.StringUtf8:
                {
                    if (value is NullValue || value == null) return new MarshalledArg(0, 0, false, IntPtr.Zero, 0);
                    var s = StringFrom(value);
                    var buf = Marshal.StringToCoTaskMemUTF8(s);
                    ownedBuffers.Add(buf);
                    return new MarshalledArg(buf.ToInt64(), 0, false, buf, 2);
                }

                case NativeTypeKind.StringAnsi:
                {
                    if (value is NullValue || value == null) return new MarshalledArg(0, 0, false, IntPtr.Zero, 0);
                    var s = StringFrom(value);
                    var buf = Marshal.StringToCoTaskMemAnsi(s);
                    ownedBuffers.Add(buf);
                    return new MarshalledArg(buf.ToInt64(), 0, false, buf, 3);
                }

                case NativeTypeKind.Handle:
                case NativeTypeKind.Pointer:
                {
                    if (value is NativeHandleValue nh) return new MarshalledArg(nh.Handle.ToInt64(), 0, false, IntPtr.Zero, 0);
                    if (value is NullValue || value == null) return new MarshalledArg(0, 0, false, IntPtr.Zero, 0);
                    return new MarshalledArg(IntegralFrom(value), 0, false, IntPtr.Zero, 0);
                }

                case NativeTypeKind.Buffer:
                {
                    if (value is NullValue || value == null) return new MarshalledArg(0, 0, false, IntPtr.Zero, 0);
                    if (value is NativeHandleValue nh2) return new MarshalledArg(nh2.Handle.ToInt64(), 0, false, IntPtr.Zero, 0);
                    if (value is ListValue lv)
                    {
                        var bytes = new byte[lv.Elements.Count];
                        for (int i = 0; i < lv.Elements.Count; i++) bytes[i] = (byte)(IntegralFrom(lv.Elements[i]) & 0xff);
                        var buf = Marshal.AllocHGlobal(bytes.Length);
                        Marshal.Copy(bytes, 0, buf, bytes.Length);
                        ownedBuffers.Add(buf);
                        return new MarshalledArg(buf.ToInt64(), 0, false, buf, 4);
                    }
                    return new MarshalledArg(IntegralFrom(value), 0, false, IntPtr.Zero, 0);
                }

                default:
                    return new MarshalledArg(IntegralFrom(value), 0, false, IntPtr.Zero, 0);
            }
        }

        public static RuntimeValue FromNative(NativeTypeKind kind, long intResult, double doubleResult, NativeCharset charset)
        {
            switch (kind)
            {
                case NativeTypeKind.Void: return new NullValue();
                case NativeTypeKind.Bool: return new BooleanValue(intResult != 0);
                case NativeTypeKind.Int8: return new IntegerValue((sbyte)intResult);
                case NativeTypeKind.Int16: return new IntegerValue((short)intResult);
                case NativeTypeKind.Int32: return new IntegerValue((int)intResult);
                case NativeTypeKind.Int64: return new LongValue(intResult);
                case NativeTypeKind.UInt8: return new IntegerValue((byte)intResult);
                case NativeTypeKind.UInt16: return new IntegerValue((ushort)intResult);
                case NativeTypeKind.UInt32: return new UnsignedIntegerValue((uint)intResult);
                case NativeTypeKind.UInt64: return new UnsignedLongValue((ulong)intResult);
                case NativeTypeKind.Float: return new FloatValue((float)doubleResult);
                case NativeTypeKind.Double: return new DoubleValue(doubleResult);
                case NativeTypeKind.IntPtr:
                case NativeTypeKind.Handle:
                case NativeTypeKind.Pointer:
                    return new NativeHandleValue(new IntPtr(intResult), NativeHandleKind.Pointer);
                case NativeTypeKind.StringUtf16:
                    return new StringValue(Marshal.PtrToStringUni(new IntPtr(intResult)) ?? "");
                case NativeTypeKind.StringUtf8:
                    return new StringValue(Marshal.PtrToStringUTF8(new IntPtr(intResult)) ?? "");
                case NativeTypeKind.StringAnsi:
                    return new StringValue(Marshal.PtrToStringAnsi(new IntPtr(intResult)) ?? "");
                default:
                    return new LongValue(intResult);
            }
        }

        public static void FreeOwnedBuffers(List<IntPtr> ownedBuffers, IReadOnlyList<NativeTypeKind> paramKinds, List<MarshalledArg> marshalled)
        {
            for (int i = 0; i < marshalled.Count; i++)
            {
                if (marshalled[i].OwnedBuffer == IntPtr.Zero) continue;
                switch (marshalled[i].OwnedBufferKind)
                {
                    case 1: Marshal.ZeroFreeCoTaskMemUnicode(marshalled[i].OwnedBuffer); break;
                    case 2: Marshal.ZeroFreeCoTaskMemUTF8(marshalled[i].OwnedBuffer); break;
                    case 3: Marshal.ZeroFreeCoTaskMemAnsi(marshalled[i].OwnedBuffer); break;
                    case 4: Marshal.FreeHGlobal(marshalled[i].OwnedBuffer); break;
                }
            }
            ownedBuffers.Clear();
        }

        private static long IntegralFrom(RuntimeValue v)
        {
            switch (v)
            {
                case IntegerValue iv: return iv.Value;
                case LongValue lv: return lv.Value;
                case ShortValue sv: return sv.Value;
                case ByteValue bv: return bv.Value;
                case UnsignedIntegerValue ui: return ui.Value;
                case UnsignedLongValue ul: return (long)ul.Value;
                case UnsignedShortValue us: return us.Value;
                case Int128Value i128: return (long)i128.Value;
                case UnsignedInt128Value u128: return (long)u128.Value;
                case FloatValue fv: return (long)fv.Value;
                case DoubleValue dv: return (long)dv.Value;
                case DecimalValue dcv: return (long)dcv.Value;
                case NumberValue nv:
                    try { return (long)nv.Value; }
                    catch { return long.TryParse(nv.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ll) ? ll : 0L; }
                case BooleanValue bo: return bo.Value ? 1 : 0;
                case NativeHandleValue nh: return nh.Handle.ToInt64();
                case NullValue: return 0;
                default: return 0;
            }
        }

        private static double FloatFrom(RuntimeValue v)
        {
            switch (v)
            {
                case FloatValue fv: return fv.Value;
                case DoubleValue dv: return dv.Value;
                case DecimalValue dcv: return (double)dcv.Value;
                case IntegerValue iv: return iv.Value;
                case LongValue lv: return lv.Value;
                case NumberValue nv:
                    if (double.TryParse(nv.Value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                    return 0;
                default: return IntegralFrom(v);
            }
        }

        private static bool BoolFrom(RuntimeValue v)
        {
            if (v is BooleanValue bv) return bv.Value;
            if (v is NullValue) return false;
            return v != null && v.IsTrue();
        }

        private static string StringFrom(RuntimeValue v)
        {
            if (v is StringValue sv) return sv.Value;
            return v?.ToString() ?? "";
        }
    }
}

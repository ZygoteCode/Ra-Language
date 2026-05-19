using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    /// <summary>
    /// Sequential-layout descriptor for a Ra struct type that can be moved across
    /// the FFI boundary as a byte buffer. Only primitive fields are blittable today
    /// (int*/uint*/bool/float/double/byte/short/long/ptr). Strings and nested
    /// structs are rejected at layout build time with a clear diagnostic.
    /// </summary>
    public sealed class NativeStructLayout
    {
        public StructTypeValue StructType { get; }
        public IReadOnlyList<NativeStructField> Fields { get; }
        public int Size { get; }
        public int Alignment { get; }

        private static readonly ConcurrentDictionary<StructTypeValue, NativeStructLayout> _cache = new();

        private NativeStructLayout(StructTypeValue type, List<NativeStructField> fields, int size, int alignment)
        {
            StructType = type; Fields = fields; Size = size; Alignment = alignment;
        }

        public static (NativeStructLayout? layout, string? error) Build(StructTypeValue type, RaLanguage.Interpreter.Runtime.SymbolTable? lookupTable = null)
        {
            return BuildInternal(type, new HashSet<StructTypeValue>(), lookupTable);
        }

        private static (NativeStructLayout? layout, string? error) BuildInternal(StructTypeValue type, HashSet<StructTypeValue> stack, RaLanguage.Interpreter.Runtime.SymbolTable? lookupTable)
        {
            if (_cache.TryGetValue(type, out var existing)) return (existing, null);
            if (!stack.Add(type)) return (null, $"struct '{type.StructName}': recursive layout detected");

            var fields = new List<NativeStructField>();
            int offset = 0;
            int maxAlign = 1;

            foreach (var f in type.Fields)
            {
                if (f.IsStatic) continue;
                var name = f.NameTok.Value?.ToString() ?? "";
                if (f.FieldType == null)
                {
                    stack.Remove(type);
                    return (null, $"struct '{type.StructName}': field '{name}' has no explicit type; cannot marshal across native boundary");
                }

                var resolvedTypeName = f.FieldType.Name ?? "";
                NativeStructLayout? nestedLayout = null;
                RaLanguage.Interpreter.Values.RuntimeValue? lookedUp =
                    lookupTable?.Get(resolvedTypeName)
                    ?? RaLanguage.Program.GlobalSymbolTable?.Get(resolvedTypeName);
                if (lookedUp is StructTypeValue nestedStruct)
                {
                    var (nl, err) = BuildInternal(nestedStruct, stack, lookupTable);
                    if (err != null)
                    {
                        stack.Remove(type);
                        return (null, $"struct '{type.StructName}': nested field '{name}' -> {err}");
                    }
                    nestedLayout = nl;
                    int alignN = nl!.Alignment;
                    offset = AlignUp(offset, alignN);
                    fields.Add(new NativeStructField(name, NativeTypeKind.Pointer, offset, nl.Size, nl));
                    offset += nl.Size;
                    if (alignN > maxAlign) maxAlign = alignN;
                    continue;
                }

                var kind = NativeMarshaller.ResolveKind(f.FieldType, NativeCharset.Auto);
                if (!IsBlittablePrimitive(kind))
                {
                    stack.Remove(type);
                    return (null, $"struct '{type.StructName}': field '{name}' has non-blittable type '{f.FieldType}'; only primitives/pointers/nested-structs are supported");
                }

                int width = WidthOf(kind);
                int align = width;
                offset = AlignUp(offset, align);
                fields.Add(new NativeStructField(name, kind, offset, width));
                offset += width;
                if (align > maxAlign) maxAlign = align;
            }

            int size = AlignUp(offset, maxAlign);
            var layout = new NativeStructLayout(type, fields, size, maxAlign);
            _cache.TryAdd(type, layout);
            stack.Remove(type);
            return (layout, null);
        }

        private static bool IsBlittablePrimitive(NativeTypeKind k) =>
            k == NativeTypeKind.Bool || k == NativeTypeKind.Int8 || k == NativeTypeKind.UInt8
            || k == NativeTypeKind.Int16 || k == NativeTypeKind.UInt16
            || k == NativeTypeKind.Int32 || k == NativeTypeKind.UInt32
            || k == NativeTypeKind.Int64 || k == NativeTypeKind.UInt64
            || k == NativeTypeKind.Float || k == NativeTypeKind.Double
            || k == NativeTypeKind.IntPtr || k == NativeTypeKind.Handle || k == NativeTypeKind.Pointer;

        private static int WidthOf(NativeTypeKind k) => k switch
        {
            NativeTypeKind.Bool => 4,
            NativeTypeKind.Int8 or NativeTypeKind.UInt8 => 1,
            NativeTypeKind.Int16 or NativeTypeKind.UInt16 => 2,
            NativeTypeKind.Int32 or NativeTypeKind.UInt32 or NativeTypeKind.Float => 4,
            NativeTypeKind.Int64 or NativeTypeKind.UInt64 or NativeTypeKind.Double => 8,
            _ => IntPtr.Size
        };

        private static int AlignUp(int offset, int align) => (offset + align - 1) & ~(align - 1);

        public IntPtr SerializeInstance(StructInstanceValue instance)
        {
            var buf = Marshal.AllocHGlobal(Size);
            unsafe
            {
                byte* p = (byte*)buf;
                for (int i = 0; i < Size; i++) p[i] = 0;
            }
            foreach (var f in Fields)
            {
                if (!instance.HasField(f.Name)) continue;
                var v = instance.GetField(f.Name);
                WriteField(buf, f, v);
            }
            return buf;
        }

        public void ReadInto(StructInstanceValue instance, IntPtr buf)
        {
            foreach (var f in Fields)
            {
                if (!instance.HasField(f.Name)) continue;
                var newVal = ReadField(buf, f);
                bool wasPublic = instance.IsFieldPublic(f.Name);
                instance.SetField(f.Name, newVal, wasPublic, instance.GetFieldDeclarationType(f.Name));
            }
        }

        public StructInstanceValue Materialize(IntPtr buf)
        {
            var instance = new StructInstanceValue(StructType);
            foreach (var sf in StructType.Fields)
            {
                if (sf.IsStatic) continue;
                instance.SetField(sf.NameTok.Value?.ToString() ?? "", NullValue.Null, sf.IsPublic, sf.DeclarationType);
            }
            ReadInto(instance, buf);
            return instance;
        }

        private static long IntegralFrom(Values.RuntimeValue v)
        {
            return v switch
            {
                IntegerValue iv => iv.Value,
                LongValue lv => lv.Value,
                BooleanValue bv => bv.Value ? 1 : 0,
                FloatValue fv => (long)fv.Value,
                DoubleValue dv => (long)dv.Value,
                NumberValue nv => Convert.ToInt64(nv.ToString(), System.Globalization.CultureInfo.InvariantCulture),
                NativeHandleValue nh => nh.Handle.ToInt64(),
                NullValue => 0,
                _ => Convert.ToInt64(v?.ToString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        private static double DoubleFrom(Values.RuntimeValue v) => v switch
        {
            DoubleValue dv => dv.Value,
            FloatValue fv => fv.Value,
            IntegerValue iv => iv.Value,
            LongValue lv => lv.Value,
            NullValue => 0,
            _ => Convert.ToDouble(v?.ToString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
        };

        private static void WriteField(IntPtr buf, NativeStructField f, Values.RuntimeValue v)
        {
            if (f.NestedLayout != null && v is StructInstanceValue nestedInstance)
            {
                IntPtr nestedBufPtr = IntPtr.Add(buf, f.Offset);
                unsafe
                {
                    byte* tgt = (byte*)nestedBufPtr;
                    for (int i = 0; i < f.NestedLayout.Size; i++) tgt[i] = 0;
                }
                foreach (var inner in f.NestedLayout.Fields)
                {
                    if (!nestedInstance.HasField(inner.Name)) continue;
                    WriteField(nestedBufPtr, inner, nestedInstance.GetField(inner.Name));
                }
                return;
            }

            unsafe
            {
                byte* p = (byte*)buf + f.Offset;
                switch (f.Kind)
                {
                    case NativeTypeKind.Bool:
                    case NativeTypeKind.Int32:
                    case NativeTypeKind.UInt32: *(int*)p = (int)IntegralFrom(v); break;
                    case NativeTypeKind.Int8: *(sbyte*)p = (sbyte)IntegralFrom(v); break;
                    case NativeTypeKind.UInt8: *p = (byte)(IntegralFrom(v) & 0xff); break;
                    case NativeTypeKind.Int16: *(short*)p = (short)IntegralFrom(v); break;
                    case NativeTypeKind.UInt16: *(ushort*)p = (ushort)IntegralFrom(v); break;
                    case NativeTypeKind.Int64:
                    case NativeTypeKind.UInt64: *(long*)p = IntegralFrom(v); break;
                    case NativeTypeKind.Float: *(float*)p = (float)DoubleFrom(v); break;
                    case NativeTypeKind.Double: *(double*)p = DoubleFrom(v); break;
                    default: *(IntPtr*)p = v is NativeHandleValue nh ? nh.Handle : new IntPtr(IntegralFrom(v)); break;
                }
            }
        }

        private static Values.RuntimeValue ReadField(IntPtr buf, NativeStructField f)
        {
            if (f.NestedLayout != null)
            {
                IntPtr nestedBufPtr = IntPtr.Add(buf, f.Offset);
                return f.NestedLayout.Materialize(nestedBufPtr);
            }
            unsafe
            {
                byte* p = (byte*)buf + f.Offset;
                return f.Kind switch
                {
                    NativeTypeKind.Bool => BooleanValue.Of(*(int*)p != 0),
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
                    _ => (Values.RuntimeValue)new NativeHandleValue(*(IntPtr*)p, NativeHandleKind.Pointer)
                };
            }
        }
    }

    public readonly struct NativeStructField
    {
        public string Name { get; }
        public NativeTypeKind Kind { get; }
        public int Offset { get; }
        public int Width { get; }
        public NativeStructLayout? NestedLayout { get; }

        public NativeStructField(string name, NativeTypeKind kind, int offset, int width, NativeStructLayout? nested = null)
        {
            Name = name; Kind = kind; Offset = offset; Width = width; NestedLayout = nested;
        }
    }
}

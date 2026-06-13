using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Namespaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Structs;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Lexer;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    internal static class BuiltinUtils
    {
        public static RuntimeResult Fail(Context ctx, Position p1, Position p2, string message)
        {
            return new RuntimeResult().Failure(new RuntimeError(p1, p2, message, ctx));
        }

        public static RuntimeResult Ok(RuntimeValue value, Context ctx, Position p1, Position p2)
        {
            return new RuntimeResult().Success(value.SetContext(ctx).SetPos(p1, p2));
        }

        public static RuntimeResult OkNull(Context ctx, Position p1, Position p2)
        {
            return Ok(NullValue.Null, ctx, p1, p2);
        }

        public static bool ExpectArgs(string name, List<RuntimeValue> args, int expected, Context ctx, Position p1, Position p2, out RuntimeResult err)
        {
            if (args.Count != expected)
            {
                err = Fail(ctx, p1, p2, $"{name} expects {expected} argument(s), got {args.Count}");
                return false;
            }
            err = default!;
            return true;
        }

        public static bool ExpectMinArgs(string name, List<RuntimeValue> args, int min, Context ctx, Position p1, Position p2, out RuntimeResult err)
        {
            if (args.Count < min)
            {
                err = Fail(ctx, p1, p2, $"{name} expects at least {min} argument(s), got {args.Count}");
                return false;
            }
            err = default!;
            return true;
        }

        public static bool ExpectRangeArgs(string name, List<RuntimeValue> args, int min, int max, Context ctx, Position p1, Position p2, out RuntimeResult err)
        {
            if (args.Count < min || args.Count > max)
            {
                err = Fail(ctx, p1, p2, $"{name} expects {min}-{max} argument(s), got {args.Count}");
                return false;
            }
            err = default!;
            return true;
        }

        public static long AsLong(RuntimeValue v)
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
                    catch { return long.TryParse(nv.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0L; }
                case BooleanValue bo: return bo.Value ? 1 : 0;
                case StringValue stv: return long.TryParse(stv.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var lv2) ? lv2 : 0L;
                case NullValue: return 0;
                default: return 0;
            }
        }

        public static int AsInt(RuntimeValue v)
        {
            long l = AsLong(v);
            if (l > int.MaxValue) return int.MaxValue;
            if (l < int.MinValue) return int.MinValue;
            return (int)l;
        }

        public static double AsDouble(RuntimeValue v)
        {
            switch (v)
            {
                case DoubleValue dv: return dv.Value;
                case FloatValue fv: return fv.Value;
                case DecimalValue dcv: return (double)dcv.Value;
                case NumberValue nv:
                    if (double.TryParse(nv.Value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
                    return 0;
                case IntegerValue iv: return iv.Value;
                case LongValue lv: return lv.Value;
                case ShortValue sv: return sv.Value;
                case ByteValue bv: return bv.Value;
                case UnsignedIntegerValue ui: return ui.Value;
                case UnsignedLongValue ul: return ul.Value;
                case UnsignedShortValue us: return us.Value;
                case Int128Value i128: return (double)i128.Value;
                case UnsignedInt128Value u128: return (double)u128.Value;
                case BooleanValue bo: return bo.Value ? 1 : 0;
                case StringValue stv: return double.TryParse(stv.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) ? d2 : 0;
                case NullValue: return 0;
                default: return 0;
            }
        }

        public static string AsString(RuntimeValue v)
        {
            if (v is StringValue sv) return sv.Value;
            return v?.ToString() ?? "";
        }

        public static bool AsBool(RuntimeValue v)
        {
            if (v is BooleanValue bv) return bv.Value;
            if (v is NullValue) return false;
            if (v is StringValue sv) return sv.Value.Length > 0;
            return v != null && v.IsTrue();
        }

        public static RuntimeValue NumberFor(long v)
        {
            if (v >= int.MinValue && v <= int.MaxValue) return new IntegerValue((int)v);
            return new LongValue(v);
        }

        public static RuntimeValue NumberFor(double v)
        {
            return new DoubleValue(v);
        }

        public static RuntimeValue MakeBool(bool b) => BooleanValue.Of(b);

        public static List<RuntimeValue> Strings(IEnumerable<string> seq)
        {
            var r = new List<RuntimeValue>();
            foreach (var s in seq) r.Add(new StringValue(s));
            return r;
        }

        public static List<RuntimeValue> Ints(IEnumerable<int> seq)
        {
            var r = new List<RuntimeValue>();
            foreach (var i in seq) r.Add(new IntegerValue(i));
            return r;
        }

        public static string TypeKind(RuntimeValue v)
        {
            switch (v.Type)
            {
                case RuntimeValueType.Null: return "null";
                case RuntimeValueType.Boolean: return "boolean";
                case RuntimeValueType.String: return "string";
                case RuntimeValueType.Number:
                case RuntimeValueType.Integer:
                case RuntimeValueType.Long:
                case RuntimeValueType.Short:
                case RuntimeValueType.Byte:
                case RuntimeValueType.UnsignedInteger:
                case RuntimeValueType.UnsignedLong:
                case RuntimeValueType.UnsignedShort:
                case RuntimeValueType.Int128:
                case RuntimeValueType.UnsignedInt128:
                case RuntimeValueType.Float:
                case RuntimeValueType.Double:
                case RuntimeValueType.Decimal: return "number";
                case RuntimeValueType.List: return "list";
                case RuntimeValueType.Set: return "set";
                case RuntimeValueType.Map: return "map";
                case RuntimeValueType.Tuple: return "tuple";
                case RuntimeValueType.Function:
                case RuntimeValueType.BaseFunction: return "function";
                case RuntimeValueType.ClassType: return "class";
                case RuntimeValueType.ClassInstance: return "class_instance";
                case RuntimeValueType.StructType: return "struct";
                case RuntimeValueType.StructInstance: return "struct_instance";
                case RuntimeValueType.EnumType: return "enum";
                case RuntimeValueType.Enum: return "enum_value";
                case RuntimeValueType.InterfaceType: return "interface";
                case RuntimeValueType.TraitType: return "trait";
                case RuntimeValueType.AnnotationType: return "annotation_type";
                case RuntimeValueType.AnnotationInstance: return "annotation_instance";
                case RuntimeValueType.Task: return "task";
                case RuntimeValueType.Channel: return "channel";
                case RuntimeValueType.Stream: return "stream";
                case RuntimeValueType.AsyncStream: return "async_stream";
                case RuntimeValueType.Namespace: return "namespace";
                case RuntimeValueType.Reference: return "reference";
                case RuntimeValueType.ModuleWrapper: return "module";
                case RuntimeValueType.GenericTypeBinding: return "generic_binding";
                case RuntimeValueType.NativeHandle: return "native_handle";
                case RuntimeValueType.MemberHandle: return "member_handle";
                case RuntimeValueType.Super: return "super";
                default: return v.Type.ToString().ToLowerInvariant();
            }
        }

        public static string TypeName(RuntimeValue v)
        {
            switch (v)
            {
                case ClassInstanceValue ci: return FormatClassName(ci.Definition);
                case StructInstanceValue si: return FormatStructName(si.Definition);
                case ClassTypeValue ct: return FormatClassName(ct);
                case StructTypeValue st: return FormatStructName(st);
                case EnumValue ev: return ev.EnumName;
                case EnumTypeValue et: return et.EnumName;
                case InterfaceTypeValue iv: return iv.InterfaceName;
                case TraitTypeValue tv: return tv.TraitName;
            }
            return TypeKind(v);
        }

        private static string FormatClassName(ClassTypeValue ct)
        {
            if (ct.GenericTypeParams == null || ct.GenericTypeParams.Count == 0) return ct.ClassName;
            return ct.ClassName + "<" + string.Join(", ", ct.GenericTypeParams) + ">";
        }

        private static string FormatStructName(StructTypeValue st)
        {
            if (st.GenericTypeParams == null || st.GenericTypeParams.Count == 0) return st.StructName;
            return st.StructName + "<" + string.Join(", ", st.GenericTypeParams) + ">";
        }
    }
}

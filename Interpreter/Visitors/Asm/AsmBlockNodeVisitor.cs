using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Asm;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Runtime.Interop;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Asm;

namespace RaLanguage.Interpreter.Visitors.Asm
{
    /// <summary>
    /// Executes inline `asm { ... }` and `asm -> T { ... }` blocks.
    ///
    /// Interpolation modes:
    ///   %{expr}             — integer / pointer value, decimal literal
    ///   %{expr:i32|i64|u8…} — explicit width; floats encoded as 64-bit hex bits
    ///   %{expr:hex}         — hex literal
    ///   %{expr:f64}         — emit float bits as integer (no FP literal)
    /// </summary>
    public sealed class AsmBlockNodeVisitor : NodeVisitor<AsmBlockNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(AsmBlockNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (!AsmExecutor.IsSupported)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "asm blocks require an x64 process. x86 / non-x64 architectures are not supported.", context));
            }

            var sb = new StringBuilder();

            for (int i = 0; i < node.Parts.Count; i++)
            {
                var part = node.Parts[i];
                if (part is AsmTextPartNode text)
                {
                    sb.Append(text.Text);
                    continue;
                }

                string? typeHint = null;
                AstNode evalPart = part;
                if (part is AsmInterpPartNode ip)
                {
                    typeHint = ip.TypeHint;
                    evalPart = ip.Expr;
                }

                var val = res.Register(await interpreter.Visit(evalPart, context));
                if (res.ShouldReturn()) return res;
                if (val == null)
                {
                    return res.Failure(new RuntimeError(part.PositionStart, part.PositionEnd,
                        "asm %{...} interpolation produced a null value", context));
                }

                if (!TryFormatInterpolated(val, typeHint, out string formatted, out string? interpErr))
                {
                    return res.Failure(new RuntimeError(part.PositionStart, part.PositionEnd,
                        $"asm %{{...}}: {interpErr}", context));
                }
                sb.Append(formatted);
            }

            string source = sb.ToString();

            string signature = BuildSignatureFromReturnTypes(node.ReturnTypes);

            try
            {
                IntPtr addr = AsmRegionRegistry.GetOrCompile(source);

                if (node.ReturnTypes.Count <= 1)
                {
                    var fn = AsmFunctionFactory.Create("<asm-inline>", addr, signature);
                    RuntimeResult execRes = default;
                    AsmSehGuard.RunVoid(() => execRes = SyncAwait.Get(fn.Execute(new List<RuntimeValue>())));
                    if (execRes.Error != null) return res.Failure(execRes.Error);
                    var value = execRes.Value ?? new LongValue(0);
                    return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }

                if (node.ReturnTypes.Count == 2)
                {
                    var wrapperSrc =
                        "    sub rsp, 40\n" +
                        "    mov [rsp+32], rcx\n" +
                        "    mov rax, " + addr.ToInt64().ToString(CultureInfo.InvariantCulture) + "\n" +
                        "    call rax\n" +
                        "    mov rcx, [rsp+32]\n" +
                        "    mov [rcx], rax\n" +
                        "    mov [rcx+8], rdx\n" +
                        "    add rsp, 40\n" +
                        "    ret\n";
                    IntPtr wrapAddr = AsmRegionRegistry.GetOrCompile(wrapperSrc);

                    IntPtr buf = Marshal.AllocHGlobal(16);
                    try
                    {
                        long lo = 0, hi = 0;
                        IntPtr bufLocal = buf;
                        AsmSehGuard.RunVoid(() =>
                        {
                            var d = Marshal.GetDelegateForFunctionPointer<NativeInvoker.Fn_V1>(wrapAddr);
                            d(bufLocal);
                            lo = Marshal.ReadInt64(bufLocal);
                            hi = Marshal.ReadInt64(bufLocal + 8);
                        });

                        var t0 = NarrowByType(lo, node.ReturnTypes[0]);
                        var t1 = NarrowByType(hi, node.ReturnTypes[1]);
                        var tupleVal = new TupleValue(new List<RuntimeValue> { t0, t1 });
                        return res.Success(tupleVal.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "asm tuple return supports at most 2 values (RAX, RDX)", context));
            }
            catch (AsmAssembleException ax)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"asm assemble error: {ax.Message}", context));
            }
            catch (AsmSehGuard.GuardedFailure gf)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, gf.Message, context));
            }
            catch (PlatformNotSupportedException pex)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, pex.Message, context));
            }
            catch (Exception ex)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"asm runtime error: {ex.Message}", context));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TwoLongsStruct { public long rax; public long rdx; }

        [UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
        private delegate TwoLongsStruct TwoLongs();

        private static string BuildSignatureFromReturnTypes(List<string> rets)
        {
            if (rets == null || rets.Count == 0) return "i64()";
            if (rets.Count == 1) return rets[0] + "()";
            return "i64()";
        }

        private static RuntimeValue NarrowByType(long raw, string typeName)
        {
            switch (typeName.ToLowerInvariant())
            {
                case "i8": case "sbyte": return new IntegerValue((sbyte)(raw & 0xff));
                case "u8": case "byte":  return new IntegerValue((byte)(raw & 0xff));
                case "i16": case "short":return new IntegerValue((short)(raw & 0xffff));
                case "u16": case "ushort":return new IntegerValue((ushort)(raw & 0xffff));
                case "i32": case "int":  return new IntegerValue((int)(raw & 0xffffffff));
                case "u32": case "uint": return new UnsignedIntegerValue((uint)(raw & 0xffffffff));
                case "i64": case "long": return new LongValue(raw);
                case "u64": case "ulong":return new UnsignedLongValue(unchecked((ulong)raw));
                case "bool": return BooleanValue.Of(raw != 0);
                case "ptr": return new NativeHandleValue((IntPtr)raw, NativeHandleKind.Pointer);
                default: return new LongValue(raw);
            }
        }

        private static bool TryFormatInterpolated(RuntimeValue v, string? typeHint, out string formatted, out string? error)
        {
            error = null;
            if (typeHint != null)
            {
                string lower = typeHint.ToLowerInvariant();
                if (lower == "hex" || lower == "x")
                {
                    long lv = ToLongFromAny(v);
                    formatted = "0x" + lv.ToString("X", CultureInfo.InvariantCulture);
                    return true;
                }
                if (lower == "f64" || lower == "double")
                {
                    double d = ToDoubleFromAny(v);
                    long bits = BitConverter.DoubleToInt64Bits(d);
                    formatted = "0x" + bits.ToString("X", CultureInfo.InvariantCulture);
                    return true;
                }
                if (lower == "f32" || lower == "float")
                {
                    float fv = (float)ToDoubleFromAny(v);
                    int bits = BitConverter.SingleToInt32Bits(fv);
                    formatted = "0x" + bits.ToString("X", CultureInfo.InvariantCulture);
                    return true;
                }
            }

            switch (v)
            {
                case IntegerValue iv:    formatted = iv.Value.ToString(CultureInfo.InvariantCulture); return true;
                case LongValue lv:       formatted = lv.Value.ToString(CultureInfo.InvariantCulture); return true;
                case ShortValue sv:      formatted = sv.Value.ToString(CultureInfo.InvariantCulture); return true;
                case ByteValue bv:       formatted = bv.Value.ToString(CultureInfo.InvariantCulture); return true;
                case UnsignedIntegerValue ui: formatted = ui.Value.ToString(CultureInfo.InvariantCulture); return true;
                case UnsignedLongValue ul: formatted = unchecked((long)ul.Value).ToString(CultureInfo.InvariantCulture); return true;
                case UnsignedShortValue us: formatted = us.Value.ToString(CultureInfo.InvariantCulture); return true;
                case BooleanValue bo:    formatted = bo.Value ? "1" : "0"; return true;
                case StringValue str:    formatted = str.Value; return true;
                case NativeHandleValue nh: formatted = nh.Handle.ToInt64().ToString(CultureInfo.InvariantCulture); return true;
                case NumberValue nv:
                {
                    try
                    {
                        long lv2 = (long)nv.Value;
                        formatted = lv2.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch
                    {
                        formatted = "";
                        error = "value does not fit in a 64-bit integer";
                        return false;
                    }
                }
                case FloatValue fv:
                {
                    int bits = BitConverter.SingleToInt32Bits(fv.Value);
                    formatted = "0x" + bits.ToString("X", CultureInfo.InvariantCulture);
                    return true;
                }
                case DoubleValue dv:
                {
                    long bits = BitConverter.DoubleToInt64Bits(dv.Value);
                    formatted = "0x" + bits.ToString("X", CultureInfo.InvariantCulture);
                    return true;
                }
                default:
                    formatted = "";
                    error = $"cannot interpolate value of kind '{v.Type}' into asm";
                    return false;
            }
        }

        private static long ToLongFromAny(RuntimeValue v)
        {
            switch (v)
            {
                case IntegerValue iv: return iv.Value;
                case LongValue lv: return lv.Value;
                case ByteValue bv: return bv.Value;
                case ShortValue sv: return sv.Value;
                case UnsignedIntegerValue ui: return ui.Value;
                case UnsignedLongValue ul: return unchecked((long)ul.Value);
                case UnsignedShortValue us: return us.Value;
                case BooleanValue bo: return bo.Value ? 1 : 0;
                case NativeHandleValue nh: return nh.Handle.ToInt64();
                case FloatValue fv: return (long)fv.Value;
                case DoubleValue dv: return (long)dv.Value;
                case NumberValue nv: try { return (long)nv.Value; } catch { return 0; }
                default: return 0;
            }
        }

        private static double ToDoubleFromAny(RuntimeValue v)
        {
            switch (v)
            {
                case FloatValue fv: return fv.Value;
                case DoubleValue dv: return dv.Value;
                case IntegerValue iv: return iv.Value;
                case LongValue lv: return lv.Value;
                case NumberValue nv: try { return (double)nv.Value; } catch { return 0; }
                default: return 0;
            }
        }
    }
}

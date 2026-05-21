using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using RaLanguage.Interpreter.Runtime.Interop;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Compact signature grammar for asm-compiled functions, intentionally
    /// matching the surface used by `native_invoke` so users can mix the two.
    ///
    /// Grammar: `ret_type(arg1,arg2,...)`
    /// Types: void | i8 | u8 | i16 | u16 | i32 | u32 | i64 | u64
    ///        | f32 | f64 | bool | ptr | int(=i32) | long(=i64)
    ///        | float(=f32) | double(=f64) | uint(=u32) | ulong(=u64)
    ///        | short(=i16) | ushort(=u16) | byte(=u8) | sbyte(=i8)
    ///        | string (=utf8 c-string in)
    /// </summary>
    public static class AsmSignature
    {
        public static (NativeTypeKind ret, List<NativeTypeKind> args) Parse(string signature)
        {
            if (string.IsNullOrWhiteSpace(signature))
                throw new ArgumentException("Empty signature.", nameof(signature));

            int lp = signature.IndexOf('(');
            int rp = signature.LastIndexOf(')');
            if (lp < 0 || rp < 0 || rp < lp)
                throw new ArgumentException("Signature must follow 'ret(args)' form.", nameof(signature));

            string retText = signature.Substring(0, lp).Trim();
            string argsText = signature.Substring(lp + 1, rp - lp - 1).Trim();

            NativeTypeKind ret = ParseType(retText);
            var args = new List<NativeTypeKind>();
            if (argsText.Length > 0)
            {
                foreach (var p in argsText.Split(','))
                {
                    var t = p.Trim();
                    if (t.Length == 0) continue;
                    args.Add(ParseType(t));
                }
            }
            return (ret, args);
        }

        public static NativeTypeKind ParseType(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "void": return NativeTypeKind.Void;
                case "i8": case "sbyte": return NativeTypeKind.Int8;
                case "u8": case "byte": return NativeTypeKind.UInt8;
                case "i16": case "short": return NativeTypeKind.Int16;
                case "u16": case "ushort": return NativeTypeKind.UInt16;
                case "i32": case "int": return NativeTypeKind.Int32;
                case "u32": case "uint": return NativeTypeKind.UInt32;
                case "i64": case "long": return NativeTypeKind.Int64;
                case "u64": case "ulong": return NativeTypeKind.UInt64;
                case "f32": case "float": return NativeTypeKind.Float;
                case "f64": case "double": return NativeTypeKind.Double;
                case "bool": return NativeTypeKind.Bool;
                case "ptr": case "pointer": case "handle": case "intptr": return NativeTypeKind.IntPtr;
                case "string": case "cstr": case "utf8": return NativeTypeKind.StringUtf8;
                case "utf16": case "wstr": return NativeTypeKind.StringUtf16;
                case "buffer": return NativeTypeKind.Buffer;
                default: throw new ArgumentException($"Unknown asm signature type '{s}'.");
            }
        }
    }
}

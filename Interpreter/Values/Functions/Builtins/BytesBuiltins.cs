using System;
using System.Collections.Generic;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    // std.prelude.bytes — binary-buffer helpers. Ra has no distinct byte type,
    // so a "byte buffer" is a list of integers in [0, 255]. These cover the
    // common needs of binary protocols: text<->bytes, hex/base64, slicing,
    // concatenation, XOR, and little/big-endian integer reads. All AOT-safe.
    public static class BytesBuiltins
    {
        private static byte[] ToBytes(RuntimeValue v, string fn)
        {
            if (v is not ListValue lv) throw new RuntimeBuiltinException($"{fn}: expected a byte list");
            var b = new byte[lv.Elements.Count];
            for (int i = 0; i < b.Length; i++)
            {
                long n = AsLong(lv.Elements[i]);
                if (n < 0 || n > 255) throw new RuntimeBuiltinException($"{fn}: byte at index {i} is out of range [0, 255]");
                b[i] = (byte)n;
            }
            return b;
        }

        private static ListValue FromBytes(byte[] b)
        {
            var list = new List<RuntimeValue>(b.Length);
            foreach (var x in b) list.Add(new IntegerValue(x));
            return new ListValue(list);
        }

        public static void Register()
        {
            BuiltInRegistry.Register("bytes_from_string", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_from_string", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(FromBytes(Encoding.UTF8.GetBytes(AsString(args[0]))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("bytes_to_string", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_to_string", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Encoding.UTF8.GetString(ToBytes(args[0], "bytes_to_string"))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("bytes_to_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_to_hex", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Convert.ToHexString(ToBytes(args[0], "bytes_to_hex")).ToLowerInvariant()), ctx, p1, p2);
            });
            BuiltInRegistry.Register("bytes_from_hex", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_from_hex", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(FromBytes(Convert.FromHexString(AsString(args[0]))), ctx, p1, p2); }
                catch (FormatException) { return Fail(ctx, p1, p2, "bytes_from_hex: invalid hex"); }
            });
            BuiltInRegistry.Register("bytes_to_base64", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_to_base64", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(Convert.ToBase64String(ToBytes(args[0], "bytes_to_base64"))), ctx, p1, p2);
            });
            BuiltInRegistry.Register("bytes_from_base64", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_from_base64", args, 1, ctx, p1, p2, out var err)) return err;
                try { return Ok(FromBytes(Convert.FromBase64String(AsString(args[0]))), ctx, p1, p2); }
                catch (FormatException) { return Fail(ctx, p1, p2, "bytes_from_base64: invalid base64"); }
            });
            BuiltInRegistry.Register("bytes_concat", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_concat", args, 2, ctx, p1, p2, out var err)) return err;
                byte[] a = ToBytes(args[0], "bytes_concat"), b = ToBytes(args[1], "bytes_concat");
                var r = new byte[a.Length + b.Length];
                Buffer.BlockCopy(a, 0, r, 0, a.Length);
                Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
                return Ok(FromBytes(r), ctx, p1, p2);
            });
            BuiltInRegistry.Register("bytes_slice", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_slice", args, 3, ctx, p1, p2, out var err)) return err;
                byte[] a = ToBytes(args[0], "bytes_slice");
                int start = AsInt(args[1]), end = AsInt(args[2]);
                if (start < 0) start += a.Length;
                if (end < 0) end += a.Length;
                start = Math.Clamp(start, 0, a.Length);
                end = Math.Clamp(end, start, a.Length);
                var r = new byte[end - start];
                Buffer.BlockCopy(a, start, r, 0, r.Length);
                return Ok(FromBytes(r), ctx, p1, p2);
            });
            BuiltInRegistry.Register("bytes_eq", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_eq", args, 2, ctx, p1, p2, out var err)) return err;
                byte[] a = ToBytes(args[0], "bytes_eq"), b = ToBytes(args[1], "bytes_eq");
                bool eq = a.Length == b.Length;
                for (int i = 0; eq && i < a.Length; i++) if (a[i] != b[i]) eq = false;
                return Ok(MakeBool(eq), ctx, p1, p2);
            });
            BuiltInRegistry.Register("bytes_xor", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_xor", args, 2, ctx, p1, p2, out var err)) return err;
                byte[] a = ToBytes(args[0], "bytes_xor"), b = ToBytes(args[1], "bytes_xor");
                if (b.Length == 0) return Fail(ctx, p1, p2, "bytes_xor: key must be non-empty");
                var r = new byte[a.Length];
                for (int i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ b[i % b.Length]);   // repeating-key
                return Ok(FromBytes(r), ctx, p1, p2);
            });
            BuiltInRegistry.Register("bytes_fill", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("bytes_fill", args, 2, ctx, p1, p2, out var err)) return err;
                int n = AsInt(args[0]);
                if (n < 0) return Fail(ctx, p1, p2, "bytes_fill: count must be non-negative");
                long val = AsLong(args[1]);
                if (val < 0 || val > 255) return Fail(ctx, p1, p2, "bytes_fill: value must be in [0, 255]");
                var r = new byte[n];
                if (val != 0) Array.Fill(r, (byte)val);
                return Ok(FromBytes(r), ctx, p1, p2);
            });
            BuiltInRegistry.Register("bytes_read_u16le", (ctx, args, p1, p2) => ReadInt(ctx, args, p1, p2, "bytes_read_u16le", 2, false));
            BuiltInRegistry.Register("bytes_read_u16be", (ctx, args, p1, p2) => ReadInt(ctx, args, p1, p2, "bytes_read_u16be", 2, true));
            BuiltInRegistry.Register("bytes_read_u32le", (ctx, args, p1, p2) => ReadInt(ctx, args, p1, p2, "bytes_read_u32le", 4, false));
            BuiltInRegistry.Register("bytes_read_u32be", (ctx, args, p1, p2) => ReadInt(ctx, args, p1, p2, "bytes_read_u32be", 4, true));
        }

        private static RuntimeResult ReadInt(Context ctx, List<RuntimeValue> args, Position p1, Position p2, string fn, int width, bool bigEndian)
        {
            if (!ExpectArgs(fn, args, 2, ctx, p1, p2, out var err)) return err;
            byte[] b = ToBytes(args[0], fn);
            int off = AsInt(args[1]);
            if (off < 0 || off + width > b.Length) return Fail(ctx, p1, p2, $"{fn}: offset {off} out of range");
            long v = 0;
            if (bigEndian) for (int i = 0; i < width; i++) v = (v << 8) | b[off + i];
            else for (int i = width - 1; i >= 0; i--) v = (v << 8) | b[off + i];
            return Ok(NumberFor(v), ctx, p1, p2);
        }
    }
}

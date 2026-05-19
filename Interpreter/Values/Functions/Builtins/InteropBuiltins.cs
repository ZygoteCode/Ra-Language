using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Interop;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class InteropBuiltins
    {
        private delegate void VoidNoArg();
        private delegate void VoidIntArg(int v);
        private delegate void VoidLongArg(long v);
        private delegate void VoidPtrArg(IntPtr v);
        private delegate int IntNoArg();
        private delegate int IntIntArg(int v);
        private delegate int IntIntInt(int a, int b);
        private delegate long LongNoArg();
        private delegate long LongLongLong(long a, long b);
        private delegate IntPtr PtrNoArg();
        private delegate IntPtr PtrIntArg(int v);
        private delegate IntPtr PtrPtrArg(IntPtr v);
        private delegate IntPtr PtrPtrPtr(IntPtr a, IntPtr b);
        private delegate double DoubleDoubleDouble(double a, double b);
        private delegate double DoubleNoArg();
        private delegate double DoubleDoubleArg(double v);

        public static void Register()
        {
            BuiltInRegistry.Register("native_load", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_load", args, 1, ctx, p1, p2, out var err)) return err;
                try
                {
                    var path = AsString(args[0]);
                    var handle = NativeLibrary.Load(path);
                    return Ok(new NativeHandleValue(handle, NativeHandleKind.Library, 0, path, false), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"native_load: {ex.Message}"); }
            });
            BuiltInRegistry.Register("native_unload", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_unload", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_unload: requires native handle");
                try { NativeLibrary.Free(nh.Handle); nh.MarkDisposed(); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"native_unload: {ex.Message}"); }
            });
            BuiltInRegistry.Register("native_symbol", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_symbol", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_symbol: first arg must be a library handle");
                try
                {
                    var name = AsString(args[1]);
                    if (!NativeLibrary.TryGetExport(nh.Handle, name, out var sym))
                        return OkNull(ctx, p1, p2);
                    return Ok(new NativeHandleValue(sym, NativeHandleKind.Symbol, 0, name, false), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"native_symbol: {ex.Message}"); }
            });
            BuiltInRegistry.Register("native_invoke", (ctx, args, p1, p2) =>
            {
                if (!ExpectMinArgs("native_invoke", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_invoke: first arg must be a native symbol/handle");
                var sig = AsString(args[1]);
                var callArgs = args.GetRange(2, args.Count - 2);
                try { return Ok(InvokeWithSignature(nh.Handle, sig, callArgs), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"native_invoke[{sig}]: {ex.Message}"); }
            });
            BuiltInRegistry.Register("native_alloc", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_alloc", args, 1, ctx, p1, p2, out var err)) return err;
                long size = AsLong(args[0]);
                if (size < 0) return Fail(ctx, p1, p2, "native_alloc: size must be non-negative");
                try
                {
                    var ptr = Marshal.AllocHGlobal((nint)size);
                    unsafe { Buffer.MemoryCopy((void*)IntPtr.Zero, (void*)ptr, 0, 0); }
                    return Ok(new NativeHandleValue(ptr, NativeHandleKind.Memory, size, "alloc", true), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"native_alloc: {ex.Message}"); }
            });
            BuiltInRegistry.Register("native_zero_alloc", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_zero_alloc", args, 1, ctx, p1, p2, out var err)) return err;
                long size = AsLong(args[0]);
                if (size < 0) return Fail(ctx, p1, p2, "native_zero_alloc: size must be non-negative");
                try
                {
                    var ptr = Marshal.AllocHGlobal((nint)size);
                    unsafe
                    {
                        byte* p = (byte*)ptr;
                        for (long i = 0; i < size; i++) p[i] = 0;
                    }
                    return Ok(new NativeHandleValue(ptr, NativeHandleKind.Memory, size, "zero_alloc", true), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"native_zero_alloc: {ex.Message}"); }
            });
            BuiltInRegistry.Register("native_free", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_free", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_free: requires native handle");
                try { if (nh.OwnsMemory) Marshal.FreeHGlobal(nh.Handle); nh.MarkDisposed(); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"native_free: {ex.Message}"); }
            });
            BuiltInRegistry.Register("native_size", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_size", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_size: requires native handle");
                return Ok(new LongValue(nh.ByteSize), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_address", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_address", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_address: requires native handle");
                return Ok(new LongValue(nh.Handle.ToInt64()), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_is_null", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_is_null", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_is_null: requires native handle");
                return Ok(MakeBool(nh.Handle == IntPtr.Zero || nh.IsDisposed), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_offset", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_offset", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_offset: requires native handle");
                long off = AsLong(args[1]);
                return Ok(new NativeHandleValue(nh.Handle + (nint)off, NativeHandleKind.Pointer, Math.Max(0, nh.ByteSize - off), nh.Description, false), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_null_ptr", (ctx, args, p1, p2) =>
                Ok(new NativeHandleValue(IntPtr.Zero, NativeHandleKind.Pointer, 0, "null", false), ctx, p1, p2));
            BuiltInRegistry.Register("native_ptr_eq", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_ptr_eq", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue a || args[1] is not NativeHandleValue b) return Fail(ctx, p1, p2, "native_ptr_eq: requires two native handles");
                return Ok(MakeBool(a.Handle == b.Handle), ctx, p1, p2);
            });

            RegisterRead("native_read_u8", 1, (h, o) => { unsafe { return new IntegerValue(*((byte*)h + o)); } });
            RegisterRead("native_read_i8", 1, (h, o) => { unsafe { return new IntegerValue(*(sbyte*)((byte*)h + o)); } });
            RegisterRead("native_read_u16", 2, (h, o) => { unsafe { return new IntegerValue(*(ushort*)((byte*)h + o)); } });
            RegisterRead("native_read_i16", 2, (h, o) => { unsafe { return new IntegerValue(*(short*)((byte*)h + o)); } });
            RegisterRead("native_read_u32", 4, (h, o) => { unsafe { return new UnsignedIntegerValue(*(uint*)((byte*)h + o)); } });
            RegisterRead("native_read_i32", 4, (h, o) => { unsafe { return new IntegerValue(*(int*)((byte*)h + o)); } });
            RegisterRead("native_read_u64", 8, (h, o) => { unsafe { return new UnsignedLongValue(*(ulong*)((byte*)h + o)); } });
            RegisterRead("native_read_i64", 8, (h, o) => { unsafe { return new LongValue(*(long*)((byte*)h + o)); } });
            RegisterRead("native_read_f32", 4, (h, o) => { unsafe { return new FloatValue(*(float*)((byte*)h + o)); } });
            RegisterRead("native_read_f64", 8, (h, o) => { unsafe { return new DoubleValue(*(double*)((byte*)h + o)); } });
            RegisterRead("native_read_ptr", 8, (h, o) => { unsafe { return new NativeHandleValue(*(IntPtr*)((byte*)h + o), NativeHandleKind.Pointer); } });

            RegisterWrite("native_write_u8", (h, o, v) => { unsafe { *((byte*)h + o) = (byte)(AsLong(v) & 0xff); } });
            RegisterWrite("native_write_i8", (h, o, v) => { unsafe { *(sbyte*)((byte*)h + o) = (sbyte)AsLong(v); } });
            RegisterWrite("native_write_u16", (h, o, v) => { unsafe { *(ushort*)((byte*)h + o) = (ushort)AsLong(v); } });
            RegisterWrite("native_write_i16", (h, o, v) => { unsafe { *(short*)((byte*)h + o) = (short)AsLong(v); } });
            RegisterWrite("native_write_u32", (h, o, v) => { unsafe { *(uint*)((byte*)h + o) = (uint)AsLong(v); } });
            RegisterWrite("native_write_i32", (h, o, v) => { unsafe { *(int*)((byte*)h + o) = (int)AsLong(v); } });
            RegisterWrite("native_write_u64", (h, o, v) => { unsafe { *(ulong*)((byte*)h + o) = (ulong)AsLong(v); } });
            RegisterWrite("native_write_i64", (h, o, v) => { unsafe { *(long*)((byte*)h + o) = AsLong(v); } });
            RegisterWrite("native_write_f32", (h, o, v) => { unsafe { *(float*)((byte*)h + o) = (float)AsDouble(v); } });
            RegisterWrite("native_write_f64", (h, o, v) => { unsafe { *(double*)((byte*)h + o) = AsDouble(v); } });
            RegisterWrite("native_write_ptr", (h, o, v) =>
            {
                IntPtr p = v is NativeHandleValue nh ? nh.Handle : (IntPtr)AsLong(v);
                unsafe { *(IntPtr*)((byte*)h + o) = p; }
            });

            BuiltInRegistry.Register("native_read_bytes", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_read_bytes", args, 3, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_read_bytes: requires native handle");
                long off = AsLong(args[1]); int len = AsInt(args[2]);
                if (len < 0) return Fail(ctx, p1, p2, "native_read_bytes: len must be non-negative");
                var list = new List<RuntimeValue>(len);
                unsafe { byte* p = (byte*)nh.Handle + off; for (int i = 0; i < len; i++) list.Add(new ByteValue(p[i])); }
                return Ok(new ListValue(list), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_write_bytes", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_write_bytes", args, 3, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_write_bytes: requires native handle");
                long off = AsLong(args[1]);
                if (args[2] is not ListValue lv) return Fail(ctx, p1, p2, "native_write_bytes: bytes must be a list");
                unsafe
                {
                    byte* p = (byte*)nh.Handle + off;
                    for (int i = 0; i < lv.Elements.Count; i++) p[i] = (byte)(AsLong(lv.Elements[i]) & 0xff);
                }
                return Ok(new IntegerValue(lv.Elements.Count), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_read_cstr", (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs("native_read_cstr", args, 1, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_read_cstr: requires native handle");
                long off = args.Count == 2 ? AsLong(args[1]) : 0;
                try
                {
                    var s = Marshal.PtrToStringUTF8(nh.Handle + (nint)off);
                    return Ok(new StringValue(s ?? ""), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"native_read_cstr: {ex.Message}"); }
            });
            BuiltInRegistry.Register("native_write_cstr", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_write_cstr", args, 3, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_write_cstr: requires native handle");
                long off = AsLong(args[1]); var s = AsString(args[2]);
                var bytes = Encoding.UTF8.GetBytes(s);
                unsafe
                {
                    byte* p = (byte*)nh.Handle + off;
                    for (int i = 0; i < bytes.Length; i++) p[i] = bytes[i];
                    p[bytes.Length] = 0;
                }
                return Ok(new IntegerValue(bytes.Length + 1), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_memcpy", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_memcpy", args, 3, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue dst || args[1] is not NativeHandleValue src) return Fail(ctx, p1, p2, "native_memcpy: requires native handles");
                long n = AsLong(args[2]);
                unsafe { Buffer.MemoryCopy((void*)src.Handle, (void*)dst.Handle, n, n); }
                return Ok(new LongValue(n), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_memset", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_memset", args, 3, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue dst) return Fail(ctx, p1, p2, "native_memset: requires native handle");
                byte val = (byte)(AsLong(args[1]) & 0xff);
                long n = AsLong(args[2]);
                unsafe { byte* p = (byte*)dst.Handle; for (long i = 0; i < n; i++) p[i] = val; }
                return Ok(new LongValue(n), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_memzero", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_memzero", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue dst) return Fail(ctx, p1, p2, "native_memzero: requires native handle");
                long n = AsLong(args[1]);
                unsafe { byte* p = (byte*)dst.Handle; for (long i = 0; i < n; i++) p[i] = 0; }
                return Ok(new LongValue(n), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_sizeof", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_sizeof", args, 1, ctx, p1, p2, out var err)) return err;
                var t = AsString(args[0]).ToLowerInvariant();
                int s = t switch
                {
                    "u8" or "i8" or "byte" or "bool" => 1,
                    "u16" or "i16" or "short" => 2,
                    "u32" or "i32" or "int" or "f32" or "float" => 4,
                    "u64" or "i64" or "long" or "f64" or "double" => 8,
                    "ptr" or "pointer" or "void*" => IntPtr.Size,
                    _ => -1
                };
                return s < 0 ? OkNull(ctx, p1, p2) : Ok(new IntegerValue(s), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_pointer_size", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(IntPtr.Size), ctx, p1, p2));

            BuiltInRegistry.Register("as_callback", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("as_callback", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not BaseFunctionValue bfv) return Fail(ctx, p1, p2, "as_callback: first arg must be a function");
                if (args[1] is not StringValue sv) return Fail(ctx, p1, p2, "as_callback: second arg must be a signature string");
                var (handle, e) = CallbackRegistry.Create(bfv, sv.Value, ctx);
                if (e != null) return Fail(ctx, p1, p2, e);
                return Ok(handle, ctx, p1, p2);
            });

            BuiltInRegistry.Register("release_callback", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("release_callback", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "release_callback: requires a native handle");
                bool released = CallbackRegistry.Release(nh.Handle);
                return Ok(MakeBool(released), ctx, p1, p2);
            });

            BuiltInRegistry.Register("callback_count", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(CallbackRegistry.Count), ctx, p1, p2));

            BuiltInRegistry.Register("trampoline_enabled", (ctx, args, p1, p2) =>
                Ok(MakeBool(TrampolineGen.IsEnabled), ctx, p1, p2));

            BuiltInRegistry.Register("trampoline_last_error", (ctx, args, p1, p2) =>
                Ok(new StringValue(TrampolineGen.LastError ?? ""), ctx, p1, p2));

            BuiltInRegistry.Register("trampoline_detect_error", (ctx, args, p1, p2) =>
                Ok(new StringValue(RuntimeFeature.DetectError ?? ""), ctx, p1, p2));

            BuiltInRegistry.Register("native_free_cotask", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_free_cotask", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_free_cotask: requires native handle");
                Marshal.FreeCoTaskMem(nh.Handle); nh.MarkDisposed();
                return Ok(MakeBool(true), ctx, p1, p2);
            });

            BuiltInRegistry.Register("struct_to_buffer", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("struct_to_buffer", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not RaLanguage.Interpreter.Values.Structs.StructInstanceValue inst)
                    return Fail(ctx, p1, p2, "struct_to_buffer: argument must be a struct instance");
                var (layout, lerr) = NativeStructLayout.Build(inst.Definition, ctx?.SymbolTable);
                if (lerr != null) return Fail(ctx, p1, p2, lerr);
                var buf = layout!.SerializeInstance(inst);
                return Ok(new NativeHandleValue(buf, NativeHandleKind.Memory, layout.Size, "struct:" + inst.Definition.StructName, true), ctx, p1, p2);
            });

            BuiltInRegistry.Register("struct_from_buffer", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("struct_from_buffer", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "struct_from_buffer: first arg must be a native handle");
                if (args[1] is not RaLanguage.Interpreter.Values.Structs.StructTypeValue stype) return Fail(ctx, p1, p2, "struct_from_buffer: second arg must be a struct type");
                var (layout, lerr) = NativeStructLayout.Build(stype, ctx?.SymbolTable);
                if (lerr != null) return Fail(ctx, p1, p2, lerr);
                var inst = layout!.Materialize(nh.Handle);
                return Ok(inst, ctx, p1, p2);
            });

            BuiltInRegistry.Register("struct_size", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("struct_size", args, 1, ctx, p1, p2, out var err)) return err;
                RaLanguage.Interpreter.Values.Structs.StructTypeValue? stype = args[0] switch
                {
                    RaLanguage.Interpreter.Values.Structs.StructTypeValue t => t,
                    RaLanguage.Interpreter.Values.Structs.StructInstanceValue i => i.Definition,
                    _ => null
                };
                if (stype == null) return Fail(ctx, p1, p2, "struct_size: argument must be a struct type/instance");
                var (layout, lerr) = NativeStructLayout.Build(stype, ctx?.SymbolTable);
                if (lerr != null) return Fail(ctx, p1, p2, lerr);
                return Ok(new IntegerValue(layout!.Size), ctx, p1, p2);
            });

            BuiltInRegistry.Register("struct_offset_of", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("struct_offset_of", args, 2, ctx, p1, p2, out var err)) return err;
                RaLanguage.Interpreter.Values.Structs.StructTypeValue? stype = args[0] switch
                {
                    RaLanguage.Interpreter.Values.Structs.StructTypeValue t => t,
                    RaLanguage.Interpreter.Values.Structs.StructInstanceValue i => i.Definition,
                    _ => null
                };
                if (stype == null) return Fail(ctx, p1, p2, "struct_offset_of: first arg must be a struct type/instance");
                var name = AsString(args[1]);
                var (layout, lerr) = NativeStructLayout.Build(stype, ctx?.SymbolTable);
                if (lerr != null) return Fail(ctx, p1, p2, lerr);
                foreach (var f in layout!.Fields)
                    if (f.Name == name) return Ok(new IntegerValue(f.Offset), ctx, p1, p2);
                return Ok(new IntegerValue(-1), ctx, p1, p2);
            });

            BuiltInRegistry.Register("native_free_local", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_free_local", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "native_free_local: requires native handle");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var k32 = NativeLibraryResolver.Load("kernel32", null, out _, out _);
                        if (NativeLibraryResolver.TryGetExport(k32, "LocalFree", true, out var lf))
                        {
                            Marshal.GetDelegateForFunctionPointer<NativeInvoker.Fn_P1>(lf)(nh.Handle);
                            nh.MarkDisposed();
                            return Ok(MakeBool(true), ctx, p1, p2);
                        }
                    }
                    catch (Exception ex) { return Fail(ctx, p1, p2, $"native_free_local: {ex.Message}"); }
                }
                return Fail(ctx, p1, p2, "native_free_local: only supported on Windows");
            });

            // ===== Tier 4: ownership / lifetime =====
            BuiltInRegistry.Register("handle_info", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("handle_info", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "handle_info: requires native handle");
                var pairs = new List<(RuntimeValue, RuntimeValue)>
                {
                    (new StringValue("id"), new LongValue(nh.Id)),
                    (new StringValue("kind"), new StringValue(nh.Kind.ToString())),
                    (new StringValue("ownership"), new StringValue(nh.Ownership.ToString())),
                    (new StringValue("address"), new LongValue(nh.Handle.ToInt64())),
                    (new StringValue("size"), new LongValue(nh.ByteSize)),
                    (new StringValue("disposed"), BooleanValue.Of(nh.IsDisposed)),
                    (new StringValue("generation"), new IntegerValue(nh.Generation)),
                    (new StringValue("description"), new StringValue(nh.Description ?? "")),
                };
                return Ok(new MapValue(pairs), ctx, p1, p2);
            });
            BuiltInRegistry.Register("handle_alive_count", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(NativeHandleValue.AliveCount), ctx, p1, p2));
            BuiltInRegistry.Register("handle_double_free_count", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(NativeHandleValue.DoubleFreesDetected), ctx, p1, p2));

            // ===== Tier 4: pin / unpin (zero-copy) =====
            BuiltInRegistry.Register("pin", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("pin", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not ListValue lv) return Fail(ctx, p1, p2, "pin: argument must be a list<byte>");
                var bytes = new byte[lv.Elements.Count];
                for (int i = 0; i < lv.Elements.Count; i++) bytes[i] = (byte)(AsInt(lv.Elements[i]) & 0xff);
                var gch = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                var addr = gch.AddrOfPinnedObject();
                return Ok(new NativeHandleValue(addr, NativeHandleKind.Pinned, bytes.Length, "pinned-bytes", NativeHandleOwnership.Owned, gch), ctx, p1, p2);
            });
            BuiltInRegistry.Register("unpin", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("unpin", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "unpin: requires native handle");
                if (nh.IsDisposed) return Fail(ctx, p1, p2, "unpin: handle already disposed");
                nh.MarkDisposed();
                return Ok(MakeBool(true), ctx, p1, p2);
            });

            // ===== Tier 5: native_reload + cache invalidation =====
            BuiltInRegistry.Register("native_reload", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("native_reload", args, 1, ctx, p1, p2, out var err)) return err;
                var name = AsString(args[0]);
                var evicted = NativeLibraryResolver.Reload(name);
                return Ok(MakeBool(evicted), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_reload_all", (ctx, args, p1, p2) =>
            {
                NativeLibraryResolver.ClearCache();
                return Ok(MakeBool(true), ctx, p1, p2);
            });
            BuiltInRegistry.Register("native_libraries", (ctx, args, p1, p2) =>
            {
                var list = new List<RuntimeValue>();
                foreach (var k in NativeLibraryResolver.Snapshot().Keys)
                {
                    int idx = k.IndexOf('|');
                    var libName = idx > 0 ? k.Substring(0, idx) : k;
                    list.Add(new StringValue(libName));
                }
                return Ok(new ListValue(list), ctx, p1, p2);
            });

            // ===== Tier 5: ABI canary diagnostic =====
            BuiltInRegistry.Register("abi_canary_count", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(AbiCanary.Detected), ctx, p1, p2));

            // ===== Tier 3: callback enhancements =====
            BuiltInRegistry.Register("as_callback_with", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("as_callback_with", args, 3, ctx, p1, p2, out var err)) return err;
                if (args[0] is not BaseFunctionValue bfv) return Fail(ctx, p1, p2, "as_callback_with: first arg must be a function");
                if (args[1] is not StringValue sv) return Fail(ctx, p1, p2, "as_callback_with: second arg must be a signature string");
                var (handle, e) = CallbackRegistry.CreateWithContext(bfv, sv.Value, args[2], ctx);
                if (e != null) return Fail(ctx, p1, p2, e);
                return Ok(handle, ctx, p1, p2);
            });

            // ===== Tier 6: COM bridge — IUnknown vtable navigation =====
            // COM object layout:
            //   *obj         -> vtable*
            //   vtable[0..N] -> function pointers (slot 0=QueryInterface, 1=AddRef, 2=Release)
            BuiltInRegistry.Register("com_vtable", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("com_vtable", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "com_vtable: requires native handle");
                unsafe
                {
                    IntPtr* obj = (IntPtr*)nh.Handle;
                    if (obj == null) return Fail(ctx, p1, p2, "com_vtable: object is null");
                    IntPtr vtab = *obj;
                    return Ok(new NativeHandleValue(vtab, NativeHandleKind.Pointer, 0, "vtable", false), ctx, p1, p2);
                }
            });
            BuiltInRegistry.Register("com_vtable_slot", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("com_vtable_slot", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "com_vtable_slot: requires native handle");
                int slot = AsInt(args[1]);
                unsafe
                {
                    IntPtr* obj = (IntPtr*)nh.Handle;
                    if (obj == null) return Fail(ctx, p1, p2, "com_vtable_slot: object is null");
                    IntPtr* vtab = (IntPtr*)*obj;
                    return Ok(new NativeHandleValue(vtab[slot], NativeHandleKind.Symbol, 0, "vslot[" + slot + "]", false), ctx, p1, p2);
                }
            });
            BuiltInRegistry.Register("com_add_ref", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("com_add_ref", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "com_add_ref: requires native handle");
                unsafe
                {
                    IntPtr* obj = (IntPtr*)nh.Handle;
                    IntPtr* vtab = (IntPtr*)*obj;
                    IntPtr addRef = vtab[1];
                    var d = Marshal.GetDelegateForFunctionPointer<NativeInvoker.Fn_I32_1>(addRef);
                    return Ok(new IntegerValue(d(nh.Handle)), ctx, p1, p2);
                }
            });
            BuiltInRegistry.Register("com_release", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("com_release", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "com_release: requires native handle");
                unsafe
                {
                    IntPtr* obj = (IntPtr*)nh.Handle;
                    IntPtr* vtab = (IntPtr*)*obj;
                    IntPtr release = vtab[2];
                    var d = Marshal.GetDelegateForFunctionPointer<NativeInvoker.Fn_I32_1>(release);
                    int refCount = d(nh.Handle);
                    if (refCount == 0) nh.MarkDisposed();
                    return Ok(new IntegerValue(refCount), ctx, p1, p2);
                }
            });
            // QueryInterface(this, REFIID riid, void** ppvObject) -> HRESULT
            // GUID encoded as 16-byte buffer. Allocate IID + out-ptr, call slot 0, materialize handle.
            BuiltInRegistry.Register("com_query_interface", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("com_query_interface", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, "com_query_interface: requires native handle");
                var guidStr = AsString(args[1]);
                if (!Guid.TryParse(guidStr, out var guid)) return Fail(ctx, p1, p2, $"com_query_interface: invalid GUID '{guidStr}'");
                var iidBytes = guid.ToByteArray();
                IntPtr iidBuf = Marshal.AllocHGlobal(16);
                IntPtr outPtr = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.Copy(iidBytes, 0, iidBuf, 16);
                    IntPtr qi;
                    int hr;
                    IntPtr newObj;
                    unsafe
                    {
                        *(IntPtr*)outPtr = IntPtr.Zero;
                        IntPtr* obj = (IntPtr*)nh.Handle;
                        IntPtr* vtab = (IntPtr*)*obj;
                        qi = vtab[0];
                        var d = Marshal.GetDelegateForFunctionPointer<NativeInvoker.Fn_I32_3>(qi);
                        hr = d(nh.Handle, iidBuf, outPtr);
                        newObj = *(IntPtr*)outPtr;
                    }
                    if (hr != 0)
                    {
                        return Ok(new MapValue(new List<(RuntimeValue, RuntimeValue)>
                        {
                            (new StringValue("hr"), new IntegerValue(hr)),
                            (new StringValue("ptr"), NullValue.Null)
                        }), ctx, p1, p2);
                    }
                    return Ok(new MapValue(new List<(RuntimeValue, RuntimeValue)>
                    {
                        (new StringValue("hr"), new IntegerValue(0)),
                        (new StringValue("ptr"), new NativeHandleValue(newObj, NativeHandleKind.Pointer, 0, "com:" + guidStr, false))
                    }), ctx, p1, p2);
                }
                finally
                {
                    Marshal.FreeHGlobal(iidBuf);
                    Marshal.FreeHGlobal(outPtr);
                }
            });

            // ===== Tier 8: native_async =====
            // Wraps a native call invocation in a TaskValue scheduled on the async pool.
            // Usage: var t = native_async(my_fn, [arg1, arg2]);  await t;
            BuiltInRegistry.Register("native_async", (ctx, args, p1, p2) =>
            {
                if (!ExpectMinArgs("native_async", args, 1, ctx, p1, p2, out var err)) return err;
                if (args[0] is not BaseFunctionValue bfv) return Fail(ctx, p1, p2, "native_async: first arg must be a function");
                var callArgs = new List<RuntimeValue>();
                if (args.Count == 2 && args[1] is ListValue lv) callArgs.AddRange(lv.Elements);
                else for (int i = 1; i < args.Count; i++) callArgs.Add(args[i]);

                var task = RaLanguage.Interpreter.Runtime.Async.AsyncScheduler.Schedule(
                    "native_async:" + bfv.Name,
                    ctx?.AsyncCtx,
                    childCtx =>
                    {
                        try
                        {
                            var r = bfv.Execute(callArgs);
                            if (r.Error != null) return ((RuntimeValue?)null, (Error?)r.Error);
                            return ((RuntimeValue?)(r.Value ?? NullValue.Null), (Error?)null);
                        }
                        catch (Exception ex)
                        {
                            return ((RuntimeValue?)null, (Error?)new RuntimeError(p1, p2, $"native_async failed: {ex.Message}", ctx));
                        }
                    });
                return Ok(new RaLanguage.Interpreter.Values.Async.TaskValue(task), ctx, p1, p2);
            });

            // ===== Tier 3: scoped callback (RAII wrapper) =====
            // with_callback(fn, "sig", body): builds callback, runs body(callback_handle), releases.
            BuiltInRegistry.Register("with_callback", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("with_callback", args, 3, ctx, p1, p2, out var err)) return err;
                if (args[0] is not BaseFunctionValue cbFn) return Fail(ctx, p1, p2, "with_callback: first arg must be a function (the callback)");
                if (args[1] is not StringValue sigStr) return Fail(ctx, p1, p2, "with_callback: second arg must be a signature string");
                if (args[2] is not BaseFunctionValue bodyFn) return Fail(ctx, p1, p2, "with_callback: third arg must be a body function (handle -> result)");
                var (handle, e) = CallbackRegistry.Create(cbFn, sigStr.Value, ctx);
                if (e != null) return Fail(ctx, p1, p2, e);
                try
                {
                    var r = bodyFn.Execute(new List<RuntimeValue> { handle });
                    if (r.Error != null) return new RuntimeResult().Failure(r.Error);
                    return Ok(r.Value ?? NullValue.Null, ctx, p1, p2);
                }
                finally
                {
                    CallbackRegistry.Release(handle.Handle);
                }
            });
        }

        private static void RegisterRead(string name, int width, Func<IntPtr, long, RuntimeValue> reader)
        {
            BuiltInRegistry.Register(name, (ctx, args, p1, p2) =>
            {
                if (!ExpectRangeArgs(name, args, 1, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, $"{name}: requires native handle");
                long off = args.Count == 2 ? AsLong(args[1]) : 0;
                try { return Ok(reader(nh.Handle, off), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"{name}: {ex.Message}"); }
            });
        }

        private static void RegisterWrite(string name, Action<IntPtr, long, RuntimeValue> writer)
        {
            BuiltInRegistry.Register(name, (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs(name, args, 3, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeHandleValue nh) return Fail(ctx, p1, p2, $"{name}: requires native handle");
                long off = AsLong(args[1]);
                try { writer(nh.Handle, off, args[2]); return new RuntimeResult().Success(args[2].SetContext(ctx).SetPos(p1, p2)); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"{name}: {ex.Message}"); }
            });
        }

        private static RuntimeValue InvokeWithSignature(IntPtr sym, string sig, List<RuntimeValue> args)
        {
            switch (sig)
            {
                case "void()":
                    Marshal.GetDelegateForFunctionPointer<VoidNoArg>(sym)();
                    return NullValue.Null;
                case "void(int)":
                    Marshal.GetDelegateForFunctionPointer<VoidIntArg>(sym)((int)AsLong(args[0]));
                    return NullValue.Null;
                case "void(long)":
                    Marshal.GetDelegateForFunctionPointer<VoidLongArg>(sym)(AsLong(args[0]));
                    return NullValue.Null;
                case "void(ptr)":
                    Marshal.GetDelegateForFunctionPointer<VoidPtrArg>(sym)(args[0] is NativeHandleValue nh1 ? nh1.Handle : (IntPtr)AsLong(args[0]));
                    return NullValue.Null;
                case "int()":
                    return new IntegerValue(Marshal.GetDelegateForFunctionPointer<IntNoArg>(sym)());
                case "int(int)":
                    return new IntegerValue(Marshal.GetDelegateForFunctionPointer<IntIntArg>(sym)((int)AsLong(args[0])));
                case "int(int,int)":
                    return new IntegerValue(Marshal.GetDelegateForFunctionPointer<IntIntInt>(sym)((int)AsLong(args[0]), (int)AsLong(args[1])));
                case "long()":
                    return new LongValue(Marshal.GetDelegateForFunctionPointer<LongNoArg>(sym)());
                case "long(long,long)":
                    return new LongValue(Marshal.GetDelegateForFunctionPointer<LongLongLong>(sym)(AsLong(args[0]), AsLong(args[1])));
                case "ptr()":
                    return new NativeHandleValue(Marshal.GetDelegateForFunctionPointer<PtrNoArg>(sym)(), NativeHandleKind.Pointer);
                case "ptr(int)":
                    return new NativeHandleValue(Marshal.GetDelegateForFunctionPointer<PtrIntArg>(sym)((int)AsLong(args[0])), NativeHandleKind.Pointer);
                case "ptr(ptr)":
                {
                    IntPtr a = args[0] is NativeHandleValue nh ? nh.Handle : (IntPtr)AsLong(args[0]);
                    return new NativeHandleValue(Marshal.GetDelegateForFunctionPointer<PtrPtrArg>(sym)(a), NativeHandleKind.Pointer);
                }
                case "ptr(ptr,ptr)":
                {
                    IntPtr a = args[0] is NativeHandleValue n1 ? n1.Handle : (IntPtr)AsLong(args[0]);
                    IntPtr b = args[1] is NativeHandleValue n2 ? n2.Handle : (IntPtr)AsLong(args[1]);
                    return new NativeHandleValue(Marshal.GetDelegateForFunctionPointer<PtrPtrPtr>(sym)(a, b), NativeHandleKind.Pointer);
                }
                case "double()":
                    return new DoubleValue(Marshal.GetDelegateForFunctionPointer<DoubleNoArg>(sym)());
                case "double(double)":
                    return new DoubleValue(Marshal.GetDelegateForFunctionPointer<DoubleDoubleArg>(sym)(AsDouble(args[0])));
                case "double(double,double)":
                    return new DoubleValue(Marshal.GetDelegateForFunctionPointer<DoubleDoubleDouble>(sym)(AsDouble(args[0]), AsDouble(args[1])));
                default:
                    throw new RuntimeBuiltinException($"unsupported signature '{sig}'. Supported: void(), void(int|long|ptr), int(), int(int), int(int,int), long(), long(long,long), ptr()/ptr(int)/ptr(ptr)/ptr(ptr,ptr), double()/double(double)/double(double,double)");
            }
        }
    }
}

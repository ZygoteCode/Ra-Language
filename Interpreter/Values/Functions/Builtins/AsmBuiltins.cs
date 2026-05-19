using System;
using System.Collections.Generic;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Asm;
using RaLanguage.Interpreter.Runtime.Interop;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    /// <summary>
    /// Built-in functions that expose the asm subsystem to Ra programs.
    ///
    /// Surface API:
    ///   asm_supported() -> bool
    ///   asm_arch() -> string
    ///   asm_assemble(source) -> list&lt;byte&gt;     (assemble only, no exec memory)
    ///   asm_compile(signature, source) -> function   (callable, full marshal)
    ///   asm_compile_bytes(bytes_list) -> native      (executable region)
    ///   asm_invoke(fn_or_handle, signature, args...) -> result
    ///   asm_compose(parts...) -> string              (concatenate snippets)
    ///   asm_prepend(prelude, body) -> string         (prelude + "\n" + body)
    ///   asm_region_count() -> int
    ///   asm_region_bytes() -> long
    ///   asm_clear_cache() -> bool
    /// </summary>
    public static class AsmBuiltins
    {
        public static void Register()
        {
            BuiltInRegistry.Register("asm_supported", (ctx, args, p1, p2) =>
                Ok(new BooleanValue(AsmExecutor.IsSupported), ctx, p1, p2));

            BuiltInRegistry.Register("asm_arch", (ctx, args, p1, p2) =>
                Ok(new StringValue(AsmExecutor.Architecture), ctx, p1, p2));

            BuiltInRegistry.Register("asm_assemble", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_assemble", args, 1, ctx, p1, p2, out var err)) return err;
                var src = AsString(args[0]);
                try
                {
                    var bytes = AsmRegionRegistry.AssembleOnly(src);
                    var list = new List<RuntimeValue>(bytes.Length);
                    for (int i = 0; i < bytes.Length; i++) list.Add(new ByteValue(bytes[i]));
                    return Ok(new ListValue(list), ctx, p1, p2);
                }
                catch (AsmAssembleException ax) { return Fail(ctx, p1, p2, $"asm_assemble: {ax.Message}"); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"asm_assemble: {ex.Message}"); }
            });

            BuiltInRegistry.Register("asm_compile", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_compile", args, 2, ctx, p1, p2, out var err)) return err;
                if (!AsmExecutor.IsSupported)
                    return Fail(ctx, p1, p2, "asm_compile: x64-only platform required");
                var signature = AsString(args[0]);
                var src = AsString(args[1]);
                try
                {
                    IntPtr address = AsmRegionRegistry.GetOrCompile(src);
                    var fn = AsmFunctionFactory.Create("<asm-fn>", address, signature);
                    return Ok(fn, ctx, p1, p2);
                }
                catch (AsmAssembleException ax) { return Fail(ctx, p1, p2, $"asm_compile: {ax.Message}"); }
                catch (ArgumentException axe) { return Fail(ctx, p1, p2, $"asm_compile: {axe.Message}"); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"asm_compile: {ex.Message}"); }
            });

            BuiltInRegistry.Register("asm_compile_bytes", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_compile_bytes", args, 1, ctx, p1, p2, out var err)) return err;
                if (!AsmExecutor.IsSupported)
                    return Fail(ctx, p1, p2, "asm_compile_bytes: x64-only platform required");
                if (args[0] is not ListValue lv)
                    return Fail(ctx, p1, p2, "asm_compile_bytes: argument must be a list of bytes");
                var bytes = new byte[lv.Elements.Count];
                for (int i = 0; i < lv.Elements.Count; i++) bytes[i] = (byte)(AsLong(lv.Elements[i]) & 0xff);
                try
                {
                    var region = AsmExecutor.Allocate(bytes);
                    return Ok(new NativeHandleValue(region.Address, NativeHandleKind.Symbol, bytes.Length, "asm-bytes", false), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"asm_compile_bytes: {ex.Message}"); }
            });

            BuiltInRegistry.Register("asm_invoke", (ctx, args, p1, p2) =>
            {
                if (!ExpectMinArgs("asm_invoke", args, 2, ctx, p1, p2, out var err)) return err;

                IntPtr address;
                if (args[0] is NativeHandleValue nh) address = nh.Handle;
                else if (args[0] is NativeFunctionValue nf) address = nf.Binding.FunctionPointer;
                else return Fail(ctx, p1, p2, "asm_invoke: first arg must be an asm function or native handle");

                var signature = AsString(args[1]);
                var callArgs = args.GetRange(2, args.Count - 2);
                try
                {
                    var fn = AsmFunctionFactory.Create("<asm-invoke>", address, signature);
                    var res = fn.Execute(callArgs);
                    if (res.Error != null) return new RuntimeResult().Failure(res.Error);
                    return Ok(res.Value ?? new NullValue(), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"asm_invoke: {ex.Message}"); }
            });

            BuiltInRegistry.Register("asm_compose", (ctx, args, p1, p2) =>
            {
                var sb = new StringBuilder();
                for (int i = 0; i < args.Count; i++)
                {
                    if (i > 0 && !EndsWithNewline(sb)) sb.Append('\n');
                    sb.Append(AsString(args[i]));
                }
                return Ok(new StringValue(sb.ToString()), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_prepend", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_prepend", args, 2, ctx, p1, p2, out var err)) return err;
                string prelude = AsString(args[0]);
                string body = AsString(args[1]);
                string sep = prelude.EndsWith("\n") ? "" : "\n";
                return Ok(new StringValue(prelude + sep + body), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_append", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_append", args, 2, ctx, p1, p2, out var err)) return err;
                string body = AsString(args[0]);
                string tail = AsString(args[1]);
                string sep = body.EndsWith("\n") ? "" : "\n";
                return Ok(new StringValue(body + sep + tail), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_region_count", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(AsmRegionRegistry.LiveRegionCount), ctx, p1, p2));

            BuiltInRegistry.Register("asm_region_bytes", (ctx, args, p1, p2) =>
                Ok(new LongValue(AsmExecutor.TotalAllocatedBytes), ctx, p1, p2));

            BuiltInRegistry.Register("asm_clear_cache", (ctx, args, p1, p2) =>
            {
                AsmRegionRegistry.Clear();
                return Ok(new BooleanValue(true), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_disasm", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_disasm", args, 1, ctx, p1, p2, out var err)) return err;
                byte[] bytes;
                if (args[0] is ListValue lv)
                {
                    bytes = new byte[lv.Elements.Count];
                    for (int i = 0; i < lv.Elements.Count; i++) bytes[i] = (byte)(AsLong(lv.Elements[i]) & 0xff);
                }
                else if (args[0] is StringValue sv)
                {
                    try { bytes = AsmRegionRegistry.AssembleOnly(sv.Value); }
                    catch (AsmAssembleException ax) { return Fail(ctx, p1, p2, $"asm_disasm: {ax.Message}"); }
                }
                else return Fail(ctx, p1, p2, "asm_disasm: argument must be source string or byte list");

                var lines = X64Disassembler.Disassemble(bytes);
                var listOut = new List<RuntimeValue>(lines.Count);
                foreach (var l in lines) listOut.Add(new StringValue(l));
                return Ok(new ListValue(listOut), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_explain", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_explain", args, 1, ctx, p1, p2, out var err)) return err;
                var mnem = AsString(args[0]);
                return Ok(new StringValue(AsmExplain.Explain(mnem)), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_analyze", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_analyze", args, 1, ctx, p1, p2, out var err)) return err;
                var src = AsString(args[0]);
                var findings = new AsmStaticAnalyzer().Analyze(src);
                var list = new List<RuntimeValue>(findings.Count);
                foreach (var f in findings)
                {
                    list.Add(new MapValue(new List<(RuntimeValue, RuntimeValue)>
                    {
                        (new StringValue("severity"), new StringValue(f.Severity)),
                        (new StringValue("line"), new IntegerValue(f.LineNumber)),
                        (new StringValue("message"), new StringValue(f.Message)),
                        (new StringValue("raw"), new StringValue(f.RawLine ?? "")),
                    }));
                }
                return Ok(new ListValue(list), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_bench", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_bench", args, 2, ctx, p1, p2, out var err)) return err;
                if (args[0] is not NativeFunctionValue nfv) return Fail(ctx, p1, p2, "asm_bench: first arg must be an asm function");
                long iters = AsLong(args[1]);
                if (iters <= 0) iters = 1;
                var emptyArgs = new List<RuntimeValue>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AsmSehGuard.RunVoid(() =>
                {
                    for (long i = 0; i < iters; i++) nfv.Execute(emptyArgs);
                });
                sw.Stop();

                double ns = sw.Elapsed.TotalNanoseconds;
                var pairs = new List<(RuntimeValue, RuntimeValue)>
                {
                    (new StringValue("iterations"), new LongValue(iters)),
                    (new StringValue("total_ns"), new DoubleValue(ns)),
                    (new StringValue("avg_ns"), new DoubleValue(ns / iters)),
                    (new StringValue("ticks"), new LongValue(sw.ElapsedTicks)),
                };
                return Ok(new MapValue(pairs), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_pool_stats", (ctx, args, p1, p2) =>
            {
                var pairs = new List<(RuntimeValue, RuntimeValue)>
                {
                    (new StringValue("interned"), new IntegerValue(AsmCodePool.InternedCount)),
                    (new StringValue("live_bytes"), new LongValue(AsmCodePool.LiveBytes)),
                    (new StringValue("max_bytes"), new LongValue(AsmCodePool.MaxTotalBytes)),
                };
                return Ok(new MapValue(pairs), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_pool_limit", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_pool_limit", args, 1, ctx, p1, p2, out var err)) return err;
                AsmCodePool.MaxTotalBytes = AsLong(args[0]);
                return Ok(new BooleanValue(true), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_compile_sandboxed", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_compile_sandboxed", args, 2, ctx, p1, p2, out var err)) return err;
                if (!AsmExecutor.IsSupported) return Fail(ctx, p1, p2, "asm_compile_sandboxed: x64-only platform required");
                var signature = AsString(args[0]);
                var src = AsString(args[1]);
                try
                {
                    var policy = AsmSecurityPolicy.Sandbox();
                    var bytes = X64Assembler.Assemble(src, null, policy);
                    var hash = AsmCodePool.ComputeHash(src);
                    var slot = AsmCodePool.Allocate(bytes, hash);
                    var fn = AsmFunctionFactory.Create("<asm-sandbox>", slot.Address, signature);
                    return Ok(fn, ctx, p1, p2);
                }
                catch (AsmAssembleException ax) { return Fail(ctx, p1, p2, $"asm_compile_sandboxed: {ax.Message}"); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"asm_compile_sandboxed: {ex.Message}"); }
            });

            BuiltInRegistry.Register("asm_mnemonics", (ctx, args, p1, p2) =>
            {
                var list = new List<RuntimeValue>();
                foreach (var m in RaLanguage.Interpreter.Runtime.Asm.AsmMnemonicCatalog.AllMnemonics) list.Add(new StringValue(m));
                return Ok(new ListValue(list), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_with_defines", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_with_defines", args, 3, ctx, p1, p2, out var err)) return err;
                if (args[0] is not StringValue sigSv) return Fail(ctx, p1, p2, "asm_with_defines: first arg must be signature string");
                if (args[1] is not MapValue defsMv) return Fail(ctx, p1, p2, "asm_with_defines: second arg must be a map");
                if (args[2] is not StringValue srcSv) return Fail(ctx, p1, p2, "asm_with_defines: third arg must be source string");
                var opts = new X64Preprocessor.Options();
                foreach (var kv in defsMv.Pairs)
                {
                    opts.InitialDefines[AsString(kv.Key)] = AsString(kv.Value);
                }
                try
                {
                    var bytes = X64Assembler.Assemble(srcSv.Value, opts, null);
                    var hash = AsmCodePool.ComputeHash(srcSv.Value);
                    var slot = AsmCodePool.Allocate(bytes, hash);
                    var fn = AsmFunctionFactory.Create("<asm-with-defines>", slot.Address, sigSv.Value);
                    return Ok(fn, ctx, p1, p2);
                }
                catch (AsmAssembleException ax) { return Fail(ctx, p1, p2, $"asm_with_defines: {ax.Message}"); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"asm_with_defines: {ex.Message}"); }
            });

            BuiltInRegistry.Register("asm_hash", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_hash", args, 1, ctx, p1, p2, out var err)) return err;
                return Ok(new StringValue(AsmCodePool.ComputeHash(AsString(args[0]))), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_pin", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("asm_pin", args, 1, ctx, p1, p2, out var err)) return err;
                AsmCodePool.Pin(AsString(args[0]));
                return Ok(new BooleanValue(true), ctx, p1, p2);
            });

            BuiltInRegistry.Register("asm_rdtsc_calibrate", (ctx, args, p1, p2) =>
            {
                if (!AsmExecutor.IsSupported) return Fail(ctx, p1, p2, "asm_rdtsc_calibrate: requires x64");
                try
                {
                    var src = "rdtsc\nshl rdx, 32\nor rax, rdx\nret";
                    var bytes = AsmRegionRegistry.AssembleOnly(src);
                    var hash = AsmCodePool.ComputeHash(src);
                    var slot = AsmCodePool.Allocate(bytes, hash);
                    var fn = AsmFunctionFactory.Create("<rdtsc>", slot.Address, "i64()");
                    long start = ((LongValue)fn.Execute(new List<RuntimeValue>()).Value!).Value;
                    System.Threading.Thread.Sleep(50);
                    long end = ((LongValue)fn.Execute(new List<RuntimeValue>()).Value!).Value;
                    return Ok(new MapValue(new List<(RuntimeValue, RuntimeValue)>
                    {
                        (new StringValue("start"), new LongValue(start)),
                        (new StringValue("end"), new LongValue(end)),
                        (new StringValue("delta"), new LongValue(end - start)),
                        (new StringValue("approx_hz"), new LongValue((end - start) * 20)),
                    }), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"asm_rdtsc_calibrate: {ex.Message}"); }
            });
        }

        private static bool EndsWithNewline(StringBuilder sb)
        {
            if (sb.Length == 0) return true;
            char c = sb[sb.Length - 1];
            return c == '\n';
        }
    }
}

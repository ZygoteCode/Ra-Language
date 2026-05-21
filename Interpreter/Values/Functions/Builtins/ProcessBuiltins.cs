using System.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using static RaLanguage.Interpreter.Values.Functions.Builtins.BuiltinUtils;

namespace RaLanguage.Interpreter.Values.Functions.Builtins
{
    public static class ProcessBuiltins
    {
        private static long _handleSeq;
        private static readonly ConcurrentDictionary<long, Process> _processes = new();

        public static void Register()
        {
            BuiltInRegistry.Register("process_pid", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(Environment.ProcessId), ctx, p1, p2));
            BuiltInRegistry.Register("process_name", (ctx, args, p1, p2) =>
            {
                try { return Ok(new StringValue(Process.GetCurrentProcess().ProcessName ?? ""), ctx, p1, p2); }
                catch { return OkNull(ctx, p1, p2); }
            });
            BuiltInRegistry.Register("thread_id", (ctx, args, p1, p2) =>
                Ok(new IntegerValue(Environment.CurrentManagedThreadId), ctx, p1, p2));
            BuiltInRegistry.Register("thread_count", (ctx, args, p1, p2) =>
            {
                try { return Ok(new IntegerValue(Process.GetCurrentProcess().Threads.Count), ctx, p1, p2); }
                catch { return Ok(new IntegerValue(0), ctx, p1, p2); }
            });
            BuiltInRegistry.Register("process_exists", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("process_exists", args, 1, ctx, p1, p2, out var err)) return err;
                try { Process.GetProcessById(AsInt(args[0])); return Ok(MakeBool(true), ctx, p1, p2); }
                catch { return Ok(MakeBool(false), ctx, p1, p2); }
            });
            BuiltInRegistry.Register("process_kill", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("process_kill", args, 1, ctx, p1, p2, out var err)) return err;
                try { Process.GetProcessById(AsInt(args[0])).Kill(); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"process_kill: {ex.Message}"); }
            });
            BuiltInRegistry.Register("process_list", (ctx, args, p1, p2) =>
            {
                var list = new List<RuntimeValue>();
                try
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        try
                        {
                            list.Add(new MapValue(new List<(RuntimeValue, RuntimeValue)>
                            {
                                (new StringValue("pid"), new IntegerValue(p.Id)),
                                (new StringValue("name"), new StringValue(p.ProcessName ?? ""))
                            }));
                        }
                        catch { }
                    }
                }
                catch { }
                return Ok(new ListValue(list), ctx, p1, p2);
            });
            BuiltInRegistry.Register("process_run", (ctx, args, p1, p2) =>
            {
                if (!ExpectMinArgs("process_run", args, 1, ctx, p1, p2, out var err)) return err;
                var psi = new ProcessStartInfo
                {
                    FileName = AsString(args[0]),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                if (args.Count >= 2 && args[1] is ListValue lv)
                    foreach (var a in lv.Elements) psi.ArgumentList.Add(AsString(a));
                try
                {
                    using var proc = new Process { StartInfo = psi };
                    var sbOut = new StringBuilder();
                    var sbErr = new StringBuilder();
                    proc.OutputDataReceived += (_, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit();
                    return Ok(new MapValue(new List<(RuntimeValue, RuntimeValue)>
                    {
                        (new StringValue("exit_code"), new IntegerValue(proc.ExitCode)),
                        (new StringValue("stdout"), new StringValue(sbOut.ToString())),
                        (new StringValue("stderr"), new StringValue(sbErr.ToString())),
                        (new StringValue("pid"), new IntegerValue(proc.Id)),
                    }), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"process_run: {ex.Message}"); }
            });
            BuiltInRegistry.Register("process_spawn", (ctx, args, p1, p2) =>
            {
                if (!ExpectMinArgs("process_spawn", args, 1, ctx, p1, p2, out var err)) return err;
                var psi = new ProcessStartInfo
                {
                    FileName = AsString(args[0]),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                if (args.Count >= 2 && args[1] is ListValue lv)
                    foreach (var a in lv.Elements) psi.ArgumentList.Add(AsString(a));
                try
                {
                    var proc = Process.Start(psi);
                    if (proc == null) return Fail(ctx, p1, p2, "process_spawn: failed to start");
                    long handle = Interlocked.Increment(ref _handleSeq);
                    _processes[handle] = proc;
                    return Ok(new LongValue(handle), ctx, p1, p2);
                }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"process_spawn: {ex.Message}"); }
            });
            BuiltInRegistry.Register("process_wait", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("process_wait", args, 1, ctx, p1, p2, out var err)) return err;
                long h = AsLong(args[0]);
                if (!_processes.TryGetValue(h, out var p)) return Fail(ctx, p1, p2, "process_wait: unknown handle");
                try { p.WaitForExit(); return Ok(new IntegerValue(p.ExitCode), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"process_wait: {ex.Message}"); }
            });
            BuiltInRegistry.Register("process_exit_code", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("process_exit_code", args, 1, ctx, p1, p2, out var err)) return err;
                long h = AsLong(args[0]);
                if (!_processes.TryGetValue(h, out var p)) return OkNull(ctx, p1, p2);
                if (!p.HasExited) return OkNull(ctx, p1, p2);
                return Ok(new IntegerValue(p.ExitCode), ctx, p1, p2);
            });
            BuiltInRegistry.Register("process_handle_kill", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("process_handle_kill", args, 1, ctx, p1, p2, out var err)) return err;
                long h = AsLong(args[0]);
                if (!_processes.TryGetValue(h, out var p)) return Fail(ctx, p1, p2, "process_handle_kill: unknown handle");
                try { p.Kill(); return Ok(MakeBool(true), ctx, p1, p2); }
                catch (Exception ex) { return Fail(ctx, p1, p2, $"process_handle_kill: {ex.Message}"); }
            });
            BuiltInRegistry.Register("process_handle_close", (ctx, args, p1, p2) =>
            {
                if (!ExpectArgs("process_handle_close", args, 1, ctx, p1, p2, out var err)) return err;
                long h = AsLong(args[0]);
                if (_processes.TryRemove(h, out var p)) { try { p.Dispose(); } catch { } }
                return OkNull(ctx, p1, p2);
            });
            BuiltInRegistry.Register("process_memory", (ctx, args, p1, p2) =>
            {
                try { return Ok(new LongValue(Process.GetCurrentProcess().WorkingSet64), ctx, p1, p2); }
                catch { return Ok(new LongValue(0), ctx, p1, p2); }
            });
        }
    }
}

using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    /// <summary>
    /// Structured FFI call tracer. Emits one JSONL line per call.
    /// Output sink configured via environment variable RA_FFI_TRACE_FILE (default: stderr).
    /// Activated per-binding via @dll_import(trace = true) or globally via RA_FFI_TRACE=1.
    /// </summary>
    public static class FfiTracer
    {
        private static readonly object _writeLock = new();
        private static TextWriter? _sink;
        private static bool _initAttempted;

        public static bool Enabled => Environment.GetEnvironmentVariable("RA_FFI_TRACE") == "1";

        // ----- Introspection store (deterministic fields only) -------------
        //
        // The JSONL line emitted to the sink carries inherently volatile data
        // (UTC timestamp, elapsed microseconds, the live return value such as
        // a process id) which makes stdout/stderr golden-comparison flaky. To
        // let regression tests verify that tracing actually FIRED — and that
        // the recorded call shape is correct — without depending on volatile
        // values, every Emit also records into this process-wide store the
        // fields that ARE a deterministic function of the call site:
        // library, entry point, argument count, and the OS last-error code.
        // Exposed to Ra via the `ffi_trace_count` / `ffi_trace_last`
        // builtins. The running counter mirrors the design of
        // `abi_canary_count` / `callback_count`; tests use a before/after
        // delta so the value is robust to accumulation across calls.
        private static long _count;
        private static string _lastLibrary = "";
        private static string _lastEntryPoint = "";
        private static int _lastArgCount;
        private static int _lastErrorCode;

        public static long Count => Interlocked.Read(ref _count);

        public static (string Library, string EntryPoint, int ArgCount, int LastError) LastRecord
        {
            get { lock (_writeLock) return (_lastLibrary, _lastEntryPoint, _lastArgCount, _lastErrorCode); }
        }

        // Test-isolation reset. Mirrors the other process-wide registries
        // cleared by Program.InitializeSymbolTable so menu-driven re-runs
        // start from a known state.
        public static void Reset()
        {
            Interlocked.Exchange(ref _count, 0);
            lock (_writeLock)
            {
                _lastLibrary = "";
                _lastEntryPoint = "";
                _lastArgCount = 0;
                _lastErrorCode = 0;
            }
        }

        public static void Emit(string library, string entryPoint, IReadOnlyList<string>? formattedArgs, string? returnRepr, long elapsedMicros, int lastErrorCode)
        {
            // Record deterministic fields BEFORE any sink work so the
            // introspection store is populated even when output is
            // suppressed or the sink write throws.
            Interlocked.Increment(ref _count);
            lock (_writeLock)
            {
                _lastLibrary = library ?? "";
                _lastEntryPoint = entryPoint ?? "";
                _lastArgCount = formattedArgs?.Count ?? 0;
                _lastErrorCode = lastErrorCode;
            }

            var sink = ResolveSink();
            if (sink == null) return;

            var sb = new StringBuilder(256);
            sb.Append("{\"ts\":\"").Append(DateTime.UtcNow.ToString("O")).Append("\",");
            sb.Append("\"tid\":").Append(Environment.CurrentManagedThreadId).Append(',');
            sb.Append("\"lib\":\"").Append(Escape(library)).Append("\",");
            sb.Append("\"fn\":\"").Append(Escape(entryPoint)).Append("\",");

            sb.Append("\"args\":[");
            if (formattedArgs != null)
            {
                for (int i = 0; i < formattedArgs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Escape(formattedArgs[i])).Append('"');
                }
            }
            sb.Append("],");

            sb.Append("\"ret\":\"").Append(Escape(returnRepr ?? "")).Append("\",");
            sb.Append("\"us\":").Append(elapsedMicros).Append(',');
            sb.Append("\"last_error\":").Append(lastErrorCode);
            sb.Append('}');

            lock (_writeLock)
            {
                try
                {
                    sink.WriteLine(sb.ToString());
                    sink.Flush();
                }
                catch { }
            }
        }

        private static TextWriter? ResolveSink()
        {
            if (_initAttempted) return _sink;
            lock (_writeLock)
            {
                if (_initAttempted) return _sink;
                _initAttempted = true;
                var path = Environment.GetEnvironmentVariable("RA_FFI_TRACE_FILE");
                if (string.IsNullOrWhiteSpace(path))
                {
                    _sink = Console.Error;
                    return _sink;
                }
                try
                {
                    _sink = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                }
                catch
                {
                    _sink = Console.Error;
                }
                return _sink;
            }
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}

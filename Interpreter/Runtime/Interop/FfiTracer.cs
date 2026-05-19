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

        public static void Emit(string library, string entryPoint, IReadOnlyList<string>? formattedArgs, string? returnRepr, long elapsedMicros, int lastErrorCode)
        {
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

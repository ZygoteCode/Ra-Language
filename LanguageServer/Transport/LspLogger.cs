using System;
using System.IO;
using System.Text;

namespace RaLanguage.LanguageServer.Transport
{
    public enum LogLevel
    {
        Error = 0,
        Warning = 1,
        Info = 2,
        Debug = 3,
    }

    /// <summary>
    /// Diagnostic logger for the language server. Writes exclusively to STDERR so
    /// STDOUT stays a clean JSON-RPC channel — a hard requirement of the protocol.
    /// Thread-safe; the read loop and debounced background work both log.
    /// </summary>
    public sealed class LspLogger
    {
        private readonly TextWriter _err;
        private readonly object _gate = new();
        private LogLevel _level;

        public LspLogger(LogLevel level = LogLevel.Info)
        {
            _level = level;
            // Own writer over the raw stderr handle; AutoFlush so crashes still
            // surface their last line.
            _err = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }

        public LogLevel Level
        {
            get => _level;
            set => _level = value;
        }

        public void Error(string message) => Write(LogLevel.Error, message);
        public void Warning(string message) => Write(LogLevel.Warning, message);
        public void Info(string message) => Write(LogLevel.Info, message);
        public void Debug(string message) => Write(LogLevel.Debug, message);

        public void Exception(string context, Exception ex)
            => Write(LogLevel.Error, context + ": " + ex.GetType().Name + ": " + ex.Message);

        private void Write(LogLevel level, string message)
        {
            if (level > _level) return;
            string tag = level switch
            {
                LogLevel.Error => "ERROR",
                LogLevel.Warning => "WARN",
                LogLevel.Info => "INFO",
                _ => "DEBUG",
            };
            lock (_gate)
            {
                _err.Write("[ra-lsp ");
                _err.Write(tag);
                _err.Write("] ");
                _err.WriteLine(message);
            }
        }
    }
}

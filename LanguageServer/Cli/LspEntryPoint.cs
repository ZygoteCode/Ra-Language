using System;
using System.IO;
using System.Text;
using RaLanguage.LanguageServer.Transport;

namespace RaLanguage.LanguageServer.Cli
{
    /// <summary>
    /// Boots the language server for the <c>--lsp</c> CLI mode. Its first job is to
    /// quarantine STDOUT: the JSON-RPC channel must carry protocol bytes only, so the
    /// process-wide <see cref="Console.Out"/> writer is redirected to STDERR. Any
    /// stray <c>Console.Write</c> from deep in the front-end then lands harmlessly on
    /// the log channel instead of corrupting a message frame. The raw STDOUT handle
    /// the connection uses is obtained separately and is unaffected by this redirect.
    /// </summary>
    public static class LspEntryPoint
    {
        public static int Run(string[] args)
        {
            // Quarantine stdout (see summary). Use a non-BOM UTF-8 writer.
            var stderrWriter = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(stderrWriter);

            var logger = new LspLogger(ParseLogLevel(args));

            try
            {
                var server = new LspServer(logger);
                return server.Run();
            }
            catch (Exception ex)
            {
                logger.Exception("language server crashed", ex);
                return 1;
            }
        }

        private static LogLevel ParseLogLevel(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--log-level", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1].ToLowerInvariant() switch
                    {
                        "error" => LogLevel.Error,
                        "warning" or "warn" => LogLevel.Warning,
                        "debug" or "trace" => LogLevel.Debug,
                        _ => LogLevel.Info,
                    };
                }
            }
            return LogLevel.Info;
        }
    }
}

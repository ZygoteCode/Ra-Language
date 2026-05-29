using System;
using System.IO;
using System.Text;
using System.Text.Json;
using RaLanguage.LanguageServer.Protocol;

namespace RaLanguage.LanguageServer.Transport
{
    /// <summary>
    /// Owns the stdio byte channel and the JSON-RPC framing (LSP base protocol:
    /// <c>Content-Length</c> header, CRLFCRLF separator, UTF-8 body). Reads are
    /// driven by the single server pump; writes are serialized behind a lock so
    /// debounced background diagnostics can publish safely while the pump is busy.
    /// All payloads are (de)serialized through <see cref="RaLspJsonContext"/> — the
    /// only AOT-safe path under this project's trimming configuration.
    /// </summary>
    public sealed class LspConnection
    {
        private readonly Stream _in;
        private readonly Stream _out;
        private readonly object _writeGate = new();
        private readonly LspLogger _log;

        public LspConnection(LspLogger log)
        {
            _log = log;
            // Raw byte streams. Crucially NOT Console.In/Out (TextReader/Writer),
            // which would apply newline translation and encoding that corrupts the
            // Content-Length byte accounting.
            _in = new BufferedStream(Console.OpenStandardInput(), 1 << 16);
            _out = Console.OpenStandardOutput();
        }

        /// <summary>
        /// Blocks until a full message body is read. Returns null on clean EOF
        /// (client closed stdin), which the server treats as a shutdown signal.
        /// </summary>
        public byte[]? ReadMessage()
        {
            int contentLength = -1;

            // Read headers until the blank separator line.
            while (true)
            {
                string? line = ReadHeaderLine();
                if (line == null) return null;       // EOF mid-stream
                if (line.Length == 0) break;          // blank line ends headers

                int colon = line.IndexOf(':');
                if (colon <= 0) continue;             // tolerate malformed header lines
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(value, out contentLength)) contentLength = -1;
                }
                // Content-Type is accepted and ignored.
            }

            if (contentLength < 0)
            {
                _log.Warning("Message without a valid Content-Length header; skipping.");
                return Array.Empty<byte>();
            }

            var body = new byte[contentLength];
            try
            {
                _in.ReadExactly(body, 0, contentLength);
            }
            catch (EndOfStreamException)
            {
                return null;
            }
            return body;
        }

        private string? ReadHeaderLine()
        {
            // Header lines are ASCII and terminated by CRLF. Read until '\n'.
            var sb = new StringBuilder(32);
            while (true)
            {
                int b = _in.ReadByte();
                if (b == -1) return sb.Length == 0 ? null : sb.ToString();
                if (b == '\n') break;
                if (b == '\r') continue;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        // ---- Writing ----

        public void SendResult(JsonElement id, object? result)
        {
            var bytes = BuildMessage(writer =>
            {
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                id.WriteTo(writer);
                writer.WritePropertyName("result");
                if (result is null) writer.WriteNullValue();
                else JsonSerializer.Serialize(writer, result, result.GetType(), RaLspJsonContext.Default);
            });
            WriteFramed(bytes);
        }

        public void SendError(JsonElement? id, int code, string message)
        {
            var bytes = BuildMessage(writer =>
            {
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                if (id.HasValue) id.Value.WriteTo(writer);
                else writer.WriteNullValue();
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", code);
                writer.WriteString("message", message);
                writer.WriteEndObject();
            });
            WriteFramed(bytes);
        }

        public void SendNotification(string method, object? @params)
        {
            var bytes = BuildMessage(writer =>
            {
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", method);
                writer.WritePropertyName("params");
                if (@params is null) writer.WriteNullValue();
                else JsonSerializer.Serialize(writer, @params, @params.GetType(), RaLspJsonContext.Default);
            });
            WriteFramed(bytes);
        }

        private static byte[] BuildMessage(Action<Utf8JsonWriter> writeContents)
        {
            using var ms = new MemoryStream(256);
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writeContents(writer);
                writer.WriteEndObject();
            }
            return ms.ToArray();
        }

        private void WriteFramed(byte[] body)
        {
            // "Content-Length: N\r\n\r\n" is pure ASCII.
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            lock (_writeGate)
            {
                _out.Write(header, 0, header.Length);
                _out.Write(body, 0, body.Length);
                _out.Flush();
            }
        }
    }
}

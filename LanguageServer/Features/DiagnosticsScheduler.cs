using System;
using System.Collections.Concurrent;
using System.Threading;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Transport;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Debounces diagnostic publishing. Each edit reschedules a per-document timer so
    /// a burst of keystrokes triggers a single re-analysis once typing settles. The
    /// timer callbacks run on the thread pool and publish through the connection's
    /// locked writer, so they never interleave with the read pump's own writes.
    /// </summary>
    public sealed class DiagnosticsScheduler : IDisposable
    {
        private readonly LspConnection _connection;
        private readonly DocumentStore _store;
        private readonly IDiagnosticsService _diagnostics;
        private readonly LspLogger _log;
        private readonly int _debounceMs;
        private readonly ConcurrentDictionary<string, Timer> _timers = new();

        public DiagnosticsScheduler(
            LspConnection connection,
            DocumentStore store,
            IDiagnosticsService diagnostics,
            LspLogger log,
            int debounceMs = 250)
        {
            _connection = connection;
            _store = store;
            _diagnostics = diagnostics;
            _log = log;
            _debounceMs = debounceMs;
        }

        /// <summary>Schedule (or reschedule) a debounced publish for <paramref name="uri"/>.</summary>
        public void Schedule(string uri)
        {
            // Timer state is the uri itself, so the callback knows exactly what to
            // publish (no reverse lookup). The factory overload avoids a closure.
            var timer = _timers.GetOrAdd(
                uri,
                static (key, self) => new Timer(self.OnTimer, key, Timeout.Infinite, Timeout.Infinite),
                this);
            timer.Change(_debounceMs, Timeout.Infinite);
        }

        private void OnTimer(object? state)
        {
            if (state is string uri) PublishImmediately(uri);
        }

        public void PublishImmediately(string uri)
        {
            try
            {
                var doc = _store.TryGet(uri);
                if (doc == null) return;
                var @params = _diagnostics.Compute(doc);
                _connection.SendNotification("textDocument/publishDiagnostics", @params);
            }
            catch (Exception ex)
            {
                _log.Exception("publishDiagnostics", ex);
            }
        }

        public void Clear(string uri)
        {
            if (_timers.TryRemove(uri, out var timer))
            {
                timer.Dispose();
            }
            // Tell the client to drop existing markers for this document.
            var doc = _store.TryGet(uri);
            string rawUri = doc?.Document.RawUri ?? uri;
            _connection.SendNotification("textDocument/publishDiagnostics", new PublishDiagnosticsParams
            {
                Uri = rawUri,
                Diagnostics = Array.Empty<LspDiagnostic>(),
            });
        }

        public void Dispose()
        {
            foreach (var kv in _timers)
            {
                kv.Value.Dispose();
            }
            _timers.Clear();
        }
    }
}

using System.Collections.Concurrent;
using System.Collections.Generic;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Text;

namespace RaLanguage.LanguageServer.Workspace
{
    /// <summary>
    /// Holds the set of documents the client currently has open, keyed by a
    /// normalized URI. Concurrent because the read pump opens/closes documents while
    /// the debounced diagnostics scheduler reads them on a background thread.
    /// </summary>
    public sealed class DocumentStore
    {
        private readonly ConcurrentDictionary<string, RaDocument> _documents = new();

        /// <summary>Negotiated LSP position encoding; set once at <c>initialize</c>.</summary>
        public PositionEncodingKind Encoding { get; set; } = PositionEncodingKind.Utf16;

        public RaDocument Open(TextDocumentItem item)
        {
            var doc = new RaDocument(new TextDocument(item, Encoding));
            _documents[doc.Document.Uri] = doc;
            return doc;
        }

        public RaDocument? Change(string uri, IReadOnlyList<TextDocumentContentChangeEvent> changes, int version)
        {
            if (_documents.TryGetValue(UriUtil.NormalizeKey(uri), out var doc))
            {
                doc.ApplyChanges(changes, version);
                return doc;
            }
            return null;
        }

        public void Close(string uri)
        {
            _documents.TryRemove(UriUtil.NormalizeKey(uri), out _);
        }

        public RaDocument? TryGet(string uri)
        {
            return _documents.TryGetValue(UriUtil.NormalizeKey(uri), out var doc) ? doc : null;
        }

        public IReadOnlyCollection<string> OpenUris => (IReadOnlyCollection<string>)_documents.Keys;

        public bool Contains(string uri) => _documents.ContainsKey(UriUtil.NormalizeKey(uri));
    }
}

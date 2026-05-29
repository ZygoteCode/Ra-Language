using System;
using System.Collections.Generic;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Text;
// Disambiguate from System.Range (pulled in by ImplicitUsings).
using Range = RaLanguage.LanguageServer.Protocol.Range;
using Position = RaLanguage.LanguageServer.Protocol.Position;

namespace RaLanguage.LanguageServer.Workspace
{
    /// <summary>
    /// In-memory authoritative copy of an open document: text, version and a
    /// <see cref="LineIndex"/>. The index is rebuilt after every applied change so
    /// later changes in the same batch resolve against the updated text, and so all
    /// range conversions stay exact for the snapshot the server currently holds.
    /// </summary>
    public sealed class TextDocument
    {
        public string Uri { get; }
        /// <summary>The URI string exactly as the client sent it. Used when echoing
        /// locations / diagnostics back, since some clients match by raw string.</summary>
        public string RawUri { get; }
        public string FileName { get; }
        public string LanguageId { get; private set; }
        public int Version { get; private set; }
        public string Text { get; private set; }
        public LineIndex Lines { get; private set; }

        private readonly PositionEncodingKind _encoding;

        public TextDocument(TextDocumentItem item, PositionEncodingKind encoding = PositionEncodingKind.Utf16)
        {
            Uri = UriUtil.NormalizeKey(item.Uri);
            RawUri = item.Uri;
            FileName = UriUtil.ToFileSystemPath(item.Uri);
            LanguageId = item.LanguageId;
            Version = item.Version;
            Text = item.Text ?? string.Empty;
            _encoding = encoding;
            Lines = new LineIndex(Text, _encoding);
        }

        /// <summary>Apply an ordered batch of incremental (or full) content changes.</summary>
        public void ApplyChanges(IReadOnlyList<TextDocumentContentChangeEvent> changes, int newVersion)
        {
            for (int i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (change.Range == null)
                {
                    // Full-document replacement.
                    Text = change.Text ?? string.Empty;
                }
                else
                {
                    int start = Lines.PositionToOffset(change.Range.Start.Line, change.Range.Start.Character);
                    int end = Lines.PositionToOffset(change.Range.End.Line, change.Range.End.Character);
                    if (end < start) (start, end) = (end, start);
                    Text = string.Concat(Text.AsSpan(0, start), change.Text, Text.AsSpan(end));
                }
                Lines = new LineIndex(Text, _encoding);
            }
            Version = newVersion;
        }

        // ---- Range conversion helpers (absolute Idx is the source of truth) ----

        public Position PositionAt(int offset)
        {
            var (line, character) = Lines.OffsetToPosition(offset);
            return new Position(line, character);
        }

        public Range RangeOf(int startOffset, int endOffset)
        {
            if (endOffset < startOffset) endOffset = startOffset;
            return new Range(PositionAt(startOffset), PositionAt(endOffset));
        }

        public int OffsetAt(Position position) => Lines.PositionToOffset(position.Line, position.Character);
    }
}

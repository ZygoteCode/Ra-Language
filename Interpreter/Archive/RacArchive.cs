using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Archive
{
    // Decoded view of a .rac file. Produced by RacReader, consumed by
    // RacRunner and RacInspector. Holds metadata only — section
    // payloads are loaded on demand through `ReadSection(index)`.
    public sealed class RacArchive : IDisposable
    {
        public RacHeader Header { get; }
        public IReadOnlyList<RacSectionEntry> Sections { get; }
        public RacManifest Manifest { get; }

        public string SourcePath { get; }

        private readonly System.IO.Stream _stream;
        private readonly bool _ownsStream;
        private bool _disposed;

        internal RacArchive(string sourcePath, System.IO.Stream stream, bool ownsStream,
            RacHeader header, IReadOnlyList<RacSectionEntry> sections, RacManifest manifest)
        {
            SourcePath = sourcePath;
            _stream = stream;
            _ownsStream = ownsStream;
            Header = header;
            Sections = sections;
            Manifest = manifest;
        }

        public byte[] ReadSection(int index)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RacArchive));
            if (index < 0 || index >= Sections.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var entry = Sections[index];
            return RacReader.ReadSectionPayload(_stream, entry);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsStream) _stream.Dispose();
        }
    }
}

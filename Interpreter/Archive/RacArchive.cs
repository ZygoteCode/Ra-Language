using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Archive
{
    // Decoded view of a .rac file. Produced by RacReader, consumed by
    // RacRunner and RacInspector. Holds metadata only — section payloads
    // are loaded on demand through `ReadSection(index)` and the OS pages
    // in only the bytes that get touched (mmap path) or the bytes that
    // get requested (stream path).
    public sealed class RacArchive : IDisposable
    {
        public RacHeader Header { get; }
        public IReadOnlyList<RacSectionEntry> Sections { get; }
        public RacManifest Manifest { get; }

        public string SourcePath { get; }

        private readonly RacSource _source;
        private bool _disposed;
        // v1.1 (#7): lazily-decoded shared constant pool. First access
        // through `SharedConstPool` triggers the section lookup +
        // decode; subsequent calls return the cached instance.
        private SharedConstPool? _sharedConstPool;
        private bool _sharedConstPoolResolved;

        internal RacArchive(string sourcePath, RacSource source,
            RacHeader header, IReadOnlyList<RacSectionEntry> sections, RacManifest manifest)
        {
            SourcePath = sourcePath;
            _source = source;
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
            return RacReader.ReadSectionPayload(_source, entry);
        }

        // Locate + decode the archive-level SharedConstPool, or return
        // null when the archive has none (every const inline). The
        // result is cached for the archive's lifetime so multi-module
        // bytecode loads pay the decode cost once.
        public SharedConstPool? SharedConstPool
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RacArchive));
                if (_sharedConstPoolResolved) return _sharedConstPool;
                for (int i = 0; i < Sections.Count; i++)
                {
                    if (Sections[i].Kind != RacSectionKind.SharedConstPool) continue;
                    byte[] payload = ReadSection(i);
                    _sharedConstPool = Archive.SharedConstPool.Decode(payload);
                    break;
                }
                _sharedConstPoolResolved = true;
                return _sharedConstPool;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }
}

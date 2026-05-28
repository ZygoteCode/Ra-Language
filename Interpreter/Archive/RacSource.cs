using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace RaLanguage.Interpreter.Archive
{
    // Backing-store abstraction for a .rac archive. Decouples the reader
    // from how the bytes are sourced so the same parse / verify / section-
    // read pipeline runs against:
    //
    //   * a MemoryMappedFile — the default for on-disk archives. Pages
    //     fault in on demand, so `Open` only touches the header + section
    //     directory regardless of archive size. Uncompressed section
    //     reads are a single mmap-to-byte[] copy; the OS handles the
    //     I/O. See `MappedRacSource`.
    //   * a Stream — for in-memory archives passed via `RacReader.Open(Stream)`.
    //     Falls back to ordinary FileStream.Read / Seek behaviour. See
    //     `StreamRacSource`. Not thread-safe by itself; the reader holds
    //     the only reference.
    //
    // The mmap path is the one the v1.1 "<1ms archive open" budget targets.
    // The stream path stays correct for callers that want to drive the
    // reader from a non-file source (test fixtures, network buffers, etc.).
    public abstract class RacSource : IDisposable
    {
        public abstract long Length { get; }

        // Read `dest.Length` bytes starting at `offset`. Throws on short
        // read. Used for small contiguous reads — header (96 bytes),
        // section directory (64 bytes per entry), and uncompressed section
        // payloads.
        public abstract void ReadExact(long offset, Span<byte> dest);

        // Open a Stream over the half-open range `[offset, offset + length)`.
        // Used for streaming reads (DeflateStream input, streaming hash
        // over large uncompressed payloads). Caller disposes.
        public abstract Stream OpenView(long offset, long length);

        public virtual void Dispose() { }
    }

    // MemoryMappedFile-backed source. Holds an exclusive read-only mapping
    // for the archive's lifetime; sections are read via per-call view
    // streams so the OS pages in only what is actually touched.
    public sealed class MappedRacSource : RacSource
    {
        private readonly MemoryMappedFile _mmf;
        private readonly long _length;
        private bool _disposed;

        public MappedRacSource(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path");
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException("rac: archive not found", path);
            _length = fi.Length;
            if (_length <= 0)
                throw new InvalidDataException("rac: archive is empty");
            // ReadOnly access + ShareInheritability.None — we want to
            // refuse writes through the mapping and not let the handle
            // leak across spawn boundaries. Capacity 0 means "use file
            // length".
            _mmf = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, mapName: null,
                capacity: 0, MemoryMappedFileAccess.Read);
        }

        public override long Length => _length;

        public override void ReadExact(long offset, Span<byte> dest)
        {
            if (offset < 0 || offset + dest.Length > _length)
                throw new InvalidDataException(
                    $"rac: read [{offset}, {offset + dest.Length}) out of archive bounds (len={_length})");
            // Map a per-call view sized exactly to the request. Pages
            // outside the slice are never touched. The view is unmapped
            // when the accessor is disposed at the end of this method.
            using var view = _mmf.CreateViewAccessor(offset, dest.Length, MemoryMappedFileAccess.Read);
            // ReadArray is the safe primitive that lands in the GC heap;
            // we use it to fill `dest` without going through a separate
            // byte[] allocation when `dest` is already on the heap. For
            // small buffers (header + section dir) the cost is dominated
            // by the kernel-side page fault, not the byte copy.
            byte[] buf = new byte[dest.Length];
            view.ReadArray(0, buf, 0, dest.Length);
            buf.AsSpan().CopyTo(dest);
        }

        public override Stream OpenView(long offset, long length)
        {
            if (offset < 0 || offset + length > _length || length < 0)
                throw new InvalidDataException(
                    $"rac: view [{offset}, {offset + length}) out of bounds");
            if (length == 0)
                return new MemoryStream(Array.Empty<byte>(), writable: false);
            // CreateViewStream gives us a Seekable Stream rooted at
            // `offset` and bounded at `length`. Reads against it page
            // the underlying file in on demand — perfect for the
            // DeflateStream input path and for streaming SHA-256.
            return _mmf.CreateViewStream(offset, length, MemoryMappedFileAccess.Read);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _mmf.Dispose();
        }
    }

    // Stream-backed fallback for archives that aren't on the local
    // filesystem (test fixtures, network responses materialised in memory,
    // etc.). Not thread-safe; the reader serialises access.
    public sealed class StreamRacSource : RacSource
    {
        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private bool _disposed;

        public StreamRacSource(Stream stream, bool ownsStream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek)
                throw new InvalidDataException("rac: stream source must be seekable");
            _stream = stream;
            _ownsStream = ownsStream;
        }

        public override long Length => _stream.Length;

        public override void ReadExact(long offset, Span<byte> dest)
        {
            if (offset < 0 || offset + dest.Length > _stream.Length)
                throw new InvalidDataException(
                    $"rac: read [{offset}, {offset + dest.Length}) out of stream bounds");
            _stream.Position = offset;
            int total = 0;
            while (total < dest.Length)
            {
                int n = _stream.Read(dest.Slice(total));
                if (n <= 0) throw new EndOfStreamException("rac: short read on stream source");
                total += n;
            }
        }

        public override Stream OpenView(long offset, long length)
        {
            if (offset < 0 || offset + length > _stream.Length || length < 0)
                throw new InvalidDataException(
                    $"rac: view [{offset}, {offset + length}) out of stream bounds");
            _stream.Position = offset;
            // Slurp the slice into a MemoryStream so the returned Stream
            // can be used independently (DeflateStream may read past
            // closing the source `_stream`'s seek position). For mmap-
            // backed sources we hand back a view stream instead — same
            // shape, different ownership.
            if (length == 0)
                return new MemoryStream(Array.Empty<byte>(), writable: false);
            byte[] buf = new byte[length];
            int total = 0;
            while (total < buf.Length)
            {
                int n = _stream.Read(buf, total, buf.Length - total);
                if (n <= 0) throw new EndOfStreamException("rac: short read on stream view");
                total += n;
            }
            return new MemoryStream(buf, writable: false);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsStream) _stream.Dispose();
        }
    }
}

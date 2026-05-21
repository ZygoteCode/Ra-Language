using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RaLanguage.Interpreter.Runtime.Asm
{
    /// <summary>
    /// Content-hash deduplicating allocator for executable asm regions.
    ///
    /// Each unique source (after whitespace normalisation) produces exactly
    /// one allocation. Repeated requests hit the cache.
    ///
    /// Eviction: LRU by region. When the configured maximum live bytes is
    /// exceeded, the least recently touched region is unmapped — note: any
    /// active function pointer into an evicted region becomes invalid.
    /// Callers that hold long-lived pointers should pin them via
    /// <see cref="Pin"/>.
    ///
    /// W^X is preserved by relying on <see cref="AsmExecutor"/>, which
    /// allocates RW, writes bytes, then transitions the page to RX before
    /// returning. We never co-locate multiple regions in the same page.
    /// </summary>
    public static class AsmCodePool
    {
        public sealed class Slot
        {
            public IntPtr Address;
            public int Length;
            public AsmExecutor.ExecutableRegion Region = default!;
            public long Touched;
            public string ContentHash = "";
            public bool Pinned;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<string, Slot> _byHash = new(StringComparer.Ordinal);
        private static readonly LinkedList<string> _lru = new();
        private static readonly Dictionary<string, LinkedListNode<string>> _lruIndex = new(StringComparer.Ordinal);
        private static long _maxTotalBytes = 64 * 1024 * 1024;
        private static long _liveBytes;
        private static long _allocClock;

        public static long MaxTotalBytes { get => _maxTotalBytes; set { _maxTotalBytes = value; lock (_lock) EvictIfNeeded(0); } }
        public static long LiveBytes { get { lock (_lock) return _liveBytes; } }
        public static int InternedCount { get { lock (_lock) return _byHash.Count; } }

        public static Slot Allocate(byte[] code, string contentHash)
        {
            lock (_lock)
            {
                if (_byHash.TryGetValue(contentHash, out var existing))
                {
                    existing.Touched = Interlocked.Increment(ref _allocClock);
                    Touch(contentHash);
                    return existing;
                }

                EvictIfNeeded(code.Length);

                var region = AsmExecutor.Allocate(code);
                var slot = new Slot
                {
                    Address = region.Address,
                    Length = code.Length,
                    Region = region,
                    Touched = Interlocked.Increment(ref _allocClock),
                    ContentHash = contentHash,
                };
                _byHash[contentHash] = slot;
                var node = _lru.AddLast(contentHash);
                _lruIndex[contentHash] = node;
                _liveBytes += code.Length;
                return slot;
            }
        }

        public static void Pin(string contentHash)
        {
            lock (_lock)
            {
                if (_byHash.TryGetValue(contentHash, out var slot)) slot.Pinned = true;
            }
        }

        public static string ComputeHash(string source)
        {
            var normalized = NormalizeWhitespace(source);
            var bytes = Encoding.UTF8.GetBytes(normalized);
            var h = SHA256.HashData(bytes);
            return Convert.ToHexString(h);
        }

        private static string NormalizeWhitespace(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool prevWs = true;
            foreach (var c in s)
            {
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    if (!prevWs) { sb.Append(' '); prevWs = true; }
                }
                else { sb.Append(c); prevWs = false; }
            }
            return sb.ToString().Trim();
        }

        public static void Clear()
        {
            lock (_lock)
            {
                foreach (var slot in _byHash.Values) slot.Region?.Dispose();
                _byHash.Clear();
                _lru.Clear();
                _lruIndex.Clear();
                _liveBytes = 0;
            }
        }

        private static void Touch(string key)
        {
            if (_lruIndex.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lruIndex[key] = _lru.AddLast(key);
            }
        }

        private static void EvictIfNeeded(int incoming)
        {
            while (_liveBytes + incoming > _maxTotalBytes && _lru.Count > 0)
            {
                LinkedListNode<string>? node = _lru.First;
                while (node != null && _byHash[node.Value].Pinned) node = node.Next;
                if (node == null) return;
                var slot = _byHash[node.Value];
                slot.Region.Dispose();
                _liveBytes -= slot.Length;
                _byHash.Remove(node.Value);
                _lru.Remove(node);
                _lruIndex.Remove(node.Value);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Archive
{
    // v1.1 (#7): archive-level interned constant pool.
    //
    // Every module's ModuleBytecode payload stores its `Consts[]` array;
    // before this section each entry was inlined into the per-module
    // payload. With cross-module sharing, repeated values (typical:
    // string literals, magic numbers) collapse to one pool entry that
    // every module references by `u32` index.
    //
    // Layout (little-endian):
    //
    //   "SCPL"  u32 magic
    //   u16 version = 1
    //   u16 reserved
    //
    //   i32 stringCount  + (string * count)
    //   i32 numberCount  + (BigInteger Unscaled, BigInteger Scale) * count
    //   i32 integerCount + (i32 * count)
    //   i32 longCount    + (i64 * count)
    //   i32 doubleCount  + (u64 bit pattern * count)
    //   i32 floatCount   + (u32 bit pattern * count)
    //
    // Pool entries are deduplicated by *content*: identical strings /
    // BigNumber pairs / i32 / i64 / IEEE-754 bit-patterns share one
    // index. Bool stays inline (1-byte tag — no shareable savings).
    // NullValue stays inline (single-tag encoding).
    //
    // Threshold gate at build time: a value is only pooled when it is
    // referenced from >= 2 module const slots across the archive. A
    // singleton reference stays inline so we never pay the (tag +
    // u32) overhead to share what's already monomorphic.
    public sealed class SharedConstPool
    {
        private const uint Magic = (uint)'S' | ((uint)'C' << 8) | ((uint)'P' << 16) | ((uint)'L' << 24);
        private const ushort Version = 1;

        public readonly List<string> Strings = new();
        public readonly List<BigNumber> Numbers = new();
        public readonly List<int> Integers = new();
        public readonly List<long> Longs = new();
        public readonly List<double> Doubles = new();
        public readonly List<float> Floats = new();

        public int TotalEntries =>
            Strings.Count + Numbers.Count + Integers.Count
            + Longs.Count + Doubles.Count + Floats.Count;

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            var w = new RacBinaryWriter(ms);
            w.WriteU32(Magic);
            w.WriteU16(Version);
            w.WriteU16(0);

            w.WriteI32(Strings.Count);
            for (int i = 0; i < Strings.Count; i++) w.WriteString(Strings[i]);

            w.WriteI32(Numbers.Count);
            for (int i = 0; i < Numbers.Count; i++)
            {
                ModuleBytecodeIo.WriteBigInteger(w, Numbers[i].Unscaled);
                ModuleBytecodeIo.WriteBigInteger(w, Numbers[i].Scale);
            }

            w.WriteI32(Integers.Count);
            for (int i = 0; i < Integers.Count; i++) w.WriteI32(Integers[i]);

            w.WriteI32(Longs.Count);
            for (int i = 0; i < Longs.Count; i++) w.WriteI64(Longs[i]);

            w.WriteI32(Doubles.Count);
            for (int i = 0; i < Doubles.Count; i++)
                w.WriteU64((ulong)BitConverter.DoubleToInt64Bits(Doubles[i]));

            w.WriteI32(Floats.Count);
            for (int i = 0; i < Floats.Count; i++)
                w.WriteU32((uint)BitConverter.SingleToInt32Bits(Floats[i]));

            return ms.ToArray();
        }

        public static SharedConstPool Decode(ReadOnlySpan<byte> payload)
        {
            using var ms = new MemoryStream(payload.ToArray(), writable: false);
            var r = new RacBinaryReader(ms);
            uint magic = r.ReadU32();
            if (magic != Magic) throw new InvalidDataException("rac: SharedConstPool magic mismatch");
            ushort ver = r.ReadU16();
            if (ver != Version) throw new InvalidDataException($"rac: SharedConstPool version {ver} not supported");
            ushort reserved = r.ReadU16();
            if (reserved != 0) throw new InvalidDataException("rac: SharedConstPool reserved must be zero");

            var p = new SharedConstPool();

            int strN = r.ReadI32();
            VerifyCount(strN);
            for (int i = 0; i < strN; i++) p.Strings.Add(r.ReadString() ?? "");

            int numN = r.ReadI32();
            VerifyCount(numN);
            for (int i = 0; i < numN; i++)
            {
                var u = ModuleBytecodeIo.ReadBigInteger(r);
                var s = ModuleBytecodeIo.ReadBigInteger(r);
                p.Numbers.Add(new BigNumber(u, s));
            }

            int intN = r.ReadI32();
            VerifyCount(intN);
            for (int i = 0; i < intN; i++) p.Integers.Add(r.ReadI32());

            int longN = r.ReadI32();
            VerifyCount(longN);
            for (int i = 0; i < longN; i++) p.Longs.Add(r.ReadI64());

            int dblN = r.ReadI32();
            VerifyCount(dblN);
            for (int i = 0; i < dblN; i++)
                p.Doubles.Add(BitConverter.Int64BitsToDouble(unchecked((long)r.ReadU64())));

            int fltN = r.ReadI32();
            VerifyCount(fltN);
            for (int i = 0; i < fltN; i++)
                p.Floats.Add(BitConverter.Int32BitsToSingle(unchecked((int)r.ReadU32())));

            return p;
        }

        private static void VerifyCount(int n)
        {
            if (n < 0 || n > 16_777_216)
                throw new InvalidDataException($"rac: bogus SharedConstPool count {n}");
        }
    }

    // Two-pass builder used at packaging time. Pass 1 ("Observe") walks
    // every module's RaFunction.Consts[] and tallies per-value reference
    // counts; pass 2 ("Finalise") promotes any value with >= 2 refs to
    // the pool and assigns a stable u32 index. Singleton refs stay
    // inline.
    public sealed class SharedConstPoolBuilder
    {
        private readonly Dictionary<string, int> _stringCounts = new(StringComparer.Ordinal);
        // BigNumber doesn't define structural equality with reliable
        // GetHashCode; key by canonical string form. Cheap for the
        // typical workload (hot literals are small ints in the
        // small-int cache; rare big numbers do one ToString each).
        private readonly Dictionary<string, int> _numberCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<int, int> _integerCounts = new();
        private readonly Dictionary<long, int> _longCounts = new();
        private readonly Dictionary<long, int> _doubleBitsCounts = new();
        private readonly Dictionary<int, int> _floatBitsCounts = new();

        // After Finalise, these resolve a value back to its assigned
        // pool index (or -1 == "inline this one"). Singleton refs land
        // in the inline path so the writer never emits a pool tag for
        // them.
        private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numberIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<int, int> _integerIndex = new();
        private readonly Dictionary<long, int> _longIndex = new();
        private readonly Dictionary<long, int> _doubleBitsIndex = new();
        private readonly Dictionary<int, int> _floatBitsIndex = new();

        public SharedConstPool Pool { get; } = new();
        public bool Finalised { get; private set; }
        public int Observed { get; private set; }
        public int Pooled { get; private set; }

        // Pass 1: record every const reference. Idempotent — re-observing
        // the same value just bumps its count.
        public void Observe(RuntimeValue? v)
        {
            if (Finalised)
                throw new InvalidOperationException("SharedConstPoolBuilder: Observe called after Finalise");
            if (v == null) return;
            Observed++;
            switch (v)
            {
                case StringValue s:
                    Inc(_stringCounts, s.Value);
                    break;
                case NumberValue n:
                    Inc(_numberCounts, NumberKey(n.Value));
                    break;
                case IntegerValue iv:
                    Inc(_integerCounts, iv.Value);
                    break;
                case LongValue lv:
                    Inc(_longCounts, lv.Value);
                    break;
                case DoubleValue dv:
                    Inc(_doubleBitsCounts, BitConverter.DoubleToInt64Bits(dv.Value));
                    break;
                case FloatValue fv:
                    Inc(_floatBitsCounts, BitConverter.SingleToInt32Bits(fv.Value));
                    break;
                // Bool / Null intentionally not poolable.
            }
        }

        public void ObserveMany(RuntimeValue?[] consts)
        {
            for (int i = 0; i < consts.Length; i++) Observe(consts[i]);
        }

        // Pass 2 — promote a value to the pool only when the projected
        // byte save outweighs the per-entry overhead.
        //
        // Wire-format break-even math (inline-vs-pool for K refs to a
        // string of N bytes):
        //   inline  : K * (1 tag + 4 len + N bytes) = K * (5 + N)
        //   pool    : K * (1 tag + 4 idx) + (4 len + N) = 5K + 4 + N
        //   save    : K * (5 + N) - (5K + 4 + N) = N * (K - 1) - 4
        //
        // For fixed-width payloads (long, double — 8 bytes inline w/o
        // length prefix), pool entries skip the length prefix:
        //   save    : K * (1 + 8) - (8 + K * 5) = 3 * (K - 1) - 8
        //
        // For 4-byte fixed payloads (int, float), inline and pool ref
        // are both `1 + 4 = 5` bytes per ref; pool storage adds a net
        // 4 bytes per value. Skip those types entirely — pooling them
        // can only make the archive bigger.
        //
        // We also amortise the once-per-archive section overhead
        // (magic + version + 6 count headers + section directory
        // entry ~ 100 bytes). If the total projected save doesn't
        // clear that, we abandon the pool so a tiny archive never
        // pays the 100-byte overhead for a 10-byte win.
        private const int SectionOverheadBytes = 100;

        public void Finalise()
        {
            if (Finalised) return;
            Finalised = true;

            int totalSave = 0;

            foreach (var kvp in _stringCounts)
            {
                int k = kvp.Value;
                if (k < 2) continue;
                int n = System.Text.Encoding.UTF8.GetByteCount(kvp.Key);
                int save = n * (k - 1) - 4;
                if (save <= 0) continue;
                _stringIndex[kvp.Key] = Pool.Strings.Count;
                Pool.Strings.Add(kvp.Key);
                Pooled++;
                totalSave += save;
            }
            foreach (var kvp in _numberCounts)
            {
                int k = kvp.Value;
                if (k < 2) continue;
                var bn = ParseNumberKey(kvp.Key);
                int n = bn.Unscaled.GetByteCount() + bn.Scale.GetByteCount() + 8;
                int save = n * (k - 1) - 4;
                if (save <= 0) continue;
                _numberIndex[kvp.Key] = Pool.Numbers.Count;
                Pool.Numbers.Add(bn);
                Pooled++;
                totalSave += save;
            }
            // Long / Double — 8-byte payloads, fixed-width pool entries.
            foreach (var kvp in _longCounts)
            {
                int k = kvp.Value;
                if (k < 2) continue;
                int save = 3 * (k - 1) - 8;
                if (save <= 0) continue;
                _longIndex[kvp.Key] = Pool.Longs.Count;
                Pool.Longs.Add(kvp.Key);
                Pooled++;
                totalSave += save;
            }
            foreach (var kvp in _doubleBitsCounts)
            {
                int k = kvp.Value;
                if (k < 2) continue;
                int save = 3 * (k - 1) - 8;
                if (save <= 0) continue;
                _doubleBitsIndex[kvp.Key] = Pool.Doubles.Count;
                Pool.Doubles.Add(BitConverter.Int64BitsToDouble(kvp.Key));
                Pooled++;
                totalSave += save;
            }
            // (Integers and Floats intentionally never pool — see
            // header math above; they're net-neutral or net-negative.)

            // Section + directory overhead amortisation. If the total
            // projected save doesn't clear it, ditch the pool so the
            // writer produces a v1 payload with every const inline.
            if (totalSave <= SectionOverheadBytes)
            {
                Pool.Strings.Clear();
                Pool.Numbers.Clear();
                Pool.Integers.Clear();
                Pool.Longs.Clear();
                Pool.Doubles.Clear();
                Pool.Floats.Clear();
                _stringIndex.Clear();
                _numberIndex.Clear();
                _integerIndex.Clear();
                _longIndex.Clear();
                _doubleBitsIndex.Clear();
                _floatBitsIndex.Clear();
                Pooled = 0;
            }
        }

        // Query helpers used by ModuleBytecodeIo at serialize time.
        // Return the pool index if the value should be encoded as a
        // pool ref, or -1 when the value should be inlined.
        public int ResolveString(string v)
            => _stringIndex.TryGetValue(v, out int idx) ? idx : -1;
        public int ResolveNumber(BigNumber v)
            => _numberIndex.TryGetValue(NumberKey(v), out int idx) ? idx : -1;
        public int ResolveInteger(int v)
            => _integerIndex.TryGetValue(v, out int idx) ? idx : -1;
        public int ResolveLong(long v)
            => _longIndex.TryGetValue(v, out int idx) ? idx : -1;
        public int ResolveDouble(double v)
            => _doubleBitsIndex.TryGetValue(BitConverter.DoubleToInt64Bits(v), out int idx) ? idx : -1;
        public int ResolveFloat(float v)
            => _floatBitsIndex.TryGetValue(BitConverter.SingleToInt32Bits(v), out int idx) ? idx : -1;

        // ----------------------------------------------------------------
        private static void Inc<TKey>(Dictionary<TKey, int> d, TKey k) where TKey : notnull
        {
            if (d.TryGetValue(k, out int n)) d[k] = n + 1;
            else d[k] = 1;
        }

        private static string NumberKey(BigNumber v)
            => v.Unscaled.ToString() + ":" + v.Scale.ToString();

        private static BigNumber ParseNumberKey(string key)
        {
            int sep = key.IndexOf(':');
            if (sep < 0) throw new InvalidDataException("rac: malformed number pool key");
            return new BigNumber(
                BigInteger.Parse(key.Substring(0, sep)),
                BigInteger.Parse(key.Substring(sep + 1)));
        }
    }
}

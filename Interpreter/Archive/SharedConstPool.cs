using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Archive
{
    // v1.1 (#7) → v1.2 (#full-primitive-coverage): archive-level
    // interned constant pool.
    //
    // Every module's ModuleBytecode payload stores its `Consts[]`
    // array; before this section each entry was inlined into the
    // per-module payload. With cross-module sharing, repeated values
    // collapse to one pool entry that every module references by
    // `u32` index.
    //
    // Layout v2 (little-endian):
    //
    //   "SCPL"  u32 magic
    //   u16 version = 2
    //   u16 reserved
    //
    //   v1 buckets (kept for forward compat with v1 readers):
    //     i32 stringCount   + (string * count)
    //     i32 numberCount   + (BigInteger Unscaled, BigInteger Scale) * count
    //     i32 integerCount  + (i32 * count)
    //     i32 longCount     + (i64 * count)
    //     i32 doubleCount   + (u64 bit pattern * count)
    //     i32 floatCount    + (u32 bit pattern * count)
    //
    //   v2 buckets (new):
    //     i32 byteCount     + (u8 * count)
    //     i32 shortCount    + (i16 LE * count)
    //     i32 ushortCount   + (u16 * count)
    //     i32 uintCount     + (u32 * count)
    //     i32 ulongCount    + (u64 * count)
    //     i32 int128Count   + (BigInteger * count)   — 1-byte length + bytes
    //     i32 uint128Count  + (BigInteger * count)
    //     i32 decimalCount  + (i32 p0 + i32 p1 + i32 p2 + i32 p3) * count
    //
    // Pool entries are deduplicated by *content*. Bool stays inline
    // (1-byte tag — no shareable savings). NullValue stays inline.
    //
    // Threshold gate: a value is only pooled when projected wire-byte
    // save exceeds the per-entry overhead. Singleton refs always
    // stay inline.
    public sealed class SharedConstPool
    {
        private const uint Magic = (uint)'S' | ((uint)'C' << 8) | ((uint)'P' << 16) | ((uint)'L' << 24);
        public const ushort Version = 2;
        public const ushort Version_V1 = 1;

        public readonly List<string> Strings = new();
        public readonly List<BigNumber> Numbers = new();
        public readonly List<int> Integers = new();
        public readonly List<long> Longs = new();
        public readonly List<double> Doubles = new();
        public readonly List<float> Floats = new();

        // v2 buckets — full primitive coverage.
        public readonly List<byte> Bytes = new();
        public readonly List<short> Shorts = new();
        public readonly List<ushort> UShorts = new();
        public readonly List<uint> UInts = new();
        public readonly List<ulong> ULongs = new();
        public readonly List<Int128> Int128s = new();
        public readonly List<UInt128> UInt128s = new();
        public readonly List<decimal> Decimals = new();

        public int TotalEntries =>
            Strings.Count + Numbers.Count + Integers.Count
            + Longs.Count + Doubles.Count + Floats.Count
            + Bytes.Count + Shorts.Count + UShorts.Count
            + UInts.Count + ULongs.Count
            + Int128s.Count + UInt128s.Count + Decimals.Count;

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            var w = new RacBinaryWriter(ms);
            w.WriteU32(Magic);
            w.WriteU16(Version);
            w.WriteU16(0);

            // v1 buckets
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

            // v2 buckets
            w.WriteI32(Bytes.Count);
            for (int i = 0; i < Bytes.Count; i++) w.WriteU8(Bytes[i]);

            w.WriteI32(Shorts.Count);
            for (int i = 0; i < Shorts.Count; i++)
            {
                short v = Shorts[i];
                w.WriteU8((byte)(v & 0xFF));
                w.WriteU8((byte)((v >> 8) & 0xFF));
            }

            w.WriteI32(UShorts.Count);
            for (int i = 0; i < UShorts.Count; i++)
            {
                ushort v = UShorts[i];
                w.WriteU8((byte)(v & 0xFF));
                w.WriteU8((byte)((v >> 8) & 0xFF));
            }

            w.WriteI32(UInts.Count);
            for (int i = 0; i < UInts.Count; i++) w.WriteU32(UInts[i]);

            w.WriteI32(ULongs.Count);
            for (int i = 0; i < ULongs.Count; i++) w.WriteU64(ULongs[i]);

            w.WriteI32(Int128s.Count);
            for (int i = 0; i < Int128s.Count; i++)
                ModuleBytecodeIo.WriteBigInteger(w, (BigInteger)Int128s[i]);

            w.WriteI32(UInt128s.Count);
            for (int i = 0; i < UInt128s.Count; i++)
                ModuleBytecodeIo.WriteBigInteger(w, (BigInteger)UInt128s[i]);

            w.WriteI32(Decimals.Count);
            for (int i = 0; i < Decimals.Count; i++)
            {
                int[] parts = decimal.GetBits(Decimals[i]);
                w.WriteI32(parts[0]);
                w.WriteI32(parts[1]);
                w.WriteI32(parts[2]);
                w.WriteI32(parts[3]);
            }

            return ms.ToArray();
        }

        public static SharedConstPool Decode(ReadOnlySpan<byte> payload)
        {
            using var ms = new MemoryStream(payload.ToArray(), writable: false);
            var r = new RacBinaryReader(ms);
            uint magic = r.ReadU32();
            if (magic != Magic) throw new InvalidDataException("rac: SharedConstPool magic mismatch");
            ushort ver = r.ReadU16();
            if (ver != Version && ver != Version_V1)
                throw new InvalidDataException($"rac: SharedConstPool version {ver} not supported");
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

            // v2 buckets only when wire is v2.
            if (ver < Version) return p;

            int byteN = r.ReadI32();
            VerifyCount(byteN);
            for (int i = 0; i < byteN; i++) p.Bytes.Add(r.ReadU8());

            int shN = r.ReadI32();
            VerifyCount(shN);
            for (int i = 0; i < shN; i++)
            {
                byte lo = r.ReadU8();
                byte hi = r.ReadU8();
                p.Shorts.Add(unchecked((short)((hi << 8) | lo)));
            }

            int ushN = r.ReadI32();
            VerifyCount(ushN);
            for (int i = 0; i < ushN; i++)
            {
                byte lo = r.ReadU8();
                byte hi = r.ReadU8();
                p.UShorts.Add((ushort)((hi << 8) | lo));
            }

            int uiN = r.ReadI32();
            VerifyCount(uiN);
            for (int i = 0; i < uiN; i++) p.UInts.Add(r.ReadU32());

            int ulN = r.ReadI32();
            VerifyCount(ulN);
            for (int i = 0; i < ulN; i++) p.ULongs.Add(r.ReadU64());

            int i128N = r.ReadI32();
            VerifyCount(i128N);
            for (int i = 0; i < i128N; i++) p.Int128s.Add((Int128)ModuleBytecodeIo.ReadBigInteger(r));

            int u128N = r.ReadI32();
            VerifyCount(u128N);
            for (int i = 0; i < u128N; i++) p.UInt128s.Add((UInt128)ModuleBytecodeIo.ReadBigInteger(r));

            int decN = r.ReadI32();
            VerifyCount(decN);
            for (int i = 0; i < decN; i++)
            {
                int p0 = r.ReadI32();
                int p1 = r.ReadI32();
                int p2 = r.ReadI32();
                int p3 = r.ReadI32();
                p.Decimals.Add(new decimal(new[] { p0, p1, p2, p3 }));
            }

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
        private readonly Dictionary<string, int> _numberCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<int, int> _integerCounts = new();
        private readonly Dictionary<long, int> _longCounts = new();
        private readonly Dictionary<long, int> _doubleBitsCounts = new();
        private readonly Dictionary<int, int> _floatBitsCounts = new();

        // v2 buckets
        private readonly Dictionary<byte, int> _byteCounts = new();
        private readonly Dictionary<short, int> _shortCounts = new();
        private readonly Dictionary<ushort, int> _ushortCounts = new();
        private readonly Dictionary<uint, int> _uintCounts = new();
        private readonly Dictionary<ulong, int> _ulongCounts = new();
        // Int128 / UInt128 keyed by canonical decimal string — neither
        // has a stock GetHashCode that ties together identical values.
        private readonly Dictionary<string, int> _int128Counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _uint128Counts = new(StringComparer.Ordinal);
        // Decimal uses GetBits four-int decomposition as key (kept as
        // long+long via two pairs). Encode to a (lo64,hi64) key.
        private readonly Dictionary<(long, long), int> _decimalCounts = new();

        // After Finalise, these resolve a value back to its assigned
        // pool index (or -1 == "inline this one").
        private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _numberIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<int, int> _integerIndex = new();
        private readonly Dictionary<long, int> _longIndex = new();
        private readonly Dictionary<long, int> _doubleBitsIndex = new();
        private readonly Dictionary<int, int> _floatBitsIndex = new();
        private readonly Dictionary<byte, int> _byteIndex = new();
        private readonly Dictionary<short, int> _shortIndex = new();
        private readonly Dictionary<ushort, int> _ushortIndex = new();
        private readonly Dictionary<uint, int> _uintIndex = new();
        private readonly Dictionary<ulong, int> _ulongIndex = new();
        private readonly Dictionary<string, int> _int128Index = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _uint128Index = new(StringComparer.Ordinal);
        private readonly Dictionary<(long, long), int> _decimalIndex = new();

        public SharedConstPool Pool { get; } = new();
        public bool Finalised { get; private set; }
        public int Observed { get; private set; }
        public int Pooled { get; private set; }

        // Pass 1: record every const reference.
        public void Observe(RuntimeValue? v)
        {
            if (Finalised)
                throw new InvalidOperationException("SharedConstPoolBuilder: Observe called after Finalise");
            if (v == null) return;
            Observed++;
            switch (v)
            {
                case StringValue s: Inc(_stringCounts, s.Value); break;
                case NumberValue n: Inc(_numberCounts, NumberKey(n.Value)); break;
                case IntegerValue iv: Inc(_integerCounts, iv.Value); break;
                case LongValue lv: Inc(_longCounts, lv.Value); break;
                case DoubleValue dv: Inc(_doubleBitsCounts, BitConverter.DoubleToInt64Bits(dv.Value)); break;
                case FloatValue fv: Inc(_floatBitsCounts, BitConverter.SingleToInt32Bits(fv.Value)); break;
                case ByteValue byv: Inc(_byteCounts, byv.Value); break;
                case ShortValue shv: Inc(_shortCounts, shv.Value); break;
                case UnsignedShortValue ushv: Inc(_ushortCounts, ushv.Value); break;
                case UnsignedIntegerValue uiv: Inc(_uintCounts, uiv.Value); break;
                case UnsignedLongValue ulv: Inc(_ulongCounts, ulv.Value); break;
                case Int128Value i128: Inc(_int128Counts, ((BigInteger)i128.Value).ToString()); break;
                case UnsignedInt128Value u128: Inc(_uint128Counts, ((BigInteger)u128.Value).ToString()); break;
                case DecimalValue dec: Inc(_decimalCounts, DecimalKey(dec.Value)); break;
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
        // payload of N bytes including any length prefix):
        //   inline  : K * (1 tag + N)
        //   pool    : K * (1 tag + 4 idx) + storage
        //   save    : K * (N - 4) - storage
        //
        // For Byte / Short / UShort (1-2 byte payload), N <= 4 so
        // save is always <= 0. They never pool. UInt is exactly 4
        // bytes — net-zero per-ref, net-negative once storage is
        // factored. Same skip.
        //
        // We also amortise the once-per-archive section overhead
        // (magic + version + 14 count headers + section directory
        // entry ~ 150 bytes). If the total projected save doesn't
        // clear that, we abandon the pool.
        private const int SectionOverheadBytes = 150;

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
            // Long / Double — 8-byte fixed payloads.
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

            // v2 buckets. Pool only when the math actually nets a
            // saving. Skip the small-payload buckets (Byte / Short /
            // UShort / UInt) where pool refs are larger than the
            // inline payload itself.

            // ULong — 8-byte fixed payload, same math as Long.
            foreach (var kvp in _ulongCounts)
            {
                int k = kvp.Value;
                if (k < 2) continue;
                int save = 3 * (k - 1) - 8;
                if (save <= 0) continue;
                _ulongIndex[kvp.Key] = Pool.ULongs.Count;
                Pool.ULongs.Add(kvp.Key);
                Pooled++;
                totalSave += save;
            }

            // Int128 / UInt128 — variable BigInteger length+bytes.
            // Inline byte cost = 1 tag + 4 length + bytes. Pool ref =
            // 5 bytes. Storage = 4 + bytes.
            foreach (var kvp in _int128Counts)
            {
                int k = kvp.Value;
                if (k < 2) continue;
                var bi = BigInteger.Parse(kvp.Key);
                int bytes = bi.GetByteCount();
                int save = (4 + bytes) * (k - 1) - (4 + bytes);
                if (save <= 0) continue;
                _int128Index[kvp.Key] = Pool.Int128s.Count;
                Pool.Int128s.Add((Int128)bi);
                Pooled++;
                totalSave += save;
            }
            foreach (var kvp in _uint128Counts)
            {
                int k = kvp.Value;
                if (k < 2) continue;
                var bi = BigInteger.Parse(kvp.Key);
                int bytes = bi.GetByteCount();
                int save = (4 + bytes) * (k - 1) - (4 + bytes);
                if (save <= 0) continue;
                _uint128Index[kvp.Key] = Pool.UInt128s.Count;
                Pool.UInt128s.Add((UInt128)bi);
                Pooled++;
                totalSave += save;
            }

            // Decimal — 16-byte fixed payload (4 ints). Inline =
            // 17 bytes; pool ref = 5 bytes; storage = 16 bytes per value.
            // Save = 12 * (K - 1) - 16. K>=3 → save>=8.
            foreach (var kvp in _decimalCounts)
            {
                int k = kvp.Value;
                if (k < 2) continue;
                int save = 12 * (k - 1) - 16;
                if (save <= 0) continue;
                var dec = DecimalFromKey(kvp.Key);
                _decimalIndex[kvp.Key] = Pool.Decimals.Count;
                Pool.Decimals.Add(dec);
                Pooled++;
                totalSave += save;
            }

            // Integer / Float / Byte / Short / UShort / UInt
            // intentionally never pool — pool refs cost as much or
            // more than inline payloads.

            // Section + directory overhead amortisation. If the total
            // projected save doesn't clear it, abandon the pool so the
            // writer produces an inline-only payload.
            if (totalSave <= SectionOverheadBytes)
            {
                Pool.Strings.Clear();
                Pool.Numbers.Clear();
                Pool.Integers.Clear();
                Pool.Longs.Clear();
                Pool.Doubles.Clear();
                Pool.Floats.Clear();
                Pool.Bytes.Clear();
                Pool.Shorts.Clear();
                Pool.UShorts.Clear();
                Pool.UInts.Clear();
                Pool.ULongs.Clear();
                Pool.Int128s.Clear();
                Pool.UInt128s.Clear();
                Pool.Decimals.Clear();
                _stringIndex.Clear();
                _numberIndex.Clear();
                _integerIndex.Clear();
                _longIndex.Clear();
                _doubleBitsIndex.Clear();
                _floatBitsIndex.Clear();
                _byteIndex.Clear();
                _shortIndex.Clear();
                _ushortIndex.Clear();
                _uintIndex.Clear();
                _ulongIndex.Clear();
                _int128Index.Clear();
                _uint128Index.Clear();
                _decimalIndex.Clear();
                Pooled = 0;
            }
        }

        // Query helpers — return pool index or -1 ("inline").
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
        public int ResolveByte(byte v)
            => _byteIndex.TryGetValue(v, out int idx) ? idx : -1;
        public int ResolveShort(short v)
            => _shortIndex.TryGetValue(v, out int idx) ? idx : -1;
        public int ResolveUShort(ushort v)
            => _ushortIndex.TryGetValue(v, out int idx) ? idx : -1;
        public int ResolveUInt(uint v)
            => _uintIndex.TryGetValue(v, out int idx) ? idx : -1;
        public int ResolveULong(ulong v)
            => _ulongIndex.TryGetValue(v, out int idx) ? idx : -1;
        public int ResolveInt128(Int128 v)
            => _int128Index.TryGetValue(((BigInteger)v).ToString(), out int idx) ? idx : -1;
        public int ResolveUInt128(UInt128 v)
            => _uint128Index.TryGetValue(((BigInteger)v).ToString(), out int idx) ? idx : -1;
        public int ResolveDecimal(decimal v)
            => _decimalIndex.TryGetValue(DecimalKey(v), out int idx) ? idx : -1;

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

        private static (long, long) DecimalKey(decimal v)
        {
            int[] parts = decimal.GetBits(v);
            long lo = ((long)(uint)parts[0]) | (((long)(uint)parts[1]) << 32);
            long hi = ((long)(uint)parts[2]) | (((long)(uint)parts[3]) << 32);
            return (lo, hi);
        }

        private static decimal DecimalFromKey((long lo, long hi) key)
        {
            int p0 = unchecked((int)(uint)(key.lo & 0xFFFFFFFF));
            int p1 = unchecked((int)(uint)((key.lo >> 32) & 0xFFFFFFFF));
            int p2 = unchecked((int)(uint)(key.hi & 0xFFFFFFFF));
            int p3 = unchecked((int)(uint)((key.hi >> 32) & 0xFFFFFFFF));
            return new decimal(new[] { p0, p1, p2, p3 });
        }
    }
}

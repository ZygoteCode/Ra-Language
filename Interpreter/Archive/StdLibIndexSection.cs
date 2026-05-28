using System;
using System.Collections.Generic;
using System.IO;

namespace RaLanguage.Interpreter.Archive
{
    // Wire layout of the `RacSectionKind.StdLibIndex` section payload.
    //
    // v1.0: bare list of dotted std refs (`i32 count + string * count`),
    // emitted by the v1.0 packager. The new v1.1 (#6) packager emits a
    // tagged variant that carries the tree-shake report — kept / dropped
    // symbol lists per std module, plus per-module byte deltas.
    //
    // The tagged variant is recognised by the leading magic `"SLIX"`; a
    // payload that does NOT start with this magic is treated as the
    // bare v1.0 form so old archives keep printing in --inspect-archive.
    //
    //   v1.1 layout
    //     "SLIX"  u32 magic
    //     u16 version = 1
    //     u16 reserved
    //     i32 stdRefCount + string * count
    //     i32 shakenModuleCount
    //       per module:
    //         string logicalPath
    //         i32 bytesBefore
    //         i32 bytesAfter
    //         i32 keptCount + string * keptCount
    //         i32 droppedCount + string * droppedCount
    //
    // The section flag `RacSectionFlags.MustUnderstand` is intentionally
    // OFF: a loader that doesn't recognise the new layout just skips the
    // section. Section is purely informational for the runtime — used
    // only by `--inspect-archive` and offline tooling.
    public static class StdLibIndexSection
    {
        private const uint Magic = (uint)'S' | ((uint)'L' << 8) | ((uint)'I' << 16) | ((uint)'X' << 24);
        private const ushort Version = 1;

        public sealed class Decoded
        {
            public List<string> StdReferences = new();
            public List<ShakenModule> ShakenModules = new();
            // True when the payload was in tagged v1.1 form. False on
            // a v1.0 bare payload (still decoded into StdReferences).
            public bool HasShakeReport;
        }

        public sealed class ShakenModule
        {
            public string Path = "";
            public int BytesBefore;
            public int BytesAfter;
            public List<string> Kept = new();
            public List<string> Dropped = new();
        }

        public static byte[] EncodeTagged(IEnumerable<string> stdRefs, IEnumerable<ShakenModule> shaken)
        {
            using var ms = new MemoryStream();
            var w = new RacBinaryWriter(ms);
            w.WriteU32(Magic);
            w.WriteU16(Version);
            w.WriteU16(0);

            var stdList = new List<string>();
            foreach (var s in stdRefs) stdList.Add(s);
            w.WriteI32(stdList.Count);
            foreach (var s in stdList) w.WriteString(s);

            var shakenList = new List<ShakenModule>();
            foreach (var s in shaken) shakenList.Add(s);
            w.WriteI32(shakenList.Count);
            foreach (var m in shakenList)
            {
                w.WriteString(m.Path);
                w.WriteI32(m.BytesBefore);
                w.WriteI32(m.BytesAfter);
                w.WriteI32(m.Kept.Count);
                foreach (var n in m.Kept) w.WriteString(n);
                w.WriteI32(m.Dropped.Count);
                foreach (var n in m.Dropped) w.WriteString(n);
            }
            return ms.ToArray();
        }

        public static byte[] EncodeBare(IEnumerable<string> stdRefs)
        {
            using var ms = new MemoryStream();
            var w = new RacBinaryWriter(ms);
            var list = new List<string>();
            foreach (var s in stdRefs) list.Add(s);
            w.WriteI32(list.Count);
            foreach (var s in list) w.WriteString(s);
            return ms.ToArray();
        }

        public static Decoded Decode(ReadOnlySpan<byte> payload)
        {
            using var ms = new MemoryStream(payload.ToArray(), writable: false);
            var r = new RacBinaryReader(ms);
            var result = new Decoded();
            if (payload.Length < 4)
            {
                return result; // empty
            }
            // Peek at the first u32. If it matches the tagged magic we
            // decode v1.1; otherwise the first u32 is the bare count
            // field — interpret as bare v1.0.
            long start = r.Position;
            uint head = r.ReadU32();
            if (head == Magic)
            {
                ushort ver = r.ReadU16();
                if (ver != Version)
                    throw new InvalidDataException($"rac: StdLibIndex tagged version {ver} not supported");
                ushort reserved = r.ReadU16();
                if (reserved != 0)
                    throw new InvalidDataException("rac: StdLibIndex tagged reserved must be zero");
                int refCount = r.ReadI32();
                if (refCount < 0 || refCount > 1_000_000)
                    throw new InvalidDataException($"rac: bogus std-ref count {refCount}");
                for (int i = 0; i < refCount; i++) result.StdReferences.Add(r.ReadString() ?? "");
                int modCount = r.ReadI32();
                if (modCount < 0 || modCount > 1_000_000)
                    throw new InvalidDataException($"rac: bogus shaken-module count {modCount}");
                for (int i = 0; i < modCount; i++)
                {
                    var m = new ShakenModule { Path = r.ReadString() ?? "" };
                    m.BytesBefore = r.ReadI32();
                    m.BytesAfter = r.ReadI32();
                    int keptN = r.ReadI32();
                    for (int k = 0; k < keptN; k++) m.Kept.Add(r.ReadString() ?? "");
                    int dropN = r.ReadI32();
                    for (int k = 0; k < dropN; k++) m.Dropped.Add(r.ReadString() ?? "");
                    result.ShakenModules.Add(m);
                }
                result.HasShakeReport = true;
                return result;
            }
            // Bare v1.0 form: head is the count itself.
            int count = (int)head;
            if (count < 0 || count > 1_000_000)
                throw new InvalidDataException($"rac: bogus std-ref count {count}");
            for (int i = 0; i < count; i++) result.StdReferences.Add(r.ReadString() ?? "");
            return result;
        }
    }
}

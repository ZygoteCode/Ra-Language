using System;
using System.Collections.Generic;
using System.IO;

namespace RaLanguage.Interpreter.Archive
{
    // ModuleKind classifies each bundled module. Knowing the kind lets
    // the loader pick the right resolution strategy when reconstructing
    // the import graph.
    public enum RacModuleKind : byte
    {
        // Plain project file referenced via string-literal import.
        Project    = 1,

        // Standard library file (resolved through the dotted "std.x.y"
        // path at build time).
        StdLib     = 2,

        // The entry module. Exactly one per archive.
        Entry      = 3,
    }

    public sealed class RacModuleRecord
    {
        // 0-based index into RacManifest.Modules. Stable for the
        // archive's lifetime; used by ImportEdges to identify
        // targets without embedding full paths.
        public int Index;

        // Logical path used by the importer (`"./greet.ra"` or
        // `"std.io"`). Used only for diagnostics — resolution at
        // load time is by virtual absolute path.
        public string LogicalPath = "";

        // Absolute path computed at build time, normalised to
        // forward slashes. The virtual filesystem keys off this
        // exact string, and the loader rewrites a per-run base
        // directory in front of it to avoid path collisions
        // across multiple archives loaded by the same process.
        public string AbsoluteVirtualPath = "";

        // Index into RacManifest.Sections — points at the
        // ModuleSource section that carries this module's UTF-8
        // source. -1 if the source is not bundled (signature-only
        // mode, future use).
        public int SourceSectionIndex;

        // Index into the section directory for the future
        // ModuleBytecode payload. -1 in v1.
        public int BytecodeSectionIndex;

        // SHA-256 of the raw source bytes (utf-8). Lets a downstream
        // tool diff archives without decompressing.
        public byte[] SourceHash = Array.Empty<byte>();

        public RacModuleKind Kind;

        // 0-based indices of every module this one imports. Computed
        // at build time by walking the AST. The order in which an
        // importer's transitive dependencies appear in this list is
        // not load-bearing: the loader uses the manifest only for
        // path resolution; actual execution order falls out of the
        // ImportNodeVisitor at runtime, exactly as for a normal
        // disk-based run.
        public List<int> Imports = new();
    }

    // The binary manifest is itself stored *inside* the Manifest
    // section payload (so it benefits from compression and a SHA-256
    // hash like every other section). The wire format is:
    //
    //   "MNFS"                                magic, u32 packed
    //   manifestVersion: u16                  = 1
    //   reserved:        u16                  = 0
    //   entryModuleIndex: i32
    //   moduleCount:     i32
    //     per module:
    //       index:                 i32        (must equal position)
    //       kind:                  u8
    //       reserved:              [3]u8
    //       logicalPath:           string
    //       absoluteVirtualPath:   string
    //       sourceSectionIndex:    i32
    //       bytecodeSectionIndex:  i32
    //       sourceHash:            [32]u8
    //       importCount:           i32
    //         each: i32
    //   stdRefCount:    i32
    //     per ref: string
    //   buildTimeTicks: i64                   UTC ticks at build time
    //   buildHost:      string                machine name (informational)
    //   builtBy:        string                "ralang 1.0.0" or similar
    //   tail magic:     "/MFS" u32 packed
    //
    // The wrapped section blob is preceded by no other framing.
    public sealed class RacManifest
    {
        public int EntryModuleIndex;
        public List<RacModuleRecord> Modules = new();

        // Names of standard-library symbols / dotted modules
        // referenced from somewhere in the program. v1 does not
        // act on this — the C# builtin table lives in the
        // interpreter binary — but it is the right shape for a
        // future tree-shake / link diagnostic.
        public List<string> StdReferences = new();

        public long BuildTimeTicks;
        public string BuildHost = "";
        public string BuiltBy   = "";

        // The set of section directory entries known at the time the
        // manifest was finalised. Only populated by the writer; the
        // reader carries this through the RacArchive wrapper.
        public List<RacSectionEntry> Sections = new();

        private static readonly uint MagicHead = MakeMagic('M', 'N', 'F', 'S');
        private static readonly uint MagicTail = MakeMagic('/', 'M', 'F', 'S');

        private static uint MakeMagic(char a, char b, char c, char d)
            => (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            var w = new RacBinaryWriter(ms);
            w.WriteU32(MagicHead);
            w.WriteU16(1); // manifestVersion
            w.WriteU16(0); // reserved
            w.WriteI32(EntryModuleIndex);

            w.WriteI32(Modules.Count);
            for (int i = 0; i < Modules.Count; i++)
            {
                var m = Modules[i];
                if (m.Index != i)
                    throw new InvalidOperationException(
                        $"rac: module index mismatch at slot {i} (record carries {m.Index})");
                w.WriteI32(m.Index);
                w.WriteU8((byte)m.Kind);
                w.WriteU8(0); w.WriteU8(0); w.WriteU8(0); // reserved
                w.WriteString(m.LogicalPath);
                w.WriteString(m.AbsoluteVirtualPath);
                w.WriteI32(m.SourceSectionIndex);
                w.WriteI32(m.BytecodeSectionIndex);
                if (m.SourceHash.Length != RacFormat.HashSize)
                    throw new InvalidOperationException("rac: source hash must be 32 bytes");
                w.WriteBytes(m.SourceHash);
                w.WriteI32(m.Imports.Count);
                foreach (var imp in m.Imports) w.WriteI32(imp);
            }

            w.WriteI32(StdReferences.Count);
            foreach (var s in StdReferences) w.WriteString(s);

            w.WriteI64(BuildTimeTicks);
            w.WriteString(BuildHost);
            w.WriteString(BuiltBy);
            w.WriteU32(MagicTail);
            return ms.ToArray();
        }

        public static RacManifest Deserialize(ReadOnlySpan<byte> data)
        {
            using var ms = new MemoryStream(data.ToArray(), writable: false);
            var r = new RacBinaryReader(ms);
            uint head = r.ReadU32();
            if (head != MagicHead) throw new InvalidDataException("rac: manifest magic mismatch");
            ushort ver = r.ReadU16();
            if (ver != 1) throw new InvalidDataException($"rac: unknown manifest version {ver}");
            ushort reserved = r.ReadU16();
            if (reserved != 0) throw new InvalidDataException("rac: manifest reserved must be zero");

            var m = new RacManifest
            {
                EntryModuleIndex = r.ReadI32()
            };
            int moduleCount = r.ReadI32();
            if (moduleCount < 0 || moduleCount > 1_000_000)
                throw new InvalidDataException($"rac: bogus module count {moduleCount}");
            for (int i = 0; i < moduleCount; i++)
            {
                var record = new RacModuleRecord
                {
                    Index = r.ReadI32(),
                    Kind = (RacModuleKind)r.ReadU8(),
                };
                if (record.Index != i)
                    throw new InvalidDataException(
                        $"rac: manifest module index mismatch at {i} (got {record.Index})");
                r.ReadU8(); r.ReadU8(); r.ReadU8(); // reserved
                record.LogicalPath = r.ReadString() ?? "";
                record.AbsoluteVirtualPath = r.ReadString() ?? "";
                record.SourceSectionIndex = r.ReadI32();
                record.BytecodeSectionIndex = r.ReadI32();
                record.SourceHash = r.ReadBytes(RacFormat.HashSize);
                int impCount = r.ReadI32();
                if (impCount < 0 || impCount > 1_000_000)
                    throw new InvalidDataException($"rac: bogus import count {impCount}");
                for (int j = 0; j < impCount; j++) record.Imports.Add(r.ReadI32());
                m.Modules.Add(record);
            }

            int stdCount = r.ReadI32();
            if (stdCount < 0 || stdCount > 1_000_000)
                throw new InvalidDataException($"rac: bogus std-ref count {stdCount}");
            for (int i = 0; i < stdCount; i++) m.StdReferences.Add(r.ReadString() ?? "");

            m.BuildTimeTicks = r.ReadI64();
            m.BuildHost = r.ReadString() ?? "";
            m.BuiltBy   = r.ReadString() ?? "";
            uint tail = r.ReadU32();
            if (tail != MagicTail) throw new InvalidDataException("rac: manifest tail magic mismatch");

            // Sanity: entry index must be in range when modules > 0.
            if (m.Modules.Count > 0 &&
                (m.EntryModuleIndex < 0 || m.EntryModuleIndex >= m.Modules.Count))
                throw new InvalidDataException(
                    $"rac: entry module index out of range ({m.EntryModuleIndex} of {m.Modules.Count})");

            return m;
        }
    }
}

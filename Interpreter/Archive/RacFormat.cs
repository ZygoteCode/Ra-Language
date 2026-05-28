using System;

namespace RaLanguage.Interpreter.Archive
{
    // Format-level constants for the Ra Archive Container (".rac").
    //
    // The on-disk layout is a tiny, fixed-size FileHeader followed by an
    // arbitrary set of variable-length sections, addressable through a
    // central section directory. Every section carries its own SHA-256
    // hash and (optional) compression tag. The header carries a hash of
    // the directory itself.
    //
    // Versioning is split across three independent axes so the format
    // and the runtime can evolve at different rates:
    //
    //   * FormatMajor / FormatMinor — the *binary layout*. A loader for
    //     FormatMajor=N MUST refuse to load FormatMajor=N+1. FormatMinor
    //     differences are forward-permissive: a newer minor adds
    //     sections / flags the older loader is free to ignore as long
    //     as no MustUnderstand flag is set.
    //   * RuntimeRequired — minimum Ra interpreter version that must be
    //     present to safely execute the archive. Independent of format.
    //   * RuntimeRecommended — informative: hint for warning messages.
    //
    // See RA_RAC_FORMAT.md for the prose specification.
    public static class RacFormat
    {
        // ASCII "RA" + "C\0" + "RAC\x01" — 8 bytes, fixed.
        // Picked so the first 4 bytes spell "RAC\0" (recognisable in
        // hex dumps) and the next 4 carry a self-identifying signature
        // that survives accidental newline / encoding mangling.
        public static readonly byte[] Magic = new byte[]
        {
            (byte)'R', (byte)'A', (byte)'C', 0x00,
            (byte)'R', (byte)'A', (byte)'C', 0x01,
        };

        // Bump FormatMajor on any breaking change to the on-disk layout
        // (e.g. wider section table, different hash algorithm, removed
        // field). Bump FormatMinor on additive changes (new section
        // kinds, new flag bits) that older loaders can skip.
        public const ushort FormatMajor = 1;
        // 1.1 (M1): optional ModuleBytecode sections. v1.0 archives keep
        // loading unchanged — the minor-version bump is purely additive.
        public const ushort FormatMinor = 1;

        // Ra runtime semver packed as (major:8 | minor:8 | patch:16).
        // The tree-walker pre-VM era is treated as 0.x; the current
        // IR+VM line is 1.x. Patch is fine-grained enough for
        // backwards-compatibility checks at archive load.
        public const uint RaRuntimeVersion = (1u << 24) | (0u << 16) | 0u;

        // Header byte size — locked. Future versions add fields by
        // bumping FormatMajor (not by widening this struct in place).
        public const int FileHeaderSize = 96;

        // Section directory entry byte size — locked.
        public const int SectionEntrySize = 64;

        // Hash length: SHA-256 produces 32 bytes.
        public const int HashSize = 32;

        // Reasonable upper bound on a single archive, used by the
        // loader to refuse pathological inputs early. 4 GiB is the
        // theoretical u64 ceiling; we cap well below that.
        public const long MaxArchiveSize = 1L << 32; // 4 GiB

        // Cap a single section at 256 MiB so a malformed `storedSize`
        // can't be used to allocate a giant buffer before we even
        // start to read.
        public const long MaxSectionSize = 256L * 1024 * 1024;

        // Buffer size used for streaming SHA-256 + compression.
        public const int IoBufferSize = 64 * 1024;
    }

    // Section kind tag. Lives in the directory entry; values are stable
    // across the lifetime of FormatMajor=1.
    //
    // Unknown kinds are tolerated unless the entry carries
    // `RacSectionFlags.MustUnderstand` — in which case the loader
    // refuses the archive.
    public enum RacSectionKind : uint
    {
        // Manifest is REQUIRED. Carries the module list, entry, and
        // dependency graph. Always the first section to be loaded.
        Manifest        = 0x00000001,

        // ModuleSource — UTF-8 source text of a single .ra module.
        // The manifest cross-references each module by its directory
        // index. Required for v1 execution (the loader re-parses).
        ModuleSource    = 0x00000002,

        // ModuleBytecode — RESERVED for FormatMinor >= 1. Will carry
        // a serialised RaFunction tree (plus AST snapshot) so the
        // loader can skip lex/parse/IR-compile entirely. The format
        // is designed so this section can be added later without
        // breaking v1 loaders, who will just ignore it.
        ModuleBytecode  = 0x00000003,

        // DebugInfo — RESERVED. Source maps, line tables, name maps
        // for production diagnostics on stripped builds.
        DebugInfo       = 0x00000004,

        // StdLibIndex — names of standard-library symbols the program
        // actually references. Informational for now (the C# builtin
        // registry is shipped with the interpreter binary); future
        // tree-shaking can use this for diagnostics.
        StdLibIndex     = 0x00000005,

        // Signature — RESERVED. Detached signature over the archive
        // hash (ed25519 / RSA-PSS).
        Signature       = 0x00000006,

        // SharedConstPool — v1.1 (#7). Archive-level interned pools of
        // strings / BigNumbers / int / long / double / float values
        // shared across every module's ModuleBytecode Consts[] array.
        // Modules' const tags reference this pool by u32 index, so the
        // same string literal appearing in N modules costs one pool
        // slot plus N×5-byte references instead of N inline copies.
        // Not MustUnderstand: a loader without v1.1 (#7) support that
        // somehow saw a v2 ModuleBytecode payload would still error,
        // but PayloadVersion is the gate there — this section is just
        // the pool storage.
        SharedConstPool = 0x00000007,

        // Custom — user-defined sections. Loaders MUST skip unless
        // they explicitly opt in via a tool that understands the
        // embedded sub-format.
        Custom          = 0xFFFFFFFFu,
    }

    [Flags]
    public enum RacFlags : uint
    {
        None              = 0,

        // The payload of EVERY section is Deflate-compressed (raw,
        // RFC 1951 — no gzip / zlib header). Hashes are computed over
        // the *uncompressed* bytes so identity survives a recompress.
        Compressed        = 1u << 0,

        // Detached signature is present in a Signature section. Not
        // exercised by v1 but the bit is reserved so format-major
        // does not need to change when we ship signing.
        Signed            = 1u << 1,

        // The archive carries DebugInfo sections. Strippable: an
        // archive with this bit cleared has no debug payload at all.
        HasDebug          = 1u << 2,

        // The archive was produced with optimisations disabled.
        // Surfaces in diagnostics ("debug build, slow path").
        DebugBuild        = 1u << 3,
    }

    [Flags]
    public enum RacSectionFlags : uint
    {
        None              = 0,

        // The section content is Deflate-compressed. Independent of
        // the archive-wide Compressed flag — a writer may compress
        // some sections and leave others raw (e.g. signature blobs
        // should not be compressed).
        Compressed        = 1u << 0,

        // The loader MUST understand this section kind. Set on
        // sections whose absence breaks execution (anything the
        // runtime *consumes*, vs sections only tools care about).
        MustUnderstand    = 1u << 1,
    }
}

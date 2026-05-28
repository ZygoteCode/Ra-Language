# Ra Archive Container (`.rac`)

> Status: **v1.1** (FormatMajor = 1, FormatMinor = 1). Stable on-disk
> layout; v1.1 adds optional `ModuleBytecode` sections. v1.0 archives
> continue to load unchanged; v1.0 loaders skip the new sections
> silently (they are not `MustUnderstand`).

The `.rac` file is the canonical distributable form of a Ra program: a
single, integrity-checked, optionally-compressed binary archive that
bundles the entry module, every transitively-imported module and the
metadata the runtime needs to load and execute the program without
touching the original source tree.

This document is the normative format specification and a working
overview of the build / load pipeline. The reference implementation
lives under [`Interpreter/Archive/`](Interpreter/Archive).

## Goals

* **One file is one program.** A `.rac` is a self-contained image —
  no external manifest, lockfile, vendor dir or build artefact is
  required at run time.
* **Integrity-checked.** Each section carries a SHA-256 over its
  *uncompressed* content; the section directory itself is hashed in
  the file header. Any single-bit tamper is detected at load.
* **NativeAOT-safe.** No reflection, no runtime codegen, no
  `BinaryFormatter`, no managed serialisation framework — just
  hand-rolled little-endian primitives over `Stream`. The same
  archive loads identically on Windows, Linux and macOS.
* **Versioned on two axes.** *Format* version (binary layout) and
  *runtime* version (interpreter capabilities) evolve independently.
  Older loaders refuse newer-major archives; newer loaders accept
  older-minor archives.
* **Forward-compatible.** Sections are tagged by `Kind`. Unknown
  kinds are tolerated unless they carry the `MustUnderstand` flag,
  so future payload upgrades (bytecode, signatures, debug info)
  can land without breaking older loaders.
* **Cheap to load.** The header + manifest are validated up front;
  per-module source payloads are read on demand. A trivial archive
  loads in well under a millisecond.

## File layout

```
+---------------------------+  offset 0
|  FileHeader  (96 bytes)   |
+---------------------------+  offset 96
|  Section payload 0        |  e.g. Manifest
+---------------------------+
|  Section payload 1        |  e.g. ModuleSource (entry)
+---------------------------+
|  Section payload 2        |  e.g. ModuleSource (dep)
+---------------------------+
|  ...                      |
+---------------------------+  offset = SectionTableOffset
|  Section directory        |
|    SectionCount entries   |
|    64 bytes each          |
+---------------------------+  EOF
```

All multi-byte fields are **little-endian**. Strings are
`[u32 byteLen][utf-8 bytes]` with `0xFFFF_FFFF` reserved for `null`.

### FileHeader (96 bytes)

| Offset | Size | Field                |
| -----: | ---: | -------------------- |
|     0  |    8 | `Magic` = `52 41 43 00 52 41 43 01` (`"RAC\0RAC\x01"`) |
|     8  |    2 | `FormatMajor` u16    |
|    10  |    2 | `FormatMinor` u16    |
|    12  |    4 | `Flags` u32 (see `RacFlags`) |
|    16  |    4 | `RaRuntimeRequired` packed semver |
|    20  |    4 | `RaRuntimeBuiltWith` packed semver |
|    24  |    4 | `SectionCount` u32   |
|    28  |    4 | reserved (must be `0`) |
|    32  |    8 | `SectionTableOffset` u64 |
|    40  |    8 | `ManifestOffset` u64 |
|    48  |   32 | `DirectoryHash` = SHA-256 over section directory bytes |
|    80  |   16 | reserved (zeros) |

A loader **must** validate `Magic`, then `FormatMajor`, then
`RaRuntimeRequired` against the host interpreter, then
`DirectoryHash` against the on-disk section directory — *before*
trusting any other field.

### Section directory entry (64 bytes)

| Offset | Size | Field                |
| -----: | ---: | -------------------- |
|     0  |    4 | `Kind` u32 (see `RacSectionKind`) |
|     4  |    4 | `Flags` u32 (see `RacSectionFlags`) |
|     8  |    8 | `Offset` u64 — byte offset of payload start |
|    16  |    8 | `StoredSize` u64 — bytes in file, post-compression |
|    24  |    8 | `UncompressedSize` u64 — logical content size |
|    32  |   32 | `Hash` SHA-256 over the **uncompressed** payload |

Compression is per-section. The archive-wide `Compressed` flag is a
hint for tooling; the authoritative per-section state lives in
`Flags & RacSectionFlags.Compressed`. Hashes are over the
uncompressed bytes so re-compression with a different level does
not invalidate identity.

### Section kinds

| Kind         | Value      | Status             | Purpose                                   |
| ------------ | ---------- | ------------------ | ----------------------------------------- |
| Manifest     | `0x01`     | required           | Module list, entry, dependency graph      |
| ModuleSource | `0x02`     | required           | UTF-8 source of a single `.ra` module     |
| ModuleBytecode | `0x03`   | optional (v1.1)    | Serialised `RaFunction` + AST snapshot    |
| DebugInfo    | `0x04`     | reserved           | Source maps, line tables                  |
| StdLibIndex  | `0x05`     | informational      | Std refs + v1.1 tree-shake report         |
| Signature    | `0x06`     | reserved           | Detached signature payload                |
| SharedConstPool | `0x07`  | optional (v1.1)    | Archive-level interned const pool         |
| Custom       | `0xFFFFFFFF` | extension        | User-defined; loader skips unless opted-in |

`MustUnderstand` sections of an unknown `Kind` cause the loader to
refuse the archive. Unknown kinds without that flag are skipped
silently — that is the migration seam for new payload types.

### Manifest (binary)

The Manifest section's content is a self-contained binary record:

```
"MNFS"  u32 magic
manifestVersion: u16  = 1
reserved:        u16  = 0
entryModuleIndex: i32
moduleCount:      i32
   per module:
     index:               i32  (must equal position)
     kind:                u8   (Project / StdLib / Entry)
     reserved:            [3]u8
     logicalPath:         string
     absoluteVirtualPath: string
     sourceSectionIndex:  i32
     bytecodeSectionIndex: i32
     sourceHash:          [32]u8
     importCount:         i32
       each: i32  (target module index)
stdRefCount: i32
   each: string
buildTimeTicks: i64  UTC ticks
buildHost:      string
builtBy:        string
"/MFS"  u32 tail magic
```

The manifest is the **single source of truth** for dependency
resolution at load time. The runtime does not re-discover imports
from disk; it consults the manifest and reads the bundled module
source(s) from the corresponding `ModuleSource` sections.

## Build pipeline (compile → archive)

Entry: `dotnet run -- --compile <entry.ra> [-o out.rac] [--no-compress] [--verbose]`

Implementation: `Interpreter/Archive/RacPackager.cs`.

1. Resolve the entry to an absolute path. Reject non-`.ra` files.
2. Lex + parse the entry. Surface diagnostics at build time.
3. Walk top-level statements for `ImportAll` / `ImportSelective` /
   `ImportAlias`. For each, resolve through `ModuleResolver`,
   enqueue the target, and record an edge in the import graph.
4. Repeat transitively across every reached module. Cycles fall
   out naturally (each module enters the work queue at most once).
5. For each reached module, compute the SHA-256 of its source.
6. Emit the archive:
   * Add a `Manifest` section with the full module list and
     dependency edges.
   * Add one `ModuleSource` section per reached module.
   * Add an informational `StdLibIndex` section if any dotted
     `std.*` imports were observed.
7. Back-patch the header (offsets, directory hash) and flush.

The packager **does not execute user code at build time**. Imports
are graph edges, not function calls. Builds are deterministic and
side-effect free.

## Load pipeline (`.rac` → execute)

Entry: `dotnet run -- --run-archive <file.rac>` or simply
`dotnet run -- foo.rac`.

Implementation: `Interpreter/Archive/RacRunner.cs`.

1. `RacReader.Open` validates the header, runtime-version gate,
   and section directory hash. v1.1 (#4): file-backed archives are
   opened via `MemoryMappedFile` (`Interpreter/Archive/RacSource.cs::MappedRacSource`).
   Only the header + section directory are page-faulted in eagerly
   — everything else stays unread until a `ReadSection(index)` call
   maps a per-call view over the section payload. Steady-state
   open time is ~130 microseconds on a Windows host (1 KB to 1 MB
   archives are indistinguishable — the cost is dominated by the
   mmap + manifest decode, not by the total archive length). The
   `--bench-archive-open <file.rac> [iter]` CLI flag exposes this
   measurement.
2. The Manifest section is decompressed and verified
   eagerly; per-source sections lazily on first access.
3. For every module in the manifest the runner maps the
   archive-recorded `AbsoluteVirtualPath` to a host-side virtual
   path under `<TEMP>/ra-rac/<runId>/...`. No files are written
   to disk — only the path string is constructed, used as a key
   into `VirtualFs`.
4. Each module's source bytes are decompressed, hash-verified
   against both the section hash and the manifest's per-module
   `SourceHash` (defence in depth), and mounted into `VirtualFs`.
5. The standard runtime symbol table is re-initialised
   (`Program.InitializeSymbolTable`). The module manager is
   re-rooted at the virtual entry directory so relative imports
   resolve inside the overlay.
6. `Program.Run(entryPath, entrySource)` drives the same
   lex → parse → derive → resolve → analyse → IR compile → VM
   pipeline used for ordinary disk-based runs. Because
   `ModuleResolver` and `ModuleManager` consult `VirtualFs` before
   the disk, every `import "./x.ra"` hits a bundled overlay entry.
7. On exit the overlay is cleared so subsequent direct runs in the
   same process see a clean filesystem.

### Why this payload shape (v1)

The Ra IR carries live AST references inside every compiled
`RaFunction` (cast sites, member-access sites, type-of sites,
function-definition sites, etc.). Serialising the bytecode without
those references is straightforward, but rebuilding *only* the
referenced AST nodes from a sidecar binary requires a stable
serialiser for ~75 AST node types — a multi-week migration. V1
ships the program as **source + manifest + dependency graph**,
where the manifest skips the import-discovery work that a disk
load would otherwise repeat.

### v1.1 — `ModuleBytecode` payload

v1.1 lands the direct-bytecode payload. Per-module sections of
kind `ModuleBytecode` (`0x03`) carry the serialised `RaFunction`
tree plus an AST snapshot for `AstRefs[]` / `CastRefs[]` /
`MemberAccessRefs[]` / `FuncDefRefs[]` / etc. When present, the
loader feeds the deserialised `RaFunction` straight into
`VmExecutor` and skips lex/parse/IR-compile for the entry module.

Wire format of the section payload (little-endian):

```
"RAFB"                u32 magic
formatVersion: u16    = 1
reserved:      u16    = 0

RaFunction:
  Name:                string
  FrameId:             i32
  LocalCount:          i32
  Arity:               i32
  ParamFlags:          u8
  SlotCount:           i32
  UsesUnboxedSlots:    u8 (bool)
  HasImports:          u8 (bool)
  Code:                i32 length + u32 * length
  Consts:              i32 length + tagged RuntimeValue * length
  Names:               i32 length + string * length
  EhTable:             i32 length + (i32,i32,i32,i32,u8,i32) * length
  Upvalues:            i32 length + (u8,u16) * length
  SlotNames:           i32 length + string? * length
  PcSpans:             u8 hasPc + (i32, i32*n, SourceSpan*n)?
  DeclSlotByAstRef:    i32 length + i32 * length
  MutatedNames:        u8 hasSet + (i32, string*n)?
  AstRefs / CastRefs / MemberAccessRefs / MemberAssignRefs /
  ListAssignRefs / EnumAccessRefs / TypeofRefs / NameofRefs /
  DerefRefs / SuperRefs / FuncDefRefs / DefineRefs:
    i32 length + AstNode * length    (polymorphic)
```

Inline caches (`LoadGlobalIc`, `EnumAccessIc`, `CastIc`,
`MemberAccessIc`, `CallMethodIc`) and the IR analysis bundle are
intentionally **not** serialised — they carry live `SymbolTable` /
shape references and re-prime on the first execution after load.

Coverage is incremental. The AST serialiser supports the common
nodes that appear in straight-line scripts (primitives, control
flow, function definitions / calls, imports, collections, member
access, casts, etc.). When the packager meets an AST node or
runtime value it cannot persist, it emits a build-time warning
and drops the bytecode section for that module — the runner sees
`BytecodeSectionIndex == -1` and falls back to the v1.0 source
path automatically. The minor-version bump is purely additive:
v1.0 loaders skip the new section kind silently (not
`MustUnderstand`).

## Versioning

* **FormatMajor / FormatMinor.** Layout version. Older loaders refuse
  newer-major archives. Older loaders skip unknown sections of newer
  *minors* unless `MustUnderstand` is set.
* **RaRuntimeRequired.** Minimum interpreter the archive needs to
  execute. Loaders refuse archives whose required version exceeds
  their own — `RacFormat.RaRuntimeVersion`.
* **RaRuntimeBuiltWith.** Informational. The interpreter that built
  the archive. Surfaces in `--inspect-archive`.

`RacHeader.CompareSemver` performs the field-wise comparison.
Versions are packed `(major:8 | minor:8 | patch:16)`.

## Integrity model

* The **file header** carries a SHA-256 over the section directory.
  A loader rejects any archive whose computed directory hash does
  not match the header.
* Every **section directory entry** carries a SHA-256 over the
  uncompressed payload. The Manifest hash is verified eagerly; all
  other sections are verified on read.
* The **manifest** carries a per-module SHA-256 over the original
  source bytes. The runner verifies this hash *again* on top of the
  section hash for every loaded module — so a manifest swap that
  re-targets a module to a different (but otherwise-valid) source
  section is caught.
* The format reserves `Signature` for detached cryptographic
  signatures. Not yet exercised but the bit and section kind are
  allocated.

## CLI surface

| Command                                                  | Action                                  |
| -------------------------------------------------------- | --------------------------------------- |
| `--compile <entry.ra> [-o out.rac] [--no-compress] [--no-tree-shake] [--no-const-pool]` | Build a `.rac` from a source entry.     |
| `--run-archive <file.rac>`                               | Load and execute a `.rac`.              |
| `--inspect-archive <file.rac>`                           | Pretty-print header + manifest + sections + shake report. |
| `--bench-archive-open <file.rac> [iter]`                 | Time `RacReader.Open` over N iterations (default 1000). |
| `--dump-archive-source <file.rac> <module-idx>`          | Print a bundled module's source (post-tree-shake). |
| `<file.rac>` (positional)                                | Auto-detected as `--run-archive`.       |

Existing CLI flags (`--bench`, `--dump-ir`, `--dump-cfg`, `--repl`,
positional `.ra` path) are unchanged.

## What lives where

```
Interpreter/Archive/
  RacFormat.cs        magic, version constants, kind / flag enums
  RacHeader.cs        96-byte file header (read / write / validate)
  RacSection.cs       64-byte directory entry
  RacBinaryStream.cs  little-endian binary reader / writer
  RacIntegrity.cs     SHA-256 helpers (HashData / streaming / equal)
  RacManifest.cs      binary manifest type + (de)serialiser
  RacWriter.cs        archive builder (lay out sections, back-patch header)
  RacReader.cs        archive loader (validate, lazy section access)
  RacArchive.cs       decoded view (header + sections + manifest)
  RacPackager.cs      source-tree → .rac pipeline
  RacRunner.cs        .rac → VM execution pipeline
  RacInspector.cs     pretty-printer for --inspect-archive
  VirtualFs.cs        process-wide source overlay (used by ModuleResolver/Manager)
  ModuleBytecodeIo.cs v1.1 RaFunction (de)serialiser
  AstNodeSerializer.cs v1.1 polymorphic AstNode (de)serialiser
  RacSource.cs        v1.1 (#4) RacSource / MappedRacSource (mmap) / StreamRacSource
  StdLibTreeShaker.cs v1.1 (#6) tree-shake bundled std modules
  StdLibIndexSection.cs v1.1 (#6) tagged StdLibIndex payload (shake report)
  SharedConstPool.cs  v1.1 (#7) archive-level interned constant pool
```

## Smoke tests

* [`tests_rac/hello.ra`](tests_rac/hello.ra) — single-file, exercises
  arithmetic, `while`, function declaration. Round-trips through
  `--compile` → `--inspect-archive` → `--run-archive`.
* [`tests_rac/multi/entry.ra`](tests_rac/multi/entry.ra) +
  [`tests_rac/multi/mathy.ra`](tests_rac/multi/mathy.ra) — two-module
  archive proving relative-import resolution against the in-memory
  overlay.
* `tests_lambdas.ra` packaged via `--compile`, run via
  `--run-archive`. The interpreter's existing 30-case lambda suite
  passes verbatim from the archive.

Negative paths exercised:

* Bad magic → "not a Ra archive (bad magic)".
* Single-byte tamper → "section payload hash mismatch (kind X)".
* `FormatMajor` mismatch → "incompatible format version Y.x".
* Missing archive file → "archive not found".

### v1.1 — Tree-shaking the bundled stdlib (#6)

`StdLibIndex` (section kind `0x05`) is no longer a passive list of
dotted refs. v1.1's packager walks every parsed module to gather every
identifier-name reference, then for each module classified as
`std/*` drops top-level decls whose names appear in nothing the
program can reach. Implementation lives in
`Interpreter/Archive/StdLibTreeShaker.cs`. The packager replaces the
std module's source with the slimmed text *before* hashing it for
the manifest's `SourceHash` field and emitting the `ModuleSource`
section, so the integrity model passes through unchanged.

Section payload format upgrades alongside: v1.1 emits a tagged
variant (magic `"SLIX"` + u16 version) carrying the per-module shake
report (kept names, dropped names, bytes-before / bytes-after).
`RacInspector` decodes both the v1.0 bare form and the v1.1 tagged
form, so v1.0 archives still inspect correctly.

Conservative-by-design:
* Only `RacModuleKind.StdLib` modules participate.
* Any unknown top-level construct (extension blocks, namespace
  decls, asm) opts the module out entirely.
* A pub symbol is reachable when its **name** appears in any
  non-std module's AST. The fixed point pulls in private helpers
  via the reachable pubs' own ref sets.
* Reflective resolution by string-literal name (`exists("foo")`,
  etc.) is NOT introspected. Programs that rely on it should pass
  `--no-tree-shake`.

CLI: `--compile <...> [--no-tree-shake]` (default on). Run
`--inspect-archive` to see the per-module kept/dropped lists.

Measured on a synthetic 30-fn std module of which the entry
references 3: archive shrinks from 4,634 → 3,569 bytes
uncompressed (~23% reduction), 1,690 → 1,546 compressed
(~9%, since deflate already compresses the redundancy well).

### v1.1 — Shared cross-module constant pool (#7)

`SharedConstPool` (section kind `0x07`) interns string / BigNumber /
long / double constants that appear in two or more module
`RaFunction.Consts[]` slots across the archive. v1 ModuleBytecode
payloads inlined every value; v2 payloads route through a 5-byte
pool ref (`u8 tag + u32 idx`) for shared values, dropping the
length-prefixed payload from the per-module bytecode.

Wire layout (little-endian):

```
"SCPL"  u32 magic
u16 version = 1
u16 reserved
i32 stringCount  + (string * count)
i32 numberCount  + (BigInteger Unscaled, BigInteger Scale) * count
i32 integerCount + (i32 * count)     // currently unused, reserved
i32 longCount    + (i64 * count)
i32 doubleCount  + (u64 bit pattern * count)
i32 floatCount   + (u32 bit pattern * count)
```

Builder discipline at pack time:

* **Per-value cost gate.** A string is admitted to the pool only
  when `N * (K - 1) > 4` (the per-value break-even between inline
  and pool encoding, where `N` is UTF-8 byte length and `K` the
  ref count). Long / double use `3 * (K - 1) > 8`. Int and float
  are never pooled — their inline payload size matches the pool
  ref size so pooling them only adds the pool-storage overhead.
* **Section-overhead amortisation.** Total projected save across
  all pooled values must clear `~100` bytes (section magic +
  version + count headers + directory entry). Below that, the
  pool is abandoned and the writer emits a v1 payload.
* **Scope.** v1.1 (#7) observes the script-level `RaFunction.Consts[]`
  of each module — i.e. the consts that the v2 bytecode actually
  serialises. Nested function bodies are IR-compiled lazily at
  runtime from their stored AST, so their consts are not visible
  to the build-time pool. Widening this to nested bodies is
  future work.

ModuleBytecode payload bumps to **version 2** when the encoder
emits a pool ref. v1 payloads stay v1 (no pool ref tags) and load
unchanged in any v1.0/v1.1-#7 reader. The reader auto-detects via
the `formatVersion` u16 in the payload header.

CLI: `--compile <...> [--no-const-pool]` (default on).

`--inspect-archive` decodes the pool and prints per-type counts
plus a sample.

## Future evolution

* **v1.x — widen bytecode coverage.** v1.1 supports the common-case
  AST nodes (primitives, control flow, functions, imports, member
  access, collections). Programs that use traits, structs, classes,
  patterns, async, asm, etc. currently fall back to source. Future
  PRs extend `AstNodeSerializer` to cover the remaining ~70 node
  kinds; the wire format already routes them through the same
  polymorphic dispatcher.
* **v1.x — debug info.** Strippable `DebugInfo` sections with
  source maps + line tables for production stack traces on archives
  built with `--no-debug`.
* **v1.x — signing.** Detached signatures in a `Signature` section
  + `RacFlags.Signed`. The format reserves both today.
* **v2 — incremental builds.** Stable module-section hashes already
  permit a content-addressed build cache: a packager that detects
  unchanged modules can reuse their compressed payloads byte-for-byte
  across rebuilds.

## Invariants worth remembering

* The Manifest section MUST be the first section emitted by the
  writer. The reader does not require this, but every tool we ship
  enforces it for predictable on-disk layout.
* Section hashes are over **uncompressed** bytes. Re-compressing an
  archive with a different `CompressionLevel` does not change any
  hash, including the file's directory hash.
* `RaFunction`-internal inline caches (load-global, member-access,
  enum-access, cast, call-method) carry live `SymbolTable` / shape
  references and are intentionally *not* serialised by any future
  payload format. They re-prime on the first execution after load.

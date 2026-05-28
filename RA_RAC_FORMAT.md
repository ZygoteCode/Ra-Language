# Ra Archive Container (`.rac`)

> Status: **v1.0** (FormatMajor = 1, FormatMinor = 0). Stable on-disk
> layout; payload model documented as the v1 baseline with a forward-
> compatible reservation for a future direct-bytecode payload.

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
| ModuleSource | `0x02`     | required (v1)      | UTF-8 source of a single `.ra` module     |
| ModuleBytecode | `0x03`   | reserved (≥ 1.1)   | Serialised `RaFunction` + AST snapshot    |
| DebugInfo    | `0x04`     | reserved           | Source maps, line tables                  |
| StdLibIndex  | `0x05`     | informational      | List of std refs the program touches      |
| Signature    | `0x06`     | reserved           | Detached signature payload                |
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
   and section directory hash.
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

The forward path is wired in: `RacSectionKind.ModuleBytecode` and
each module's `BytecodeSectionIndex` are reserved in the format
today. A future v1.1 loader can opportunistically prefer the
bytecode payload when the AST-snapshot serialiser lands, with
older v1 archives continuing to load unchanged.

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
| `--compile <entry.ra> [-o out.rac] [--no-compress]`      | Build a `.rac` from a source entry.     |
| `--run-archive <file.rac>`                               | Load and execute a `.rac`.              |
| `--inspect-archive <file.rac>`                           | Pretty-print header + manifest + sections. |
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

## Future evolution

* **v1.1 — direct bytecode payload.** Land an AST-snapshot
  serialiser, populate `ModuleBytecode` sections, prefer them at
  load time. Older v1 archives keep working via the v1 source path.
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

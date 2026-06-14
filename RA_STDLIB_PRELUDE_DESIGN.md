# Ra Standard Library & Prelude — Design

Status: implemented. Smoke + categorisation + wildcard + physical-package +
error tests at `tests/stdlib/`; coverage self-test via `--selftest-stdlib`.

## Motivation

Ra historically exposed every built-in as a single, undifferentiated blob of
global symbols: `Program._builtInFunctions`, the async/stream name lists, and
the 15 `*Builtins` registry categories were all dumped into one
`BuiltinSymbolTable` that sits at the root of every scope. Useful, but flat —
there was no taxonomy, no way to import a *subset*, and nowhere for the
library to grow.

This refactor reorganises that surface into a **hierarchical, importable
standard library** addressed under `std.*`, without breaking a single line of
existing code.

## Layered model

The design separates five concerns that used to be tangled:

1. **Language core** — lexer / parser / AST / IR / VM. Untouched.
2. **Compiler / VM intrinsics** — the native implementations
   (`BuiltInFunctionValue`, `BuiltInRegistry` handlers, async/stream
   dispatch). These *stay native* — they are the bottom layer and cannot be
   written in Ra. Their dispatch is unchanged.
3. **Standard library** — the `std/` tree: physical `.ra` modules on disk
   **plus** the manifest-synthesised virtual modules of categorised
   built-ins.
4. **Public prelude** — `std.prelude.*`: the ergonomic surface, brought in
   explicitly with `import std.prelude.*` (or a specific submodule).
5. **Import / module resolution** — the resolver now understands nested
   dotted paths, packages (directories), wildcards, and virtual modules.

**Built-ins are NOT auto-imported.** There is no implicit prelude: `print`,
`str_upper`, `len`, … are unreachable until the file imports the std module
they live in (`import std.prelude.io`, `import std.prelude.*`, …). The only
always-available surface — the *core* — is what is not a callable function
and is needed before any import could run: the annotation types (`@test`,
`@derive`, … resolved at parse/processing time) and the `Result` / `Option`
ADTs (the `?` operator depends on `Result`). This is "reduce the true
intrinsics to a minimum" taken literally: the core is tiny and fixed; every
function is library code reached through an explicit import.

## The taxonomy

Source of truth: [`Interpreter/Modules/StdLibrary.cs`](Interpreter/Modules/StdLibrary.cs).
It maps every built-in *function* to exactly one module. The map is **derived,
not hand-listed**:

* **registry built-ins** inherit their category from a group tag captured at
  registration time — `BuiltInRegistry.EnsureInitialized()` wraps each
  `XxxBuiltins.Register()` call in `RegisterGrouped("<category>", …)`, so the
  ~170 individual `Register(...)` sites are untouched and cannot drift;
* **async / stream built-ins** map by their public `Names` arrays;
* a small, stable **override table** places the switch-dispatched "direct"
  built-ins and refines the two files that mix concerns (`DebugBuiltins` →
  io + errors + debug; `RuntimeBuiltins` → reflect + errors + runtime).

### Prelude modules (`std.prelude.*`, import-only)

| Module | Contents (examples) |
|---|---|
| `std.prelude.io` | `print`, `println`, `print_ret`, `eprint`, `eprintln`, `read_line`, `clear_console` |
| `std.prelude.text` | `str_upper`, `str_lower`, split/join/trim/replace, `str_strip_prefix/suffix`, `str_partition/rpartition`, `str_swapcase`, `str_center`, `str_truncate`, `str_is_empty/blank`, `str_eq_ignore_case` … |
| `std.prelude.regex` | `regex`, `re_match`, `re_replace_all`, `re_split` … |
| `std.prelude.collections` | `len`, `list_*`, `map_*`, `set_*`, `tuple_*`, `make_*`, `list_windows`, `intersperse`, `list_rotate`, `find_last`, `count_by`, `distinct_by`, `scan` |
| `std.prelude.math` | `abs`, `min`, `max`, `clamp`, `floor`, `sqrt`, `pow`, `lerp`, `inv_lerp`, `remap`, `clamp01`, `copysign`, `fmod`, `round_to`, `is_even/odd`, `next_pow2`, `mean`, `median`, `variance`, `stddev` … |
| `std.prelude.convert` | `to_int`, `to_string`, `parse_int`, `format_hex` … |
| `std.prelude.fs` | `fs_read_text`, `fs_write_text`, `fs_list_dir`, `fs_glob` … |
| `std.prelude.time` | `now_unix_ms`, `monotonic_ns`, `time_format`, `tz_name` … |
| `std.prelude.os` | `os_name`, `env_get`, `args`, `cwd`, `exit`, `home_dir` … |
| `std.prelude.process` | process spawn / wait / pipe helpers |
| `std.prelude.reflect` | `type_of`, `is_*`, `fields_of`, `lookup`, `get_field`, `annotations_of` … |
| `std.prelude.runtime` | `clone`, `equals`, `hash`, `compare`, `drop`, `current_file` |
| `std.prelude.errors` | `throw_error`, `error_message`, `assert`, `assert_eq`, `assert_ne`, `assert_true`, `assert_false`, `assert_approx`, `panic`, `todo`, `unreachable`, `warn` |
| `std.prelude.func` | `partial`, `compose`, `combine`, `invoke` |
| `std.prelude.async` | `sleep`, `gather`, `race`, `channel*`, `select` … |
| `std.prelude.stream` | `stream_*`, `astream_*` |
| `std.prelude.validate` | `validate`, `validate_target`, `validate_deferred`, `coerce_value` |
| `std.prelude.test` | `run_tests` |
| `std.prelude.debug` | `dump`, `gc_collect`, `breakpoint` … |
| `std.prelude.encoding` | `base64_encode/decode`, `base64url_encode/decode`, `base32_encode/decode`, `hex_encode/decode`, `url_encode/decode` |
| `std.prelude.crypto` | hashing (`sha256_hex` …) + **signatures**: `ed25519_keypair/sign/verify`, `rsa_keypair/sign/verify` (PSS), `ecdsa_keypair/sign/verify` (P-256) |
| `std.prelude.random` | `random`, `random_int`, `random_float`, `random_bool`, `random_bytes`, `random_choice`, `random_sample`, `random_gaussian`, `random_string`, `random_hex`, `random_seed`, `uuid_v4` |
| `std.prelude.serialize` | `json_*`, `csv_parse`/`csv_parse_objects`/`csv_stringify`, `toml_parse`/`toml_stringify`, `yaml_parse`/`yaml_stringify` |
| `std.prelude.bytes` | `bytes_from_string/to_string`, `bytes_to_hex/from_hex`, `bytes_to_base64/from_base64`, `bytes_concat`, `bytes_slice`, `bytes_eq`, `bytes_xor`, `bytes_fill`, `bytes_read_u16/u32_{le,be}` |
| `std.prelude.net` | URI: `uri_parse`/`uri_is_valid`/`uri_join`/`uri_port`. **Live**: `dns_resolve`/`dns_resolve_one`/`dns_reverse`, `tcp_port_open`/`tcp_request`, `http_get`/`http_post`/`http_request`/`http_status`/`http_download` |
| `std.result` / `std.option` | **Ra-authored** combinators over the core `Result`/`Option` ADTs: `is_ok`/`is_err`/`unwrap`/`unwrap_or`/`map_ok`/`map_err`/`and_then`/`or_else`/…; `is_some`/`unwrap_some`/`map_some`/`filter_some`/`ok_or`/… |
| `std.prelude.platform` | **physical** `.ra` facade: `name`, `arch`, `version`, `is_windows`, `is_linux`, `is_macos`, `is_unix` |

The `encoding` / `crypto` / `random` / `serialize` / `net` modules are native
(C#), deterministic, cross-platform, and AOT-safe (BCL `Convert` / `Uri` /
`System.Security.Cryptography` one-shot APIs + hand-written JSON; no
reflection). `random` is a single process-wide seedable PRNG (these used to be
split across `std.prelude.math`; consolidated here). `std.prelude.platform`
is a physical Ra module under `std/prelude/`, swept into `import std.prelude.*`
beside the virtual modules — proof the package merges Ra code with built-ins.

### System modules (`std.sys.*`, NOT pulled by the prelude wildcard)

| Module | Contents |
|---|---|
| `std.sys.ffi` | `native_*`, `com_*`, callbacks, struct marshalling |
| `std.sys.asm` | `asm_*` x64 assembler/JIT |

These remain globally reachable for backward compatibility but live outside
the prelude so a blanket `import std.prelude.*` never drags the unsafe surface
in implicitly. Run `--selftest-stdlib` to print the live per-module counts.

## Import grammar & semantics

```
import std.prelude.io            // a virtual module → flattens its members
import std.prelude.io.*          // wildcard on a module → same set
import std.prelude               // a package → flattens all sub-module members
import std.prelude.*             // wildcard on a package → same set
import std                       // the std root package → everything under std
import std.text.casing           // a physical std file (std/text/casing.ra)
import std.text.*                // a physical package → every .ra under std/text/, recursive
import { abs } from std.prelude.math      // selective
import std.prelude.reflect as r           // alias → r.type_of(5)
```

A trailing `.*` is the new token (`DOT` `MUL`), parsed in
`ParseModuleSpecifier` and carried as `ModuleSpecifier.IsWildcard`.

### Resolution precedence (dotted paths under `std`)

Implemented in `ModuleManager.TryPlanStdImport`:

1. **manifest virtual module** wins (so the categorised built-ins are never
   shadowed by a stray file of the same path);
2. **physical file** `std/<path>.ra` (this is what keeps legacy `import std.io`
   byte-for-byte identical);
3. **package** — a virtual package (the path has manifest descendants) or a
   physical directory; aggregated into one synthetic module whose exports are
   the union of every virtual member beneath it **and** every `.ra` file in
   the matching directory (recursively, archive-overlay aware via
   `VirtualFs.EnumerateRaFiles`);
4. otherwise a clear `ModuleNotFoundError` that lists the available std
   modules.

A wildcard forces the package interpretation when one exists. Non-`std`
dotted roots and string-literal imports are completely unchanged.

Virtual modules and packages are synthesised lazily and cached in the same
`ModuleManager` cache (synthetic `\0std-mod:` / `\0std-pkg:` keys), cleared
between runs like every other module. Synthesis pulls the *live*
`BuiltInFunctionValue` instances from the current `BuiltinSymbolTable`, so no
state leaks across menu re-runs.

## Scope model & compatibility

Two stores, built in `Program.InitializeSymbolTable`:

* **`BuiltinSymbolTable`** — the full set (633 functions + annotation types +
  ADTs). NOT a runtime parent; it is the synthesis source for virtual std
  modules and the "known names" source for tooling/LSP.
* **`CoreSymbolTable`** — the always-available runtime parent of every user
  scope and module. Holds ONLY the non-function builtins (annotation types +
  `Result`/`Option`), carried over as the *same instances*. The 633 functions
  are deliberately excluded.

`GlobalSymbolTable = new SymbolTable(CoreSymbolTable)`, and every module loads
with `new SymbolTable(coreProvider())` — so a function name resolves only if
the scope imported it. Built-in resolution is plain parent-chain name lookup
(the resolver assigns no special `Builtin` kind), so excluding functions from
the parent is sufficient and total.

This is a **breaking language change** (intended): every program must import
what it uses. Consequences handled in this change:

* All test + bench + fixture `.ra` files gained `import std.prelude.*;`
  (sys-using files also `import std.sys.*;`). std modules import their deps.
* `import std.io` still loads the physical `std/io.ra` (which itself now
  imports `std.prelude.io` for `print`).
* The `.rac` packager skips virtual std imports (`StdLibrary.IsVirtualStdPath`)
  — they are runtime-provided, not bundled. Committed prebuilt archives were
  regenerated from the updated sources; `--compile --std-root <dir>` was added
  so a build can target a specific std tree (used to rebuild the treeshake
  fixture against its own `std/`).
* Missing-module / circular-import / private-symbol errors are unchanged and
  still catchable via `try/catch`.

## Adding to the library

* **New built-in in an existing category** — just `Register(...)` it inside
  the relevant `*Builtins.Register()`; it inherits the group automatically and
  appears under the corresponding `std.prelude.*` module. `--selftest-stdlib`
  stays green.
* **New built-in that needs a new category** — add one `RegisterGrouped(...)`
  line in `BuiltInRegistry.EnsureInitialized` (or a one-line override in
  `StdLibrary`).
* **New Ra-authored module** — drop a `.ra` file under `std/` (e.g.
  `std/text/casing.ra`); it is importable immediately by its dotted path and
  swept by the enclosing package wildcard.

## Invariant: no orphans

`StdLibrary.Audit()` (driven by `--selftest-stdlib`) compares the categorised
set against the live built-in set and fails on any **uncategorised** built-in
(a new one nobody placed) or **phantom** manifest entry (a stale/typo name).
This makes "every built-in lives in a module" an enforced invariant rather
than a hope. Current status: **871 built-ins across 27 virtual std modules
(+ the physical `std.prelude.platform`, `std.result`, `std.option`,
`std.text.inflect`), complete and exact.** (Verify the live total any time with
`--selftest-stdlib`, which prints the per-module counts and re-runs the audit.)

## Expansion log (2026-06)

A breadth pass closing the ergonomic gaps that the flat surface still had —
all native, AOT-safe (BCL one-shot APIs + hand-written parsers, no
reflection), deterministic, offline-testable, and auto-categorised by the
group-tag taxonomy (so `--selftest-stdlib` stayed green with no manifest
edits). Tests at `tests/stdlib/test_{math,text,collections,assert,csv,
encoding,crypto,random}_ext.ra` (the CSV file is `test_csv.ra`).

* **serialize** — `csv_parse` / `csv_parse_objects` / `csv_stringify`
  (RFC 4180: quoted fields, `""` escapes, embedded CR/LF, custom delimiter).
* **math** — `lerp` / `inv_lerp` / `remap` / `clamp01`, `copysign` / `fmod` /
  `round_to` / `next_pow2` / `is_even` / `is_odd`, and the descriptive stats
  `mean` / `median` / `variance` / `stddev` (population; list-or-variadic).
* **text** — `str_strip_prefix/suffix`, `str_remove`, `str_replace_first`,
  `str_partition/rpartition`, `str_swapcase`, `str_center`, `str_truncate`,
  `str_is_empty/blank`, `str_eq_ignore_case`.
* **collections** — `list_windows`, `intersperse`, `list_rotate`,
  `find_last`, `count_by`, `distinct_by`, `scan`.
* **errors** — `assert_ne` / `assert_true` / `assert_false` / `assert_approx`
  and the catchable `panic` / `todo` / `unreachable`.
* **encoding** — `base64url_encode/decode` (RFC 4648 §5) + `base32_encode/decode`.
* **crypto** — `sha384_hex`, `hmac_sha512_hex`, `hmac_sha1_hex`, and
  `pbkdf2_sha256_hex` (the standard password-stretching KDF).
* **random** — `random_sample` (without replacement), `random_gaussian`
  (Box-Muller), `random_string`, `random_hex`.

## Expansion log (2026-06, wave 2) — the former "Future work"

The four deferred items are now shipped (all native unless noted, AOT-safe,
offline-tested where the surface allows; full suite stays 0-fail). Tests at
`tests/stdlib/test_{crypto_asym,toml,yaml,net_live,bytes,math_ext2,text_ext2,
collections_ext2,time_ext,result_option}.ra`.

* **Asymmetric crypto** (`crypto`) — `ed25519_*`, `rsa_*` (PSS/SHA-256),
  `ecdsa_*` (P-256) keypair/sign/verify. Ed25519 reuses the vendored
  `Interpreter/Archive/Crypto/Ed25519.cs`; RSA/ECDSA are BCL one-shot. Keys
  and signatures are base64 strings. Verified by sign→verify + tamper/wrong-key.
* **More serialize formats** (`serialize`) — `toml_*` (tables, arrays-of-tables,
  dotted keys, inline tables, int bases, multi-line arrays) and a pragmatic
  `yaml_*` (block maps/seqs, flow collections, scalar inference). Both
  hand-written. YAML out of scope: anchors/tags/block-scalars (documented).
* **Live networking** (`net`) — blocking: `dns_resolve`/`dns_resolve_one`/
  `dns_reverse`, `tcp_port_open`/`tcp_request`, `http_get`/`http_post`/
  `http_request`/`http_status`/`http_download`. **Async** (return a Ra Task that
  composes with `std.prelude.async` await/gather): `http_get_async`/
  `http_post_async`/`http_request_async`, `dns_resolve_async`,
  `tcp_request_async`. **100% NativeAOT-clean** — `dotnet publish -c Release -r
  win-x64` emits ZERO IL2026/IL3050 from the net code; System.Net.Http /
  Sockets / Dns are reflection-free under .NET 10 AOT, and async/await lowers to
  a reflection-free state machine — no RootDescriptor or feature switch needed.
  Offline-tested via localhost DNS, closed ports, refused/invalid-host errors
  and await error-propagation; live success is smoke-only.
* **Ra-authored modules** (`std.result`, `std.option`) — real combinator logic
  (match-based, not re-exports) over the core `Result`/`Option` ADTs. Import
  explicitly (`import std.result;`). Proves the "drop a `.ra` under `std/`"
  extension path with genuine library code.
* **Bytes** (new `bytes` module) — binary-buffer helpers over `list<int 0..255>`:
  text/hex/base64 conversion, slice/concat/xor/fill, LE/BE integer reads.
* **Breadth** — `math` (shaping, integer division, combinatorics, extra
  transcendentals), `text` (case styles, slugs, word ops), `collections`
  (split/transpose/frequency, map & set algebra), `time` (calendar helpers).

## Expansion log (2026-06, wave 3) — residual completed

The wave-2 "Future work" is now closed. **Ra is NativeAOT-only**; everything
below is verified AOT-clean — `dotnet publish -c Release -r win-x64` emits **8
IL3050 warnings total, all pre-existing FFI**, and **zero** from any stdlib
addition (the ILC trim/AOT analysis runs fully even where the MSVC *link* step
is unavailable on a given box).

* **AOT verification** — the earlier "may need a RootDescriptor" caveat was
  wrong. HttpClient (SocketsHttpHandler), Sockets, Dns and async/await state
  machines are all reflection-free under .NET 10 AOT. No RootDescriptor, no
  feature switch.
* **Async net** — `http_*_async`, `dns_resolve_async`, `tcp_request_async`
  return a Ra Task (via `AsyncScheduler`) that composes with await / gather.
* **TOML datetimes** — an RFC 3339 *offset* date-time now parses to a real
  instant (Unix-ms, the time-module representation); local date/time/datetime
  (no offset) stay strings (no unambiguous instant).
* **YAML completeness** — block scalars (`|` literal / `>` folded, with `-`/`+`
  chomping), anchors (`&a`) + aliases (`*a`), tags (`!!str/int/float/bool/null`),
  and multi-document streams via `yaml_parse_all`.
* **Deeper sub-modules** — `std.text.inflect` (physical `std/text/inflect.ra`)
  is a two-level module with real logic (`pluralize`/`singularize`/`ordinal`),
  proving arbitrarily-deep `std.x.y` nesting.

## Future work

* **TOML/YAML edge cases** — TOML local-datetime/date/time stay strings (no Ra
  date type yet); YAML merge keys (`<<`), complex/flow keys and `%` directives
  remain out of scope. The covered surface handles the vast majority of configs.
* **HTTP/2 + streaming** — the net surface is HTTP/1.1 request/response and
  whole-body reads; chunked streaming bodies could become an `astream`.
* **A first-class date/time type** — would let TOML/YAML datetimes round-trip
  losslessly and give the `time` module richer ergonomics.

## Files

* `Interpreter/Modules/StdLibrary.cs` — taxonomy + audit + `IsVirtualStdPath` (new)
* `Interpreter/Modules/ModuleManager.cs` — virtual module / package loading; two providers (core vs function store)
* `Interpreter/Modules/ModuleSpecifier.cs` — `IsWildcard`
* `Interpreter/Values/Functions/BuiltInRegistry.cs` — group tags
* `Interpreter/Values/Functions/Builtins/{Encoding,Crypto,Random,Serialize,Net}Builtins.cs` — new native modules
* `Interpreter/Values/Functions/Builtins/MathBuiltins.cs` — random_* consolidated out to `random`
* `Interpreter/Archive/VirtualFs.cs` — archive-safe directory enumeration
* `Parser/Parser.Imports.cs` — trailing `.*`
* `Interpreter/Visitors/Imports/ImportNodeVisitor.cs` — two-provider init; `MergeExtensions` reuse
* `Interpreter/Archive/RacRunner.cs` — two-provider init
* `Interpreter/Archive/RacPackager.cs` — skip virtual std imports when bundling
* `Program.cs` — `CoreSymbolTable` split; `--selftest-stdlib`; `--compile --std-root`; `AllBuiltinFunctionNames()`
* `std/io.ra`, `std/text/*.ra`, `std/prelude/platform.ra` — std modules import their prelude deps; physical platform facade
* `tests/**`, `bench/**` — every `.ra` gained `import std.prelude.*;` (+ `import std.sys.*;` for FFI/asm); 6 prebuilt `.rac` regenerated
* `tests/stdlib/*.ra` — stdlib test suite (incl. crypto/encoding/random/serialize/net/platform)

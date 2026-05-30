# Ra Language test suite

The `.ra`-only regression suite for Ra Language. There is no xUnit / NUnit
runner: each test is a real `.ra` source file exercised against the
interpreter built from this repository. This single `tests/` tree is the
canonical suite — the former top-level `other_tests/` folder has been folded
in here.

## How a test reports its result

The runner's **authoritative** pass/fail signal is:

```
PASS  <=>  process exit code == 0  AND  no "[id] FAIL" / "FAIL ..." line on stdout
FAIL  <=>  any FAIL marker, OR a non-zero exit
```

The exit code is reliable: the interpreter now reports an uncaught runtime
error, a lex/parse abort, or a file-read failure via `Environment.ExitCode`
(a caught error or a clean run yields 0). So a test that hits an uncaught
error is scored as a failure even if it printed no FAIL line.

Two reporting styles are recognised so both the structured suite and the
older hard-asserting files score without rewrites:

* **soft-assert** (preferred for new tests) — one line per case:
  ```
  print("=== <category>/<name> ===");
  ... cases, each printing exactly one of:
  [<id>] OK
  [<id>] FAIL: <details>
  print("=== done ===");
  ```
  A tiny helper at the top of the file is the norm:
  ```
  fn ok(id, cond) { if cond { print("[" + id + "] OK"); } else { print("[" + id + "] FAIL"); } }
  ```
* **hard-assert** — `check` / `check_eq` / `assert_eq` helpers that `throw`
  on failure (so the file exits non-zero and the runner flags it). Used by
  some feature suites (`types/test_constructors.ra`, `native/test_asm*.ra`, …).

Markers are matched at column 0, so an error traceback that echoes a source
line containing the literal text `[id] FAIL` is never miscounted.

## Layout

```
tests/
├── lexer/         # tokens, literals (hex/bin/oct/suffixes), escapes, interpolation, comments, regex literals
├── parser/        # precedence, associativity, terminators, nesting, call chains
├── operators/     # arithmetic, comparison, strict-eq, logical/short-circuit, bitwise, shifts+rotates, compound-assign, range, ternary, pipeline, spread
├── numbers/       # int widths, int128/uint128, overflow, division/modulo, float specials, conversions, math built-ins
├── strings/       # concat, built-ins, interpolation, format specs, regex, unicode
├── collections/   # list / map / set / tuple / slicing
├── control_flow/  # if/while/do-while/for/switch/match/goto/retry/break-continue/yield
├── functions/     # definitions, defaults, recursion, closures, lambdas, captures, pipeline, variadic, operator overload
├── scoping/       # block/function scope, const/final, shadowing, let, delete
├── types/         # class/struct/enum/interface/trait/record/generics/inheritance/constructors/properties/static/operator-overload
├── extensions/    # extension methods/fields/operators, @sealed, cross-module merge
├── pattern_match/ # match literals/guards/destructure/struct/ADT, diagnostics
├── unions/        # union types + narrowing diagnostics
├── annotations/   # decl / validators / contracts / derive / test framework / reflection
├── modules/       # imports (relative + std.*), aliases, selective, cyclic, namespaces (single + multi-file + negative)
├── errors/        # try/catch/finally ordering, throw + catch values, runtime, undefined
├── async/         # sleep/await, spawn, channels, select, concurrency
├── concurrency/   # sync primitives + borrow-across-fiber guard (negative)
├── streams/       # sync + async streams, foreach, fusion
├── events/        # event declarations + dispatch
├── delegates/     # delegate values + composition
├── regex/         # runtime regex built-ins
├── reflection/    # type_of, is_*, class_of, fields/methods_of, metadata queries
├── builtins/      # collections / strings+math / os+fs / reflection / runtime / smoke built-in coverage
├── native/        # FFI (@dll_import), inline asm, native memory, marshalling, diagnostics
├── semantics/     # truthiness, equality, move/borrow
├── integration/   # longer multi-feature programs (calculator, data pipeline, state machine)
├── edge_cases/    # deep recursion, large literals, nested structures, feature interactions, weird identifiers
├── archive/       # .rac compile->run pipeline tests (run_archive_tests.ps1 + fixtures/)
├── regressions/   # parking lot for known-broken probes (currently empty; see below)
├── run_suite.ps1  # PowerShell driver (Windows PowerShell 5.1 compatible)
└── README.md
```

### Archive (.rac) pipeline

`run_suite.ps1` only runs `.ra` files, so the compile→archive→run pipeline is
exercised by a dedicated driver, `archive/run_archive_tests.ps1`, which
`run_suite.ps1` invokes as a final phase (skip with `-NoArchive`, or run it
standalone). It performs four self-validating checks against `archive/fixtures/`:

1. **Round-trip** — for every runnable entry, the program output of running the
   source directly must equal that of compiling it to a `.rac` and running the
   archive (the runner's own `[Ra Language] …` diagnostic lines are filtered
   out). No hardcoded expected output. Archive-only entries (that can't run
   standalone) are auto-detected and skipped here, but covered by check 3.
2. **Compile knobs** — `default` / `--no-compress` / `--no-tree-shake` /
   `--no-const-pool` each build an archive whose run matches the direct run.
3. **Prebuilt archives** — every committed `.rac` must still open + run cleanly
   (format backward-compat, V4/V5).
4. **Resilience** — corrupt / truncated / empty / header-flipped `.rac` inputs
   must be rejected with a non-zero exit and a diagnostic (never crash, never
   hang).

Folders whose name contains `helpers`, plus `std/`, `fixtures/`, and
`regressions/`, are **skipped** by the runner — they hold imported modules /
fixtures / parked probes, not runnable tests. A file can also opt out with a
`# runner: skip` directive on one of its first three lines.

Imports resolve relative to the **importing file's** directory first (then the
project root), so a test and its `*_helpers/` directory move together safely.
`std.*` dotted imports resolve against `<exeDir>/std` (populated from the
repo-root `std/` folder by the csproj `<Content Include="std\**"/>` copy).

## Running the suite

The interpreter (`RaLanguage.exe`) sits one directory above `tests/`; the
runner finds it automatically.

```powershell
# everything:
powershell -ExecutionPolicy Bypass -File tests\run_suite.ps1

# one category (substring match on the relative path):
powershell -File tests\run_suite.ps1 -Filter operators

# raise the per-file timeout (default 30s):
powershell -File tests\run_suite.ps1 -TimeoutSeconds 45

# quiet (failures + summary only):
powershell -File tests\run_suite.ps1 -Quiet
```

The runner exits 1 if any file reports a FAIL, exits non-zero, or times out,
and prints a per-file line plus an aggregate of OK / FAIL assertions.

You can also run a single file directly (from the build dir, so std + relative
imports resolve):

```powershell
.\RaLanguage.exe tests\operators\test_arithmetic.ra
```

## `regressions/`

The parking lot for syntax / behaviour that is currently broken. Each probe's
header documents the bug and the expected behaviour once fixed; the runner
skips the folder so the suite stays green. When a bug is fixed, move its probe
into the appropriate live category (dropping any skip) so it runs.

## Adding a new test

1. Pick the right category folder (or add one + update this README).
2. Name the file `test_<short_name>.ra`.
3. Top-of-file comment lists the case IDs you cover.
4. Use the `ok(id, cond)` helper; print one `[id] OK` / `[id] FAIL: …` per case.
5. Wrap operations that may raise in `try { … } catch (e) { … }` and assert
   which branch ran — a single uncaught error otherwise aborts the file.
6. Run the file in isolation, then the whole suite, before committing.

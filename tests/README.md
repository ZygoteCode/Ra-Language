# Ra Language test suite

This directory contains the `.ra`-only regression suite for Ra Language.
There is no xUnit / NUnit runner: each test is a real `.ra` source file
exercised against the interpreter built from this repository.

## How a test reports results

Every test file follows the same convention:

```
print("=== <category>/<short_name> ===");
... cases ...
[<id>] OK    -> case passed
[<id>] FAIL: <details>  -> case failed
print("=== done ===");
```

The runner counts `[…] OK` and `[…] FAIL` lines in `stdout` to score a file.
`stderr` is captured so that lexer / parser / runtime diagnostics are
visible when the file crashes.

## Layout

```
tests/
├── lexer/             # tokens, literals, escapes, interpolation
├── parser/            # precedence, associativity, terminators, nesting
├── operators/         # arithmetic, comparison, logical, bitwise, range, ternary
├── numbers/           # int / float / typed declarations / math built-ins
├── strings/           # concat, repeat, built-ins, interpolation, format specs
├── collections/       # list / map / set / tuple / slicing
├── control_flow/      # if/while/do-while/for/switch/goto
├── functions/         # declarations, defaults, recursion, closures, lambdas, pipeline
├── scoping/           # block scope, function scope, const/final, shadowing, let, del
├── types/             # class / struct / enum / interface / trait / generics / extend
├── pattern_match/     # match expression (mostly deferred to regressions)
├── annotations/       # decl / validators / contracts / derive / test framework / reflection
├── modules/           # imports + namespaces (with helpers/)
├── errors/            # try/catch/finally, throw (deferred), runtime, undefined
├── async/             # sleep/await, spawn, channels
├── reflection/        # type_of, is_*, exists, nameof, typeof
├── semantics/         # truthiness, equality across types
├── integration/       # multi-feature tests (modules + namespaces, etc.) with helpers/
├── edge_cases/        # deep recursion, large literals, empty constructs, weird identifiers
├── regressions/       # known-broken / known-hanging probes (opt-in)
├── run_suite.ps1      # PowerShell driver — uses per-file timeout to survive hangs
└── README.md
```

`helpers/` folders inside `modules/` and `integration/` are imported by the
tests next to them; they are NOT executed by the runner.

## Running the suite

From the repository root, after a Release build:

```powershell
# everything except regressions:
pwsh -File tests/run_suite.ps1

# only one category:
pwsh -File tests/run_suite.ps1 -Filter operators

# include known-broken / known-hanging probes:
pwsh -File tests/run_suite.ps1 -IncludeRegressions

# raise the per-file timeout (default 15 s):
pwsh -File tests/run_suite.ps1 -TimeoutSeconds 30
```

The runner exits with code 1 if any test reports a `FAIL`, times out, or
crashes. It prints a per-file line and a final aggregate of OK / FAIL
assertions plus file-level CRASH / TIMEOUT counts.

You can also run a single file directly:

```powershell
.\bin\x64\Release\net10.0\RaLanguage.exe tests\operators\test_arithmetic.ra
```

Imports resolve relative to the current working directory, so always
launch the runner / interpreter from the repository root.

## `regressions/` (currently empty)

`regressions/` is the parking lot for syntax & behaviour we couldn't
exercise inside the main suite. Every known parked bug has now been
fixed and the corresponding probe absorbed into a real test file, so
the folder is empty.

If you discover a new form the interpreter rejects or hangs on, drop a
self-contained probe here, document the failure mode in the top
comment, and add a row to the table below. Pass
`-IncludeRegressions` to the runner to fire every probe — expect
TIMEOUTs and CRASHes.

## Adding a new test

1. Pick the right category folder (or create a new one + update this README).
2. Name the file `test_<short_name>.ra` — `run_suite.ps1` picks up every
   `.ra` outside `helpers/`.
3. Top-of-file comment lists the case IDs you cover.
4. Each case prints exactly one `[<id>] OK` or `[<id>] FAIL: <details>`
   line so the runner scores it.
5. Wrap operations that *might* raise in `try { … } catch (ex) { … }`
   so a single runtime failure doesn't abort the whole file.
6. Run the file in isolation, then run the whole suite, before checking
   it in.

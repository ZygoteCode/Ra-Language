# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Ra Language — a tree-walking interpreter for a custom scripting language, written in C# on **.NET 10**, **x64 only**, with `PublishAot=true`, `AllowUnsafeBlocks=true`, and `InvariantGlobalization=true`. Single project (`RaLanguage.csproj`, solution `RaLanguage.slnx`).

`.ra` files are Ra source. Examples at repo root: `tests_annotations.ra`, `tests_annotations_v2.ra`, `tests_validation.ra`, `tests_validation_v2.ra`, `tests_validation_v3.ra`. There is no xUnit/NUnit test runner — these scripts are the regression suite, executed by running the interpreter against each file.

## Common commands

Build:
```
dotnet build
dotnet build -c Release
```

Run interpreter on a `.ra` file (CLI path mode — required extension `.ra`, file must exist):
```
dotnet run -- path\to\script.ra
```

Run the interactive menu (no args): launches a JIT warmup of 1000 iterations against a fixed snippet, then offers `[1]` run `main.ra` once, `[2]` run `main.ra` repeatedly on ENTER, `[3]` hot-restart mode that polls `main.ra` every 100 ms and re-executes on change. `ExecuteMainFile` reads from CWD, so run from a directory containing `main.ra`.

Execute a "test" file:
```
dotnet run -- tests_annotations.ra
dotnet run -- tests_validation.ra
```

AOT publish (Release/x64 only — project locks `Platforms=x64`):
```
dotnet publish -c Release -r win-x64
```
The native **link** step needs `vswhere.exe` on PATH (the VS *Installer* dir, e.g. `C:\Program Files (x86)\Microsoft Visual Studio\Installer`) plus a `vcvars64`-initialised environment, otherwise it fails with `MSB3073` / `vswhere.exe non riconosciuto`. ILC analysis is clean apart from pre-existing IL3050 in the FFI/interop subsystem. Output exe: `bin\x64\Release\net10.0\win-x64\publish\RaLanguage.exe`.

Run the language server (separate process; JSON-RPC over stdio; STDOUT is protocol-only, logs go to STDERR):
```
dotnet run -- --lsp
RaLanguage.exe --lsp --log-level debug
```
The `--lsp` branch is taken at the very top of `MainCore`, before `Console.Title` / priority / JIT warmup.

`Program.Main` sets `ProcessPriorityClass.RealTime` on startup — expect the host machine to feel sluggish while running. Strip that when profiling on a shared box.

## Architecture

Execution pipeline lives in [Program.cs:58](Program.cs) (`Program.Run`):

1. **Lexer** ([Lexer/Lexer.cs](Lexer/Lexer.cs)) — char stream → `Token[]`, populates a `DiagnosticBag`. Uses precomputed `s_isDigit` / `s_isLetterOrDigit` ASCII tables and `AggressiveInlining` for hot paths. Tokens defined in [Lexer/Tokens/](Lexer/Tokens) (`Token`, `TokenType`, `Keyword`).
2. **Parser** ([Parser/Parser.cs](Parser/Parser.cs)) — tokens → AST (`AstNode` tree). Maintains a stack of generic-scope `HashSet<string>` for resolving in-scope type parameters during parsing. AST node kinds live in [Parser/Nodes/](Parser/Nodes), tagged by the [AstNodeType](Parser/Nodes/AstNodeType.cs) enum.
3. **DeriveTransformer** ([Interpreter/Runtime/Annotations/DeriveTransformer.cs](Interpreter/Runtime/Annotations/DeriveTransformer.cs)) — AST rewrite pass that expands `@derive(...)` annotations.
4. **StaticAnalyzer** ([Interpreter/Runtime/Annotations/StaticAnalyzer.cs](Interpreter/Runtime/Annotations/StaticAnalyzer.cs)) — warning-only pass that runs against the post-derive AST and the global `SymbolTable`.
5. **Interpreter** ([Interpreter/Interpreter.cs](Interpreter/Interpreter.cs)) — visits AST with a `Context` (carries the active `SymbolTable`) and returns a `RuntimeResult` (`RuntimeValue?, Error?`).

### Visitor dispatch

`Interpreter._visitors` is an `INodeVisitor[]` indexed by `(int)AstNodeType` — O(1) lookup, no reflection. `RegisterVisitors()` wires every `AstNodeType` to a visitor in [Interpreter/Visitors/](Interpreter/Visitors). **When adding a new AST node kind, you must:**

- Add the enum entry in [Parser/Nodes/AstNodeType.cs](Parser/Nodes/AstNodeType.cs).
- Create the `*Node` class under the matching `Parser/Nodes/<Category>/` folder.
- Create the `*NodeVisitor` under `Interpreter/Visitors/<Category>/`.
- Register it in `Interpreter.RegisterVisitors()` ([Interpreter/Interpreter.cs:39](Interpreter/Interpreter.cs)). A missing registration throws at runtime with `No visitor module registered for the node: ...`.

`Parser/Nodes/` and `Interpreter/Visitors/` mirror each other category-for-category (`Classes`, `Enums`, `Functions`, `Interfaces`, `Iterations`, `Operations`, `Primitives`, `Special`, `Statements`, `Structs`, `Traits`, `Variables`, `Annotations`, `Imports`). Keep that mirror intact.

### Runtime model

- **`SymbolTable`** ([Interpreter/Runtime/SymbolTable.cs](Interpreter/Runtime/SymbolTable.cs)) — variable/function/type bindings; `SymbolEntry` carries metadata for `let`/move semantics (`IsLet`, `IsMoved`, `Value.IsCopy`). `Interpreter.ExtractVariableValueByName` enforces the move-on-use rule for non-copy `let`s.
- **`Context`** — current file name plus active symbol table; threaded through every visitor call.
- **`GlobalSymbolTable`** — `static` on `Program`. `InitializeSymbolTable()` registers the built-in functions list (`print`, `print_ret`, `exists`, `field_exists`, `drop`, `is_public`, `is_field_public`, `is_field_static`, `annotations_of`, `has_annotation`, `annotation_arg`, `annotation_targets`, `validate`, `validate_target`, `validate_deferred`, `coerce_value`, `run_tests`) and calls `BuiltInAnnotations.RegisterAll`. **Add new built-ins to the `_builtInFunctions` array in [Program.cs](Program.cs) and implement them in [Interpreter/Values/Functions/](Interpreter/Values/Functions).**
- **`MetadataRegistry.Global`** — process-wide annotation metadata store, cleared on every `InitializeSymbolTable()`. `ExecuteMainFile` re-initializes between menu runs, so global state does not leak across `[1]`/`[2]`/`[3]` cycles.
- **`ExtensionRegistry`** — per-module storage for extension methods, properties, operators, indexers, events, and **fields** (v2.3). `Extension*Entry` records track `IsLocal` (defined here vs imported via `import *`) and `IsBlockPublic` (the `pub` on the surrounding `extend` block); resolution is local-first then imported, derived→base along the class chain. Property dispatch routes through `ExtensionDispatch` → `PropertyAccessOps`. Extension *fields* live in `ExtensionFieldStorage` — a process-wide slot allocator backed by per-instance `RuntimeValue?[] ExtFieldSlots` + `ulong[] ExtFieldInitBits` (lazy-allocated, zero overhead when untouched); reads/writes are O(1) array indexing, AOT-safe with no reflection. `@sealed extend T` freezes a target against further extensions. `extend Box<int>` vs `extend Box<string>` dispatches by receiver `GenericBindings`. Cross-module method-overload ambiguity surfaces as a diagnostic. Full grammar, dispatch order and storage model in [RA_EXTENSIONS_DESIGN.md](RA_EXTENSIONS_DESIGN.md); smoke tests at `tests_extensions.ra`.
- **`TypeSystem` / `TypeDescriptor` / `TypeChecker`** ([Types/](Types)) — static type assignment compatibility (`any`, type parameters, ref types, primitives, classes, interfaces, traits, structs). `IsAssignable` is the entry point.
- **`RuntimeValue` hierarchy** ([Interpreter/Values/](Interpreter/Values)) — `Primitives/`, `Classes/`, `Structs/`, `Enums/`, `Interfaces/`, `Traits/`, `Functions/`, `Annotations/`, plus `Operators/` for arithmetic/comparison overloads and a `BigNumber` for `int128`/`uint128`/`decimal`-class values.

### Annotations subsystem

Centre of gravity is [Interpreter/Runtime/Annotations/](Interpreter/Runtime/Annotations). Highlights:

- **Built-in meta-annotations**: `@target`, `@repeatable`, `@inherited`, `@sealed`, `@composes`, `@priority`, `@intercept`, `@deprecated`, `@validator`, `@returns`, `@deferred`, `@coerce`, plus testing (`@test`, `@before`, `@after`, `@parameterized`, `@expected_throws`, `@skip`), contract (`@requires`, `@ensures`, `@invariant`), and `@derive`. Names listed in `BuiltInAnnotations` as `const string`s.
- **`AnnotationProcessor`** applies annotations during interpretation; **`AnnotationInterceptors`** implements the call-site interceptor pipeline (`@intercept` chain ordered by `@priority`).
- **`AnnotationValidator` + `BuiltInValidators` + `ConstraintAnnotationRegistry`** — parameter validation (`@min`, `@max`, `@range`, `@not_empty`, `@length`, …) used by `tests_validation*.ra`.
- **`ContractEvaluator`** evaluates `@requires` / `@ensures` / `@invariant`.
- **`CoercerRegistry`** + `coerce_value` built-in handle `@coerce`.
- **`MetadataRegistry`** + `MetadataKeyResolver` + `MetadataTarget` — keys like `fn:build_engine`, `class:Box`, `field:Account.balance`. This is the surface that `annotations_of`, `has_annotation`, `annotation_arg`, `annotation_targets` query.
- **`TestRunner`** — backs the `run_tests` built-in and processes `@test`/`@before`/`@after`/`@parameterized`/`@expected_throws`/`@skip`.

### Imports / modules

[Interpreter/Modules/ModuleManager.cs](Interpreter/Modules/ModuleManager.cs) is a `ConcurrentDictionary<string, LoadedModule>` cache keyed by absolute path. `LoadedModule` owns its own `SymbolTable`, `ExtensionRegistry`, and `ExportTable`. `ImportNodeVisitor.InitializeModuleManager(basePath)` is called once from `Program.InitializeSymbolTable()` with `Directory.GetCurrentDirectory()` — imports resolve relative to the CWD, not to the interpreter executable.

### Errors

[Errors/](Errors) — `DiagnosticBag` (lexer + parser warnings/errors) and the typed error hierarchy under [Errors/Types/](Errors/Types) (`ExpectedCharacterError`, `IllegalCharacterError`, `InvalidSyntaxError`, `ModuleError`, `RuntimeError`). Lexer/parser failures are surfaced as `InvalidSyntaxError` from `Program.Run`; runtime failures are returned in `RuntimeResult.Error`.

### Properties subsystem

`prop NAME[: TYPE] [= default] [body]` declarations on class / struct / record / interface / trait bodies are first-class properties — they share the same member-name namespace as fields and methods, and route through the same `MemberAccessHelper` / `MemberAssignmentHelper` dispatch (so the IR's `OP_GET_MEMBER` / `OP_SET_MEMBER` opcodes pick them up with zero new opcodes). Design and grammar in [RA_PROPERTIES_DESIGN.md](RA_PROPERTIES_DESIGN.md). Runtime descriptors and the accessor pipeline live in [Interpreter/Runtime/Properties/](Interpreter/Runtime/Properties); the AST nodes are in [Parser/Nodes/Properties/](Parser/Nodes/Properties). Stored auto-properties allocate a slot in the hidden-class shape (`ClassTypeValue.BuildFieldShape` / `StructTypeValue.BuildFieldShape`) under the property name, so steady-state read cost matches a field once the IC primes. Lazy properties keep a `LazyInitialized` set on the instance for first-touch evaluation. Smoke tests at `tests_properties.ra`; microbench at `bench_properties.ra`.

### Lambdas

Bar-style anonymous functions — `|x| body`, `||`, `|x: int| -> int { ... }`, `[caps] |x| body` — lower to a `FunctionDefinitionNode` with `VarNameTok = null`, joining the same `Resolver` → `IrCompiler` → `OP_DefineFunction` (`0x8F`) → `FunctionDefinitionHelper.Apply` → `BaseFunctionValue.FreezeCaptures` path the existing anonymous-`fn` form takes. **Zero new AST nodes, zero new opcodes, zero new visitors** — the parser-only addition lives in [Parser/Parser.Lambdas.cs](Parser/Parser.Lambdas.cs) and is hooked at the atom-position branch of [Parser/Parser.Expressions.cs](Parser/Parser.Expressions.cs). Atom-position `|` (BITWISE_OR) and `||` (Keyword.Or) are unambiguous lambda openers; for the `[caps] |x|` shape the parser probes `ParseOptionalCaptureList` and rolls the cursor back to the list-literal path on a non-match. Design in [RA_LAMBDAS_DESIGN.md](RA_LAMBDAS_DESIGN.md). Smoke + capture + recursion + higher-order tests at `tests_lambdas.ra`; microbench at `bench_lambdas.ra`.

### Constructors (generative / named / factory)

Three constructor flavours on class bodies, **all reusing the existing call + member-access dispatch — zero new opcodes, zero new AST node kinds, zero new visitors**. Generative (unnamed `pub Point(x, y) { ... }`, or named `pub Point.origin() { ... }`) bind `self`, run the field-init chain, cannot `ret`urn, and chain via `super(...)`. A `factory` constructor (`pub factory Color.rgb(...) { ret ... }`, unnamed or named) has no `self`, runs no field-init, and **must** `ret` a value assignable to the enclosing type (subtype OK; explicit `-> T?`/`Result` widens it). `FunctionDefinitionNode` carries `IsFactory` + `ConstructorName` (null = unnamed); `IsConstructor` keeps meaning *generative*. Construction funnels through the single core `ClassTypeValue.Construct(args, named, typeArgs, ctorName, callSite, …)` — reached for `T(args)` via `FunctionCallExecutor.Invoke` (the universal call chokepoint, passing the live call-site context) and for `T.name(args)` via a `BoundConstructorValue` thunk returned by `MemberAccessHelper`. **Visibility:** the unnamed `T(...)` is always public (backward-compatible — every legacy ctor is unnamed); a named ctor is private unless `pub`, gated by `Context.CurrentClassMethodOwner` so factories/static contexts (no `self`) can still reach their own private allocators. Diagnostics: RA0412 private, RA0413 ambiguous, RA0414 factory-return, RA0415 unknown-name, plus a Levenshtein "did you mean" on member-access misses. Generic named/factory ctors are written `Box<int>.of(...)` — the expression parser commits speculative generic args on a following `.`. `.rac` payload is V5 (gated; V4 still loads). Design in [RA_CONSTRUCTORS_DESIGN.md](RA_CONSTRUCTORS_DESIGN.md). Hard-asserted tests at `tests_constructors.ra`; microbench at `bench_constructors.ra`.

### Language Server (LSP)

`ra --lsp` runs a Language Server Protocol backend for editor integration (the VS Code client lives in `Ra Language Support VS Code Extension/`, a thin `vscode-languageclient` ^9 client). The server lives under [LanguageServer/](LanguageServer) and depends only on the front-end — lexer, parser, `SymbolTable`, `StaticAnalyzer` — **never the VM/IR**: all tooling analysis funnels through `Compilation/ToolingCompiler` (lexer + parser + warning-only static pass), cached per text-version on `Workspace/RaDocument`. The mode branches at the top of `Program.MainCore` before any stdout-touching setup, because STDOUT is reserved for JSON-RPC framing (`Transport/LspConnection`) and all logs go to STDERR (`Transport/LspLogger`).

- **No OmniSharp / StreamJsonRpc dependency.** Both lean on reflection / DI / MediatR that breaks under this project's `PublishAot` + `TrimMode=link` + `PublishTrimmed`. The JSON-RPC base protocol (Content-Length framing) is hand-rolled, and **every wire (de)serialisation goes through the `System.Text.Json` source-generated `Protocol/RaLspJsonContext`** — under `PublishTrimmed` reflection-based JSON auto-disables, so each request/result/notification type **must** be registered there with `[JsonSerializable]`. This is verified: NativeAOT publish emits zero IL2026/IL3050 from `LanguageServer/`.
- JSON-RPC `id` is echoed as a raw `JsonElement`; dispatch keys off the `method` string into concrete params (sidesteps STJ polymorphism limits). LSP enums are wire integers → plain numeric C# enums (no string converter).
- `LspServer` is a single-threaded read pump routing to one stateless service per feature in `Features/` (each behind an interface in `FeatureServices.cs`): diagnostics (debounced push), semantic tokens, hover, completion, signature help, definition, references, document highlight, document symbols, folding, selection ranges, rename. Token-driven features stay correct on broken input; AST-driven features use `Features/SymbolIndex` (an outline walker over the declaration nodes). All ranges derive from absolute token/AST `Idx` through `Workspace/TextDocument`'s `LineIndex` (sidesteps the lexer's CRLF column quirk). definition/references/rename are name-based within a single document (documented v1 boundary; a real binder would tighten scope/overload resolution).
- **Adding a feature:** register its wire types in `RaLspJsonContext`, add the service (+ interface in `FeatureServices.cs`), add a `method` case in `LspServer.HandleRequest`/`HandleNotification`, and advertise it in `BuildCapabilities`.

## Conventions

- Mirror the `Parser/Nodes/<Category>` ↔ `Interpreter/Visitors/<Category>` split when adding language features. Failing to register a new node in `Interpreter.RegisterVisitors` is the most common breakage.
- `Program.GlobalSymbolTable` and `MetadataRegistry.Global` are reinitialised on every menu-driven script run. Anything that needs to survive across runs must live elsewhere.
- The Debug and Release configurations both set `<DebugType>none</DebugType>` — no PDBs are emitted. If you need a debugger, change that in `RaLanguage.csproj` locally.
- `PublishAot=true`: avoid reflection-heavy code paths that aren't already trimmer-safe. Existing code already pays the AOT tax; new code should too.
- The interactive `[3]` Hot Restart mode runs forever (no break in the inner `while`) and watches `main.ra` at 100 ms intervals — use `Ctrl+C` to exit.

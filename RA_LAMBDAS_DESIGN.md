# Ra Lambdas — Design (v2)

Status: shipping in this milestone. Builds on the delegate / records / events
ladder; no new opcodes, no new AST nodes, no new visitors.

## 1. Mission

Ra has always had anonymous functions through the `fn` form
(`fn(x: int): int { ret x * 2 }` and `fn(x) => x + 1`). v2 adds a
**bar-style** lambda — `|x| body` — that closes the ergonomic gap with
Rust / Kotlin / Swift while keeping the existing runtime spine, IR/VM
contract, capture model, and delegate structural typing **untouched**.

The pain we avoid:

* **Verbosity** of `fn(x) => …` for the common ad-hoc lambda.
* **Two unrelated runtime types** for "callable" — every Ra callable
  remains a `BaseFunctionValue` (see [`RA_DELEGATES_DESIGN.md`](RA_DELEGATES_DESIGN.md)).
* **Magic implicit `it`** (Kotlin / Groovy) — explicit parameter names
  cost two extra characters and remove an entire class of confusion.
* **Closure conversion at every call** — captures are frozen *once*
  at definition time, exactly like the existing `fn[...](...)` form.

## 2. Syntax

```ra
||                            // zero-arg, expression body to follow
|| 42                         // zero-arg, returns 42
| | "spaced"                  // synonym for `||` (lexer cannot fuse across whitespace)

|x| x + 1                     // single-param, expression body
|x, y| x + y                  // multi-param, expression body
|x| { ret x * x }             // single-param, block body

|x: int| -> int x * x         // typed param, typed return, expression body
|x: int, y: int| -> int {     // typed param + return + block body
    ret x + y
}

[a, &b, &mut c, move d] |x| x + a + b + c + d
                              // explicit capture clause precedes the bars
[] |x| x                      // empty capture clause: no implicit captures
                              // would land here either
```

Grammar additions, mirrored exactly in [`Parser/Parser.Lambdas.cs`](Parser/Parser.Lambdas.cs):

```
atom              ::= bar_lambda | …
bar_lambda        ::= [capture_clause] (zero_arg_bars | param_bars)
                      ['->' return_type] (block_body | expression_body)
zero_arg_bars     ::= '||'                   -- lexed as Keyword.Or
                    | '|' '|'                -- same shape across whitespace
param_bars        ::= '|' param (',' param)* '|'
param             ::= ['ref'] IDENT [':' type]
capture_clause    ::= '[' capture_list ']'   -- shared with fn(...)
block_body        ::= '{' statements '}'
expression_body   ::= expression             -- auto-returned
```

### 2.1 Disambiguation

At **atom position** (the start of an expression), `|` (`BITWISE_OR`) and
`||` (lexed as `Keyword.Or`) cannot be a binary operator — there is no
left-hand operand yet. The atom parser unambiguously reinterprets both
as lambda openers. The pattern is the same one Ra already uses for `&place`
(atom-pos `BITWISE_AND` → borrow) and `*place` (atom-pos `MUL` → deref).

At **infix position**, `|` continues to be bitwise OR and `||` continues
to be logical OR. Existing tests (`tests_shifts.ra`, the bitwise corpus,
`tests_events.ra`, every short-circuit guard) keep passing unmodified.

For `[ … ]` immediately followed by a lambda opener, the parser probes:
`ParseOptionalCaptureList` is run against the saved cursor; if it
succeeds *and* the next token is a lambda opener, the bracket is treated
as a capture clause. If either condition fails the cursor is rolled
back and the bracket falls through to the existing list-literal path.

## 3. Semantics

### 3.1 What a bar-lambda *is*

Exactly a `FunctionDefinitionNode` with `VarNameTok = null`. Every layer
downstream is the same code path the existing anonymous-`fn` form takes:

| Layer | Behaviour |
| ----- | --------- |
| Lexer | unchanged — `|` and `||` are already in the token table |
| Parser | new `Parser.Lambdas.cs` emits `FunctionDefinitionNode` |
| `Resolver` | sees the same node; allocates `FrameId`, `ParamBindings`, `ResolvedCaptures` exactly as for any anonymous function |
| `IrCompiler` | the existing `OP_DefineFunction` (`0x8F`) path emits `(FuncDefRefs idx, dst)` |
| VM | the existing `OP_DefineFunction` handler calls `FunctionDefinitionHelper.Apply` |
| `FunctionDefinitionHelper.Apply` | constructs a `FunctionValue`, freezes captures, IR-compiles the body |
| `BaseFunctionValue.FreezeCaptures` | materialises the `CaptureSpec` ladder (ByValue / ByRef / ByMove) |
| `FunctionValue.Execute` | dispatches through `VmExecutor` against the cached `RaFunction` |

The contract: **a bar-lambda is indistinguishable, after parsing, from
the existing `fn(...) => body` anonymous form.**

### 3.2 Capture model

The capture model is unified across both lambda forms and follows
[`Parser/Nodes/Functions/CaptureSpec.cs`](Parser/Nodes/Functions/CaptureSpec.cs):

* **Implicit (no `[...]`)** — the Resolver walks the body, marks each
  free variable as `BindingKind.Capture`, and records it in
  `ResolvedCaptures`. The lexical chain is still available, so calls
  to sibling top-level functions, namespace members and built-ins
  continue to work without ceremony.
* **Explicit `[name]`** — `CaptureMode.ByValue`. Snapshot at definition
  time. The body sees a frozen `Aliased()` copy; later writes to the
  outer binding do not propagate.
* **Explicit `[&name]`** — `CaptureMode.ByRef`, shared (`&`).
  Materialised as a `BorrowValue`. Read through `*name` inside the
  body; the outer binding is locked against writes while the borrow
  is alive (`SharedBorrowCount`).
* **Explicit `[&mut name]`** — `CaptureMode.ByRef` with
  `IsMutableBorrow = true`. Exclusive borrow. The body can both read
  (`*name`) and write (`*name = …`) through the borrow; the outer
  binding is locked against any access while the borrow is alive.
* **Explicit `[move name]`** — `CaptureMode.ByMove`. Transfers
  ownership. The outer binding is marked `IsMoved`; using it again
  outside the closure is a borrow-checker error.

The `[capture] | … |` form is therefore strictly *additive*: it shadows
specific names but never severs the implicit lexical chain to top-level
bindings.

### 3.3 Return value

* **Expression body** — `ShouldAutoReturn = true`. The body's evaluated
  value is the function's return value. Matches `fn(x) => expr`.
* **Block body** — `ShouldAutoReturn = false`. The body runs as a
  statement sequence; the lambda returns `null` unless the body
  contains an explicit `ret`. Matches `fn(x) { … }`.

### 3.4 Types and target-typing

A bar-lambda participates in the same structural delegate type system
as every other callable (see `RA_DELEGATES_DESIGN.md`):

```ra
let pred: fn(int) -> bool = |x| x > 0
let combine: fn(int, int) -> int = |a, b| a + b
delegate Mapper<T, U> = fn(T) -> U
let stringify: Mapper<int, string> = |n: int| -> string $"n=${n}"
```

Untyped parameters default to `any`; explicit annotations narrow the
formal type. Type-inference flows through the existing
`TypeSystem.InferBindingsFromArgs` path — the same one named functions
use.

### 3.5 Recursion

A bar-lambda is anonymous and cannot self-reference by name (the
Resolver evaluates the let initialiser *before* binding the let
target). Two escape hatches, in order of preference:

1. Use the named-`fn` form: `fn fact(n) => n <= 1 ? 1 : n * fact(n - 1)`.
2. Pass the function to itself (Y-style): `let fact = |me, n| n <= 1 ? 1 : n * me(me, n - 1); fact(fact, 5)`.

A future `let rec` form, which would bind the target before walking the
initialiser, is explicitly out of scope for v2.

## 4. Runtime + performance

* **No new opcodes.** The IR emits `OP_DefineFunction` exactly as for
  any `FunctionDefinitionNode`. Every IC, frame-pool, body-cache
  optimisation already in place applies.
* **Zero-capture lambdas** allocate one `FunctionValue` at the
  `OP_DefineFunction` PC. The body's `CompiledBody` is cached on the
  AST node, so re-evaluating the same definition site reuses the
  compiled `RaFunction`.
* **Lambdas with captures** pay one `FreezeCaptures` dictionary build
  at definition time; per-call cost is identical to the existing
  `fn[...]` form.
* **NativeAOT.** No reflection. No `IL.Emit`. No `Delegate.DynamicInvoke`.
  Every dispatch goes through the same `BaseFunctionValue.Execute` /
  `VmExecutor` path the rest of the language uses.
* **Compatibility with delegate operators.** Bar-lambdas inherit the
  multicast `+ / -` operators, `partial`, `compose`, `invoke`,
  `handler_count`, and the structural variance rules described in
  `RA_DELEGATES_DESIGN.md` without additional plumbing.

## 5. Diagnostics

The parser fast-fails with precise messages:

* Missing closing `|` → `expected ',' (continuing the parameter list) or '|' (closing it)`.
* Garbage in `[...]` capture clause → falls through to the list-literal
  diagnostics, so `[1, 2]` keeps producing a list-literal error if it
  was meant as a capture clause.
* Missing body → ParseExpression's own diagnostic is preserved, since
  wrapping it would erase the precise inner failure.
* Bad parameter token → "expected an identifier after lambda parameter
  list `|`" with the help text "bar-style lambda parameters look like
  `|x|`, `|x, y|`, or `|x: int|`".

Runtime diagnostics for captured names (moved-out, double-`&mut`,
mixing `&mut` with live `&`, etc.) are inherited verbatim from
`BaseFunctionValue.FreezeCaptures` — bar-lambdas reuse the same
materialisation path.

## 6. Test matrix

[`tests_lambdas.ra`](tests_lambdas.ra) — 30 ordered checks:

| ID | What it exercises |
| -- | ----------------- |
| L1 | `||` zero-arg, expression body |
| L2 | `| |` zero-arg with whitespace |
| L3 | single-param, expression body |
| L4 | multi-param |
| L5 | typed parameters |
| L6 | typed params + typed return |
| L7 | block body |
| L8 | implicit closure capture |
| L9 | explicit `[name]` ByValue snapshot |
| L10 | explicit `[&name]` shared borrow + deref |
| L11 | lambdas as `fn(int) -> int` arguments to a `map_list` higher-order |
| L12 | closure factory `make_adder` returns a lambda |
| L13 | nested lambdas (curry) |
| L14 | ternary body |
| L15 | nested ternary body |
| L16 | `[&mut name]` mutates through `*name` |
| L17 | `compose(...)` / `partial(...)` with lambdas |
| L18 | multicast `+` mixes a named fn with a lambda handler |
| L19 | recursion via Y-style self-pass |
| L20 | loop-built lambdas with distinct per-iteration captures |
| L21 | list literal vs capture-clause disambiguation |
| L22 | empty capture clause `[] |x| body` |
| L23 | target-typed assignment to `fn(int) -> bool` |
| L24 | IIFE `(|x| x*x)(11)` |
| L25 | lambda nested inside a block lambda |
| L26 | passing typed lambdas to higher-order fns |
| L27 | block body returning a tuple |
| L28 | lambda chosen by ternary, then invoked |
| L29 | mixed capture clause: `[snap, &live]` |
| L30 | generic-typed delegate alias `Mapper<T, U>` |

The full suite passes; the existing `tests_delegates.ra`,
`tests_shifts.ra`, `tests_properties.ra`, and `tests_events.ra`
regress without modification.

## 7. Out of scope (deferred)

* **`it` implicit parameter** (Kotlin) — would conflict with Ra's
  `{ … }` block-as-scope and create a context-sensitive grammar.
* **Trailing-lambda call syntax** (`foo { |x| body }` with the lambda
  outside the parens) — adds ambiguity around `{` for which the
  existing record / class / loop bodies have first claim.
* **`move |x| body`** — a `move`-keyword prefix that rewrites every
  implicit capture into `[move name]`. Reachable today via the
  explicit capture clause; the keyword form is a v3 ergonomic.
* **`let rec`** — would allow `let fact = |n| n <= 1 ? 1 : n * fact(n-1)`.
  Requires a Resolver change ("bind the let name before walking its
  initialiser") that is out of scope for v2.

## 8. Migration / compatibility

* All previously-valid Ra programs keep parsing identically.
* The `|` and `||` tokens behave unchanged at infix position.
* `OP_DefineFunction` (`0x8F`) is untouched. The bytecode emitted for
  `let f = fn(x) => x + 1` and `let f = |x| x + 1` is the same
  instruction stream — modulo the source positions stamped on the
  underlying `FunctionDefinitionNode`.
* Delegate type tests, multicast operators, contract / annotation
  hooks, async dispatch, and trait method binding all continue to
  observe bar-lambdas as ordinary callables.

# Ra Predicates — Design (v1)

Status: shipping in this milestone. Builds on the lambdas / delegates / records
ladder; **no new opcodes, no new AST node kinds, no new visitors.**

## 1. Mission

A predicate is the most-written abstraction in real code — every filter,
guard, validation, route, authorization rule and search is "a function that
answers yes/no". Most languages leave it as a bare `Func<T,bool>` / `Predicate<T>`
/ `(T) -> Boolean` and bolt composition on as an afterthought (static helper
methods, extension chains, operator-less `.and().or()` ladders). Ra makes the
predicate a **first-class, composable, narrowing-aware boolean function** with a
single spelling and a closed algebra.

The goals, in priority order:

1. **Semantics first.** A predicate is a `PredicateValue : BaseFunctionValue`
   whose call result is *always* a `BooleanValue` — zero ambiguity between "a
   function that happens to return bool" and "a predicate".
2. **One concept, one spelling.** Composition is the operators `&` / `|` / `!`
   (plus the method forms for the operator-less combinators). There is exactly
   one way to write "p and q".
3. **Closed, short-circuiting algebra.** `p & q`, `p | q`, `!p`, `p.xor(q)`,
   `p.implies(q)`, `p.iff(q)` all yield predicates; `(p & q)(x)` evaluates
   `q(x)` only when `p(x)` held.
4. **Depth without overhead.** A thin `BaseFunctionValue` subclass — no
   reflection, no boxing beyond the existing call path, NativeAOT-clean,
   compose-time algebraic folds.
5. **Reach into the type system.** A `param is T` predicate is recognised as a
   *type guard*; a call `p(v)` is reasoned about statically exactly like an
   inline `v is T`.

## 2. Syntax

```ra
pred even(n: int) => n % 2 == 0          // named, arrow body
pred adult(p: Person) { ret p.age >= 18 } // named, block body
let big = pred(n) => n > 100             // anonymous literal
pub pred valid_id(s: string) => len(s) == 8   // pub declaration

let ok = adult & verified & !banned       // composition operators
let either = even.xor(positive)            // method forms
let slot: pred<int> = even                 // pred<T> type sugar
```

Grammar, implemented parser-only in
[`Parser/Parser.Predicates.cs`](Parser/Parser.Predicates.cs) and hooked at the
declaration + atom positions of
[`Parser/Parser.Declarations.cs`](Parser/Parser.Declarations.cs) /
[`Parser/Parser.Expressions.cs`](Parser/Parser.Expressions.cs):

```
atom            ::= 'pred' predicate_tail | …
pred_decl       ::= ['pub'] 'pred' predicate_tail        -- statement position
predicate_tail  ::= [IDENT] [generics] [capture] '(' params? ')'
                    [':' 'bool'] (arrow_body | block_body)
arrow_body      ::= '=>' expression                      -- auto-returned
block_body      ::= '{' statements '}'
type            ::= 'pred' '<' type (',' type)* '>' | …  -- pred<T> sugar
```

A predicate returns `bool` **by definition**. The return contract is defaulted
when unannotated; an explicit non-`bool` return type is rejected (RA0209) so the
marker can never lie about its result.

`pred` is a reserved keyword. The only migration cost in the corpus was two
files using `pred` as an identifier (renamed).

## 3. Semantics

### 3.1 What a predicate *is*

Exactly a `FunctionDefinitionNode` with `IsPredicate = true` (and
`VarNameTok = null` for the anonymous literal, identical to a lambda). Every
layer downstream is the same code path an anonymous `fn` takes — the **only**
additions are the marker, the `bool` return contract, and the leaf wrapper:

| Layer | Behaviour |
| ----- | --------- |
| Lexer | new keyword `pred` (`Keyword.Pred`); nothing else |
| Parser | `Parser.Predicates.cs` emits a `FunctionDefinitionNode` with `IsPredicate = true`; `DetectNarrowingGuard` records a `param is T` body |
| `Resolver` | sees the same node; allocates `FrameId`, `ParamBindings`, `ResolvedCaptures` as for any function |
| `IrCompiler` | the existing `OP_DefineFunction` (`0x8F`) path — no new opcode |
| `FunctionDefinitionHelper.Apply` | builds the `FunctionValue`, then wraps it in `PredicateValue.Leaf(...)` (threading any guard metadata) |
| VM | `&` / `|` ride the generic `BAnd` / `BOr` paths → `BitwiseAndedBy` / `BitwiseOredBy`; `!` rides `NotB` → `Notted()`; `p(x)` rides the universal call chokepoint |
| Runtime | `PredicateValue.Execute` short-circuits composites and always returns a `BooleanValue` |

The contract: **a predicate IS-A function** (usable anywhere `fn(T) -> bool` is
wanted) that additionally carries composition + narrowing semantics. Zero new
opcodes, zero new AST node kinds, zero new visitors.

### 3.2 Composition algebra

[`PredicateValue`](Interpreter/Values/Functions/Predicates/PredicateValue.cs) is
a tagged tree (`Leaf` / `Not` / `And` / `Or` / `Xor` / `Const`):

* **`&` / `|` / `!`** are the and / or / not operators. They dispatch through the
  existing operator overrides (`BitwiseAndedBy` / `BitwiseOredBy` / `Notted`), so
  one override covers both the AST walk and the IR. The right operand is
  auto-lifted: a plain `fn(T) -> bool` on the RHS of `&` / `|` is wrapped into a
  leaf, so only the left side must be a predicate to anchor a chain.
* **`and` / `or` / `not`** are deliberately **not** overloaded. They are Ra
  boolean control-flow keywords that the IR lowers to truthiness short-circuit
  jumps (`OP_AND_JZ` / `OP_OR_JNZ`); overloading them would silently diverge the
  AST and IR. Predicate composition therefore has exactly one spelling.
* **`^` is exponent (pow), not XOR.** XOR is the method `.xor(q)`.
* **Methods** cover the operator-less combinators: `.negate()`, `.xor(q)`,
  `.implies(q)`, `.iff(q)`, plus `.test(x)` (the explicit spelling of `p(x)`).
  `and` / `or` / `not` cannot be method names (the member-access parser requires
  an identifier after `.`, and those are keywords) — which is exactly why they
  are the operators.

**Short-circuit.** `(p & q)(x)` evaluates `p(x)`; only if it held does it
evaluate `q(x)`. `(p | q)(x)` evaluates `q(x)` only if `p(x)` did *not* hold.
`.xor` evaluates both by definition.

**Compose-time folds** (in `PredicateValue`):

* `!!p → p` — double negation collapses.
* `!always_true → always_false` (and vice-versa).
* `always_true & q → q`, `always_false & q → always_false`,
  `always_true | q → always_true`, `always_false | q → q`,
  `p & always_true → p`, `p | always_false → p`.

The folds are **side-effect-preserving**: an operand is dropped only when the
runtime would skip its evaluation anyway (a constant in the short-circuited
position) or when it is a no-op constant. `p & always_false` and
`p | always_true` are deliberately *not* folded — `p` still runs first.

### 3.3 Typing — `pred<T>` and `fn` interop

`pred<T>` / `Pred<T>` is sugar for `fn(T) -> bool` (`pred<A, B>` →
`fn(A,B) -> bool`, bare `pred` → `fn(any) -> bool`), implemented in
[`Parser/Parser.Types.cs`](Parser/Parser.Types.cs). A predicate (leaf or
composite) and a plain lambda both satisfy a `pred<int>` slot, and a predicate
satisfies an `fn(int) -> bool` slot — the structural delegate type system
(`RA_DELEGATES_DESIGN.md`) sees a predicate as the function it is.

### 3.4 Stdlib higher-order functions

Predicate-driven list HOFs live in
[`CollectionBuiltins.cs`](Interpreter/Values/Functions/Builtins/CollectionBuiltins.cs)
(group `collections` → `std.prelude.collections`). Each accepts **any** callable
(a `pred`, a lambda, or a plain `fn(T) -> bool`) and short-circuits where the
semantics allow:

| Builtin | Result |
| ------- | ------ |
| `filter(xs, p)` / `reject(xs, p)` | new list of matching / non-matching elements |
| `find(xs, p)` | first matching element, or `null` |
| `find_index(xs, p)` | index of first match, or `-1` |
| `any(xs, p)` / `all(xs, p)` / `none(xs, p)` | `bool` (∃ / ∀ / ¬∃), short-circuiting |
| `count(xs, p)` | number of matches |
| `partition(xs, p)` | `(matching, non_matching)` tuple of two lists |
| `take_while(xs, p)` / `drop_while(xs, p)` | prefix / suffix by predicate |

Point-free combinators live in
[`PredicateBuiltins.cs`](Interpreter/Values/Functions/Builtins/PredicateBuiltins.cs)
(group `func` → `std.prelude.func`):

| Combinator | Meaning |
| ---------- | ------- |
| `pred_all(p…)` | holds when EVERY argument holds (AND); empty ⇒ `always_true` |
| `pred_any(p…)` | holds when ANY argument holds (OR); empty ⇒ `always_false` |
| `pred_none(p…)` | holds when NO argument holds (`!pred_any`) |
| `negate(p)` | the negation of a predicate / callable |
| `always_true` / `always_false` | constant predicates (callable on any element) |

The combinator names mirror the quantifier HOFs (`all` / `any` / `none`) so the
vocabulary is learned once. They carry the `pred_` prefix because the bare
`all_of` / `any_of` / `none_of` spellings are reserved by the built-in
`@all_of` / `@any_of` **validator annotations** — see §8.

### 3.5 Type-guard predicates (narrowing)

When a predicate's whole body is exactly `param is T` (or `param is not T`)
testing its sole parameter, the parser (`DetectNarrowingGuard`) records the
refined parameter + tested type on the node. This feeds two consumers from one
detection point:

* the runtime `PredicateValue` carries the guard metadata (`NarrowsParam` /
  `NarrowsTo` / `NarrowsNegated`) for future reflection;
* the [`NarrowingAnalyzer`](Interpreter/Runtime/Narrowing/NarrowingAnalyzer.cs)
  treats a call `p(v)` exactly like an inline `v is T`.

```ra
pred is_text(x: any) => x is string

let n: int = 1
if is_text(n) { … }      // warning: guard can never hold — branch unreachable
if !is_text(n) { … }     // warning: guard always holds — test is redundant
```

Ra's narrowing analyzer is **compile-time, diagnostics-only** — it flags
impossible / redundant tests; it does not flow-type the branch body (Ra is
dynamically typed, so member access inside the branch already works regardless
of declared type). Predicate guards get precisely the same treatment inline `is`
gets, extended across the predicate abstraction boundary. The guard body's
`is not` and a call-site `!` fold together to pick the verdict, so all four
combinations (`p(v)`, `!p(v)`, `is`-guard, `is not`-guard) report correctly.

## 4. Runtime + performance

* **No new opcodes.** `&` / `|` ride `BAnd` / `BOr`; `!` rides `NotB`; calls ride
  the universal chokepoint; member access rides `OP_GET_MEMBER`. Every IC,
  frame-pool and body-cache optimisation already in place applies.
* **Leaf predicates** add one thin `PredicateValue` wrapper over the underlying
  `FunctionValue`; the call result is the singleton `BooleanValue.True/False`.
  The microbench (`bench/bench_predicates.ra`) shows a leaf predicate call
  tracking a raw `fn(int) -> bool` call.
* **Composites** are immutable trees; `Execute` walks them with short-circuit
  and no allocation beyond the boolean result.
* **Folds** run at compose time, never at call time.
* **NativeAOT.** No reflection, no `IL.Emit`. `PredicateValue` is a sealed
  `BaseFunctionValue` subclass; NativeAOT publish stays IL2026/IL3050-clean for
  the predicate subsystem.

## 5. Diagnostics

| Code | When |
| ---- | ---- |
| **RA0209** `predicate-return-type` | a `pred` declares an explicit non-`bool` return type — *"a predicate must return 'bool'… use 'fn' if you need a non-bool result."* |
| **RA0416** `predicate-composition` | `p & x` / `p | x` where `x` is not callable — *"predicate composition needs a predicate or an `fn(T) -> bool` on both sides."* |
| RA0402 (+ help) | an unknown predicate method (`p.frobnicate()`) — rides the member-not-found path with a predicate-specific help line |
| NarrowingAnalyzer warning | a guard call `p(v)` that can never hold (unreachable branch) or always holds (redundant test) |

## 6. Competitive comparison

| Capability | C# `Func<T,bool>` | Java `Predicate<T>` | Kotlin `(T)->Boolean` | Dart `bool Function(T)` | Python `Callable` | C++ lambda | **Ra `pred`** |
| ---------- | --- | --- | --- | --- | --- | --- | --- |
| Distinct predicate type | ✗ (bare `Func`) | ✓ interface | ✗ (typealias) | ✗ | ✗ | ✗ | ✓ `PredicateValue` |
| `and` / `or` / `not` as **operators** | ✗ (`.And`/`&` on `Expression` only) | ✗ (`.and()/.or()/.negate()`) | ✗ | ✗ | ✗ | ✗ | ✓ `& \| !` |
| Method combinators | ✗ | ✓ | ✗ | ✗ | ✗ | ✗ | ✓ `.xor/.implies/.iff/.negate` |
| Short-circuit composite | n/a | ✓ | n/a | n/a | n/a | n/a | ✓ |
| Compose-time algebraic folds | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✓ (`!!`, constant folds) |
| First-class type guard | partial (pattern `is`) | ✗ | ✓ (contracts, limited) | ✗ | `TypeGuard` (3.10+, types only) | ✗ | ✓ guard-aware diagnostics |
| Built-in predicate HOFs | LINQ | Streams | stdlib | `where`/`any` | `filter`/`any` | ranges (C++20) | ✓ `filter/any/all/none/count/partition/…` |
| Point-free combinators | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✓ `pred_all/pred_any/pred_none/negate` |
| AOT-friendly (no reflection) | ✗ (`Expression`) | ✓ | ✓ | ✓ | n/a | ✓ | ✓ |

Where the others force a choice — *bare lambda* (no algebra) **or** *expression
trees / wrapper classes* (algebra, but reflection-heavy and verbose) — Ra gives
the algebra on the lambda itself, with operators, short-circuit, folds and
guard-awareness, and no reflection.

## 7. Test matrix

[`tests/functions/test_predicates.ra`](bin/x64/Release/net10.0/tests/functions/test_predicates.ra)
— 60 hard-asserting checks (every check prints `OK …` or throws):

| Group | Coverage |
| ----- | -------- |
| P1–P3 | named arrow / named block / anonymous-literal declaration + call |
| P4–P6 | `&` / `|` / `!` composites |
| P7–P8 | AND / OR short-circuit, proved with a throwing RHS predicate |
| P9 | `!!p` identity fold |
| M1–M5 | `.negate` / `.test` / `.xor` / `.implies` / `.iff` |
| T1–T4 | `pred<int>` slot + `fn(int) -> bool` interop (predicate & composite) |
| H1–H12 | every HOF (`filter/reject/count/any/all/none/find/find_index/partition/take_while/drop_while`) incl. plain-`fn` acceptance |
| C1–C9 | `pred_all/pred_any/pred_none/negate/always_true/always_false`, empty-fold identities, constant fold |
| G1–G2 | runtime type-guard behaviour |
| N1–N2 | rejected: compose-with-non-callable, unknown method |

Microbench: [`bench/bench_predicates.ra`](bin/x64/Release/net10.0/bench/bench_predicates.ra).
The full 324-file corpus regresses without modification (zero new failures).

## 8. Out of scope (deferred)

* **Composite-predicate narrowing.** Only a leaf `param is T` is a guard; a
  composite (`is_text & nonempty`) is not propagated as a guard. The runtime
  behaviour is correct regardless; only the static diagnostic is leaf-only.
* **Argument-type compatibility on composition.** `p & q` does not statically
  check that `p` and `q` accept the same parameter type — both are called with
  the same argument at runtime, and a mismatch surfaces there.
* **True flow-typing.** Injecting a narrowed binding that a type checker
  consumes inside `if p(v) { … }` is a whole-language feature absent for inline
  `is` today; building it only for predicate guards would be inconsistent. The
  guard support here is diagnostics-only, matching inline `is`.

## 9. Migration / compatibility

* `pred` is now a reserved keyword (two corpus files renamed an identifier).
* `all_of` / `any_of` / `none_of` remain the built-in **validator annotation**
  names (`@all_of`, `@any_of`); the predicate combinators use the `pred_`
  prefix to stay unambiguous. (A bare `all_of(...)` call resolves to the
  always-in-scope annotation type and is not callable — hence the rename.)
* `&` / `|` / `!` behave unchanged for every non-predicate operand; the
  predicate overrides only fire when the left operand is a `PredicateValue`.
* `^` is unchanged (exponent). `and` / `or` / `not` are unchanged
  (boolean control-flow).
* No bytecode change: a predicate compiles to the same `OP_DefineFunction`
  instruction stream a function does, modulo the leaf wrapper applied at
  definition time.

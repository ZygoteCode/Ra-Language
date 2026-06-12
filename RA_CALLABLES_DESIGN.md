# Ra Callables — Design (callable system increment)

Status: shipping. Builds on the lambdas / delegates / predicates / extensions
ladder. **No new opcodes, no new AST node kinds, no new visitors.**

## 1. Mission

Ra's callable model was already unified and deep — every callable IS a
`BaseFunctionValue` (lambdas `|x|`, anonymous/named `fn`, `pred`, bound
methods, extension methods, multicast delegates, partial/composed values),
flowing through one call chokepoint (`FunctionCallExecutor.Invoke`), with a
variance-aware structural `fn(P…) -> R` type system, closures (value/ref/move
capture), and `extend T` extensions that take and return callables.

This increment closes the two remaining gaps against the brief — a complete
**transform-HOF surface** and **compiler-grade callable diagnostics** — without
adding a second, fragmented way to do anything (the brief's principle #2).

## 2. Transform / aggregate higher-order functions

The predicate HOFs (`filter`/`any`/`all`/…) already existed; the *transform*
side did not (it existed only for streams). Now, as free functions over lists
in [`CollectionBuiltins.cs`](Interpreter/Values/Functions/Builtins/CollectionBuiltins.cs)
(group `collections` → `std.prelude.collections`), each taking ANY callable —
a `pred`, a bar-lambda, or a plain `fn`:

| Builtin | Result |
| ------- | ------ |
| `map(xs, f)` | each element transformed |
| `flat_map(xs, f)` | map then flatten one level |
| `for_each(xs, f)` | side-effect over each; returns `xs` (fluent) |
| `reduce(xs, init, f)` / `fold(xs, init, f)` | left fold `f(acc, x)` |
| `sort_with(xs, cmp)` | sort by a comparator `cmp(a, b)` (truthy ⇒ a before b) |
| `sort_by(xs, key)` | sort ascending by a key extractor (keys precomputed, O(n) calls) |
| `group_by(xs, key)` | `map` of key → list of members |
| `zip_with(xs, ys, f)` | pairwise `f(a, b)`, truncated to the shorter |
| `min_by` / `max_by` / `sum_by` `(xs, key)` | extremum / sum over a key |

Free-function spelling is deliberate: it matches the existing stdlib (and the
predicate HOFs), so `map(xs, f)` reads the same as `filter(xs, p)`. Method-style
`xs.map(f)` is reachable today by `extend list { … }` and is left as opt-in
sugar rather than a parallel built-in surface.

A user callable that errors mid-traversal propagates cleanly (including through
`sort_with`'s comparison, via a captured-error sentinel) — no traversal swallows
a failure.

### Fluent method form (Dart / C# / JS-style)

The same free functions are ALSO callable as **methods** on a list —
`xs.map(f).filter(p).sort_by(k).take(3)` — for a fluent, chainable surface that
reads like LINQ / Dart collections. **Always on** (no import) and **additive**
(the free functions are unchanged).

* Member access on a list resolves user `extend` methods FIRST, then falls back
  to a built-in fluent method
  ([`CollectionMethods`](Interpreter/Runtime/CollectionMethods.cs)), which binds
  a [`BoundCollectionMethodValue`](Interpreter/Values/Functions/BoundCollectionMethodValue.cs)
  that prepends the receiver and dispatches to the corresponding built-in — so
  `xs.map(f)` and `map(xs, f)` are literally the same code. A user
  `extend list { fn map … }` always wins (verified).
* The surface covers every list HOF and operation, under the canonical
  snake_case name plus the common LINQ/Dart aliases: `where`=`filter`,
  `some`=`any`, `every`=`all`, `each`=`for_each`, `distinct`=`unique`,
  `sorted`/`reversed`, `add`=`push`, and camelCase `flatMap`/`sortBy`/`groupBy`/
  `findIndex`/`takeWhile`/… .
* **Keyword member names.** Several aliases (`where`, …) are Ra keywords. The
  member-access parser now accepts a keyword after `.` as a contextual member
  name (synthesised as an identifier) — strictly more permissive (the prior
  behaviour was a hard parse error), and the enabler for the LINQ-style names.
* Lists only — the wrapped built-ins are list-typed; sets / maps reach the
  methods via `to_list()`.

## 3. Target-typing + signature diagnostics

### What "target-typing" means in Ra

Ra's runtime is dynamically (duck-) typed and has no static body type-checker.
The coherent reading of "target-typing" is therefore at the **acceptance +
diagnostic** layer:

* An **untyped** lambda is accepted into a typed callable slot as if its
  parameters took the expected types — `let f: fn(int) -> bool = |x| x > 0`
  is fine; `|x|` is target-typed to `int`. An inferable parameter never draws
  a spurious "x should be int".
* What the type system CAN know statically is enforced precisely: **arity**,
  **explicitly-typed** parameter conflicts (contravariant), and **explicit**
  return-type conflicts (covariant).

### The diagnostic

`TypeSystem.TryDescribeFunctionMismatch(target, value)` reports the first
concrete difference in *expected / found* form with an actionable hint:

```
expected a callable 'fn(int) -> bool' taking 1 argument(s), but found 'fn(any, any)' taking 2
  hint: remove 1 parameter(s) — 'fn(int) -> bool' only ever passes 1 argument(s)

callable parameter #1 is incompatible: 'fn(int) -> bool' supplies 'int', but the callable declares 'string'

callable return type is incompatible: 'fn(int) -> int' expects 'int', but the callable returns 'bool'
```

It fires (code `RA0404`) at **every** site that binds a value to a typed
callable slot, so the message reads identically everywhere — centralised in
[`CallableDiagnostics`](Interpreter/Runtime/CallableDiagnostics.cs):

| Site | File |
| ---- | ---- |
| variable declaration `let f: fn(...) = …` | `DeclarationHelper` (+ AST `VariableDeclarationNodeVisitor`) |
| assignment `f = …` | `AssignmentHelper` (+ AST `VariableAssignmentNodeVisitor`) |
| plain function argument | `FunctionValue`, `BaseFunctionValue` |
| class-method argument | `BoundClassMethodValue`, `BoundClassMethodGroupValue` |
| extension- / trait-method argument | `MethodCallBinder`, `BoundExtensionMethodGroupValue` |

For method / extension calls a wrong callable makes the candidate
*inapplicable* during overload resolution; the terminal "no matching overload"
sites re-examine the candidates (`MethodCallBinder.DescribeCallableArgMismatch`)
and surface the precise callable reason for the best-arity match instead of the
generic message.

### Why no runtime parameter-stamping

Mutating an untyped lambda's parameter types in place (so later mis-calls hard-
fail) was considered and rejected: lambdas are shared `BaseFunctionValue`s, so
stamping `let f: fn(int)->bool = g` would retroactively type `g` everywhere —
a surprising, aliasing-dependent side effect. Ra stays duck-typed at the call
boundary; the static signature checks above catch what is statically knowable
without that fragility. Full body-level inference would require a whole static
type-checker pass (a separate, language-wide feature) and is out of scope.

## 4. Competitive note

C#/Java/Kotlin defer a wrong-arity lambda to a generic "no overload" or a
delegate-conversion error; few report a *structural* `expected fn(int)->bool /
found fn(any,any)` diff with a remove-the-parameter hint at the call site. Ra
now does, uniformly across plain, method and extension calls, with no
reflection and no JIT dependency.

## 5. Tests

* [`tests/functions/test_callable_diagnostics.ra`](bin/x64/Release/net10.0/tests/functions/test_callable_diagnostics.ra)
  — 10 checks: untyped lambda accepted at every site; wrong-arity / explicit
  param / explicit return rejected at every site.
* [`tests/collections/test_hof_transform.ra`](bin/x64/Release/net10.0/tests/collections/test_hof_transform.ra)
  — 24 checks across the transform HOFs.
* [`tests/collections/test_fluent_methods.ra`](bin/x64/Release/net10.0/tests/collections/test_fluent_methods.ra)
  — 32 checks: fluent chaining, LINQ/Dart aliases, plain ops, and user-`extend`
  precedence over the built-in fluent methods.

Full corpus regresses with zero new failures.

## 6. Out of scope (deferred)

* Element-type-aware fluent methods — the fluent methods (§2) are duck-typed in
  the element (they wrap the untyped built-ins); a fully generic
  `extend List<T>` that binds `T` per receiver still needs primitive containers
  to carry per-instance generic bindings.
* Fluent methods on sets / maps / strings — lists only for now; the wrapped
  built-ins are list-typed.
* Paren-style lambda `(x) => e` — would be a third anonymous-function spelling
  alongside `|x|` and `fn`; rejected as fragmentation.
* Runtime lambda parameter inference / a static body type-checker — see §3.

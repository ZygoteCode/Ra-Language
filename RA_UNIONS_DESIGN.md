# Ra Language — Union Types & Narrowing

Status: shipped. Code lives under `Types/`, `Parser/`, `Interpreter/Visitors/Operations/`, `Interpreter/Runtime/Narrowing/`. Smoke + regression suite in `tests_unions.ra`, narrowing-diagnostic suite in `tests_unions_diag.ra`.

## What landed

- **Anonymous structural unions** at every type position: `let x: int | string`, `fn f(x: int | string): int | null`, `list<int | string>`, generic args, tuple slots.
- **Set semantics**: `int | string ≡ string | int`. Members are flattened (`(A | B) | C ≡ A | B | C`), deduplicated, and `any | T` collapses to `any`. A singleton union normalises to its bare member at construction time, so the rest of the type system never has to special-case it.
- **`expr is T` / `expr is not T`** at relational precedence — tighter than `==` / `and` / `or`, looser than additive / shift / null-coalescing. Returns `bool`.
- **`case is T as v -> body`** as a new match pattern. The binder gets the matched value, statically narrowed to `T`.
- **NarrowingAnalyzer** compile-time pass:
  - Flags impossible `is` tests (`x: int; x is string` ⇒ always false).
  - Flags trivially-true tests (`x: int; x is int` ⇒ always true).
  - Reports non-exhaustive `match` arms over a union scrutinee, naming the exact uncovered members and suggesting the missing arm or a wildcard fallback.
- **Runtime test path**: `TypeSystem.IsRuntimeTypeMatch` — stricter than the declaration-time `IsAssignable` (no permissive `string`-sponge, null only matches `null`, primitives match by exact runtime tag with `Number` accepted as a literal-shape wildcard for every numeric).
- **No new runtime structure / no new opcodes**. Unions are erased at runtime; values stay concrete `RuntimeValue`s. `IsTypeNode` routes through `OP_NATIVE_DEFINE`'s static-`Apply` dispatch — the same path lambdas, pipelines, and matches already use.

## Design decisions (and why)

### Structural anonymous unions, not nominal sums
Ra already has nominal sum types via `enum`. Adding a parallel nominal-union construct would have forced users to pick between two ways to express "one of these". A structural anonymous union is complementary: enums own the closed, payload-carrying cases; unions own the open, identity-only "one of these declared types" axis. Same way TypeScript's structural unions sit alongside Rust-style enums.

### `|` operator, lowest type-grammar precedence
Familiar from TypeScript, Python, Scala, Kotlin, Swift. Folds left-to-right, so `A | B | C` is a single 3-member union, not a 2-member union nested inside another. Folds *after* `&T`, `fn(...) -> T`, generic instantiation, and tuple parsing, so:
- `&A | B` reads as `(&A) | B` (prefix binds tighter).
- `fn() -> A | B` reads as `fn() -> (A | B)` (return-type slot is union-aware).
- `(A | B)` is a *parenthesised type* — needed for bar-lambda params where the surrounding `|` is the param-list terminator. `(A,)` is the 1-tuple.

### `is` at relational precedence
Tighter than `==` / `and` / `or`, looser than `+ - << & |`. Matches the C# / Kotlin convention. Makes `if x is int and y is str` work without parentheses, which was the single hardest constraint to satisfy.

### Pattern syntax `case is T as v ->`, not bare `case T as v`
Bare-identifier-as-type-pattern collides with the existing variable-binding pattern (`case foo -> ...`). The `is` prefix is unambiguous, lexically distinct, and visually consistent with the expression-level `is`.

### NarrowingAnalyzer is diagnostic-only (no flow-sensitive type rewrites)
Ra's static type checking is intentionally light — declared types are enforced at assignment, not at every use. Plumbing flow-sensitive refinements into every `VariableAccess` would touch the resolver, IR compiler, IC layout, and member-access pipeline. The analyzer pays for itself with two user-visible features (impossible / trivial test warnings, union-match exhaustiveness) without breaking the existing dynamic-by-default story. A future PR can promote the same scope-stack walker to annotate accesses if and when the type checker becomes prescriptive at use sites.

### `IsAssignable` vs `IsRuntimeTypeMatch`
The two are different jobs:
- `IsAssignable` is for declaration sites: `let x: int = 5.0` is fine because numerics interchange; `let x: string = 5` is also fine because Ra coerces via `ToString`; `let x: T = null` is fine for any `T`.
- `IsRuntimeTypeMatch` is what powers `is` and `case is T`: `5 is string` is false, `null is int` is false, `5.0 is int` is false (with the documented exception that the wide `Number` runtime tag matches every concrete numeric, because Ra literals default to `Number`).

Keeping these separate avoids the "`is`-test is more permissive than my eyes" footgun while preserving every existing assignability behavior.

### Lexer fix: word-boundary check for `is in` / `is not in`
The pre-existing lexer special-cased `is`, `is in`, `is not`, `is not in`, but the lookahead did not check word boundaries. `is int` would consume `is` + `in` (keyword `In`) and leave a stray `t` identifier in the stream. The fix adds a `IsWordBoundaryAt` predicate. Side effect: the legacy SQL-style `is` → `==` / `is not` → `!=` aliases are retired (they had no in-tree users), so plain `is` is now a real `Keyword.Is` token usable as the type-test operator.

## Trade-offs we made

- `Number` runtime tag passes every numeric `is` check. This is the price of Ra's literal-default-Number policy. `5 is int` is true (good — user expectation), but `5 is float` is also true (debatable — user wrote `5`, not `5.0`). The alternative — require explicit suffixes on literals — was a strictly worse user experience.
- Union members in bar-lambda parameter lists must be parenthesised: `|x: (int | string)| body`. Without parens the closing `|` of the param list would be eaten by the union folder. The grouping rule (`(T)` parses as `T`, not `(T,)`) was added specifically to make this readable. Documented in `Parser.Lambdas.cs`.
- Generic unification of unions is not implemented. Today, `unify(T, int | string)` fails — only `unify(int | string, int | string)` succeeds via set-equality. A future PR can lift this if real call-site demand appears; the current restriction never produces a wrong answer, only a "could not infer" diagnostic.
- Narrowing does not survive function-call boundaries, mutation, or closure capture. The analyzer invalidates a name's refinement on `VariableAssignmentNode` and never asserts anything across an unknown call. Soundness over completeness.

## Future work (not in this PR)

- Promote narrowing from diagnostic-only to AST-annotation, then thread refined types into member-access resolution, IC priming, and overload selection.
- Use `Is` opcode `0x8D` (reserved in the opcode table but currently unemitted) for a direct IR lowering instead of routing through `OP_NATIVE_DEFINE`. Worth ~one indirect call per `is`-test on hot paths.
- Generic union unification: `Some<T> | None` should bind `T` from a `Some(42)` call site.
- Discriminant-property inference: when every union member is a record/class with a known tag field, infer it and offer pattern matching on the tag without `is`.
- `match` arm narrowing for non-type patterns (e.g. `case Some(x) -> /* x has Some.payload type */`).

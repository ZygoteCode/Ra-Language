# RA_PATTERN_MATCHING_DESIGN

Ra Language — Extreme Pattern Matching & Total Destructuring.

This document specifies the semantic model, syntax, runtime, diagnostics, and
incremental implementation plan for Ra's pattern-matching subsystem.

Goals: out-class C#, Rust, Dart, Swift, F#, TypeScript and Python by
unifying *one* pattern grammar across `match`, `if let`, `while let`, `let`
destructuring, `foreach` destructuring, function-parameter destructuring and
lambda-parameter destructuring — with flow-sensitive narrowing, exhaustiveness
on closed type families, reachability checks, allocation-light binding
semantics, and a clean IR-lowering seam.

The starting position is already strong: Ra has `match`, eight pattern kinds,
union types with a narrowing analyzer, tagged enum variants with payload, and
record positional destructuring through `VariantPatternNode`. This design
keeps every existing pattern semantically identical and only *adds*.

---

## 1. Pattern grammar (final)

```
Pattern        := OrPattern Alias?

Alias          := 'as' IDENT                            // bind whole match

OrPattern      := AndPattern ('|' AndPattern)*          // disjunction
AndPattern     := BasePattern                           // (no '&' in v1; reserved)

BasePattern
    := '_'                                              // wildcard
     | LITERAL                                          // 5, "x", true, null, -3.14
     | RangePat                                         // 1..10, 1..=10, ..10, 5..
     | RelOpPat                                         // < 5, >= 0, != 0
     | 'is' Type                                        // type test (no binder)
     | 'is' Type 'as' IDENT                             // type test + bind
     | IDENT                                            // variable binding or 0-arity variant
     | IDENT '.' IDENT                                  // qualified 0-arity variant
     | IDENT '(' PatternList? ')'                       // variant / record positional
     | IDENT '.' IDENT '(' PatternList? ')'             // qualified variant
     | IDENT '{' FieldPats? '}'                         // struct / class / record by-name
     | '(' Pattern (',' Pattern)+ ')'                   // tuple (2+)
     | '(' Pattern ')'                                  // grouping
     | '[' ListPatElems? ']'                            // list with optional rest
     | '{' MapPatEntries? '}'                           // map with optional rest

RangePat       := RangeLit? '..' RangeLit?
                | RangeLit? '..=' RangeLit
RangeLit       := NUMBER | '-' NUMBER | STRING | 'char'

RelOpPat       := ('<' | '<=' | '>' | '>=' | '==' | '!=') LITERAL

FieldPats      := FieldPat (',' FieldPat)* (',' '..')?
FieldPat       := IDENT                                 // shorthand bind
                | IDENT ':' Pattern                     // name: subpattern

MapPatEntries  := MapEntry (',' MapEntry)* (',' '..')?
MapEntry       := Expr ':' Pattern                      // key expression : value pattern
```

Notes:

- `|` between patterns is the or-combinator. It is unambiguous in pattern
  position because the parallel pattern grammar starts only after `case`,
  `if let`, `while let`, in a destructuring `let`, in a `foreach`
  destructuring head, or inside a fn/lambda parameter binder.
- The trailing `..` inside `{ … }` and `User { … }` is an *open-rest*: extra
  fields are ignored. Without `..`, the pattern is *closed* (extra fields are
  a static warning, not an error, because runtime values may be wider).
- `..=` is the inclusive range terminator and uses the existing
  `TokenType.DOUBLE_DOT_EQ` lexeme.
- Bare-identifier variable bindings remain valid (today's behaviour) — the
  match engine treats them as zero-arity variant tests when a same-named
  zero-arity variant exists in scope, else as a binder.
- The `not` keyword is *reserved* for a future `not Pattern` form; v1 emits
  it via a guard.

### 1.1 Pattern-position contexts

Patterns appear in:

| Context | Refutability | Binding scope |
|---|---|---|
| `match … { case P [if G] -> body }` | refutable | arm body |
| `if let P = expr { … } [else { … }]` | refutable | then-branch only |
| `while let P = expr { … }` | refutable | loop body |
| `let P = expr;` (destructuring let) | **irrefutable** | enclosing scope |
| `const P = expr;` / `var P = expr;` / `final P = expr;` | irrefutable | enclosing scope |
| `foreach (P in iter) { … }` | irrefutable | loop body |
| Function parameter `fn f(P: T)` | irrefutable | function body |
| Lambda parameter `|P| body`, `|P: T| body` | irrefutable | lambda body |

A pattern is *irrefutable* iff it cannot fail at compile time for any value of
its scrutinee's declared type. v1 enforces irrefutability syntactically:

- Wildcard, variable, tuple-of-irrefutable, list-of-irrefutable *without
  rest in the middle*, struct/class/record-of-irrefutable, alias-of-irrefutable
  are irrefutable.
- Literal, range, relational, type, variant, or-pattern, map-pattern, list with
  rest *and* fixed prefix or suffix, are refutable.

Using a refutable pattern in an irrefutable context is a hard error at parse
or analysis time, with a help text proposing `match` or `if let`.

---

## 2. Semantic model

### 2.1 Scrutinee evaluation order

The scrutinee of `match` / `if let` / `while let` / destructuring `let` is
evaluated exactly once. The pattern engine walks against the resulting
`RuntimeValue` without re-evaluating sub-expressions of the scrutinee.

### 2.2 Binding semantics

Each pattern walk accumulates a `List<(string Name, RuntimeValue Value)>`
*proposed* bindings. Bindings are committed to the target scope only when the
*entire top-level pattern* matches. This is already how
`MatchNodeVisitor.TryMatch` works; we extend it to handle backtracking inside
or-patterns by snapshotting & truncating the proposed list when an alternative
fails.

Binding rules:

- A binding name may appear **at most once** in any single alternative of an
  or-pattern (caught at parse).
- All alternatives of an or-pattern must bind the **same set of names**
  (caught by analyzer; runtime error if missed).
- An alias `P as n` binds `n` to the **whole** scrutinee value at that position,
  regardless of how deep `P` recurses.
- A `..rest` in a list pattern binds a fresh `ListValue` slice; a `..` in a
  map / struct pattern binds nothing (it is purely an open-rest marker).

### 2.3 Narrowing

After a successful pattern match against a scrutinee whose declared type is
known, the analyzer narrows the *bindings* introduced by the pattern:

- `is T as v` → `v: T` in the arm body.
- Variable binding `x` over a scrutinee of declared `T` → `x: T`.
- Variant binding `Result.Ok(v)` over `Result<T, E>` → `v: T`.
- Tuple binding `(a, b)` over `(T, U)` → `a: T`, `b: U`.
- List head `[h, ..t]` over `List<T>` → `h: T`, `t: List<T>`.
- Struct binding `User { name: n, age: a }` over `User { name: string, age: int }` →
  `n: string`, `a: int`.
- Or-pattern alternatives must agree on the narrowed type of each shared
  binding; the resulting type is the union of the per-alternative types.

Narrowing is purely a static analyzer feature. Runtime is unaffected.

### 2.4 Exhaustiveness

`match` is *exhaustive* iff every value of the scrutinee's declared type is
covered by at least one arm whose guard is `null`.

The analyzer reports non-exhaustive `match` for:

- **Union scrutinee** `T1 | T2 | …` — missing alternatives reported by name.
  *(today's behaviour, kept verbatim).*
- **Enum scrutinee** of a closed enum — missing variants reported by name.
- **Boolean scrutinee** — must cover `true` and `false` (or a wildcard).
- **Open enum-like classes** are never declared exhaustive (analyzer is silent).
- An arm containing only a wildcard, a bare variable, an `is any` test, or
  an unconditional alias short-circuits the check.

Runtime missing-coverage still produces today's `no match arm covered the
scrutinee value` error — analyzer warnings are advisory.

### 2.5 Reachability

The analyzer detects arms that are *strictly dominated* by an earlier arm
(same or wider coverage on the same value space). Reported categories:

- Duplicate literal arm (`case 1 -> …` after `case 1 -> …`).
- Arm after a no-guard wildcard / bare variable / `is any` fallback.
- `is T` arm whose `T` is fully subsumed by an earlier `is U` where `T <: U`.
- Range arm fully contained in an earlier range.

False negatives are acceptable; false positives are not. Where reachability
cannot be statically decided, the analyzer is silent.

### 2.6 Or-pattern binding coherence

For `case A | B | C -> body`, every named binding present in *any* alternative
must be present in **every** alternative — and must have a coherent narrowed
type. Otherwise the body cannot safely use the binding. Enforced by the
analyzer (warning today; will be an error once narrowing covers all forms).

---

## 3. Sugar lowerings (parser-only)

These keep the AST surface small.

### 3.1 `if let`

```
if let P = E { THEN } else { ELSE }
```

lowers to

```
match E { case P -> THEN, case _ -> ELSE }
```

`else` is optional; absence lowers to `case _ -> { }`.

### 3.2 `while let`

```
while let P = E { BODY }
```

lowers to

```
while true {
    match E {
        case P -> { BODY }
        case _ -> { break }
    }
}
```

### 3.3 Destructuring `foreach`

```
foreach ((k, v) in pairs) { BODY }
```

lowers to

```
foreach (__it__ in pairs) {
    let (k, v) = __it__;
    BODY
}
```

Where `(k, v)` is any irrefutable destructuring pattern.

### 3.4 Destructuring fn / lambda parameters

```
fn f((a, b): (int, int)) { … }
```

lowers to

```
fn f(__p0: (int, int)) {
    let (a, b) = __p0;
    …
}
```

Lambda parameters are the same.

---

## 4. AST changes

New pattern node types under `Parser/Nodes/Patterns/`:

| Class | Shape | Use |
|---|---|---|
| `OrPatternNode` | `List<PatternNode> Alternatives` | `A \| B \| C` |
| `RangePatternNode` | `AstNode? Lo, AstNode? Hi, bool IsInclusive` | `1..10`, `..=10`, `5..` |
| `RelationalPatternNode` | `TokenType Op, AstNode Operand` | `< 5`, `>= 0`, `!= -1` |
| `AliasPatternNode` | `PatternNode Inner, string BinderName` | `P as n` |
| `MapPatternNode` | `List<(AstNode Key, PatternNode Value)> Entries, bool HasOpenRest` | `{ "x": p, .. }` |

New AST node (one entry to `AstNodeType`):

| Class | Shape |
|---|---|
| `DestructuringDeclarationNode` | `PatternNode Pattern, AstNode Initializer, BindingKind Kind, TypeDescriptor? DeclaredType` |

Where `BindingKind ∈ { Var, Let, Const, Final }` to mirror `VariableDeclarationNode`'s flavour.

`if let` and `while let` produce no new AST — they desugar in the parser to
existing `MatchNode` + `WhileNode`.

Destructuring foreach and destructuring fn/lambda params produce no new AST —
they desugar to existing forms plus `DestructuringDeclarationNode` at the
body's head.

---

## 5. Parser changes

- `ParsePattern` becomes `ParsePatternWithAlias` → `ParseOrPattern` → `ParseBasePattern`.
- `ParseBasePattern` now also recognises:
  - `< / <= / > / >= / == / !=` followed by a literal at base position
    (RelationalPatternNode).
  - `LITERAL ..[ =] LITERAL?`, `..[=] LITERAL`, `LITERAL ..` (RangePatternNode).
  - `{ … }` map pattern at base position (distinct from struct pattern, which
    is `IDENT { … }`).
- `ParseOrPattern` reads `|`-separated alternatives at the pattern level
  *only*. `|` inside expression context is unaffected.
- `Alias` trailer: after the OrPattern, optional `as IDENT` wraps the result
  in `AliasPatternNode`.
- `ParseStatement` peeks the leading token; on `let`/`const`/`var`/`final`
  followed by `(`, `[`, `{`, or `IDENT '{'` / `IDENT '('`, dispatch to a new
  `ParseDestructuringDeclaration` that parses a pattern then `= expr`.
- `ParseIfStatement` checks for `if let`; lowers as in §3.1.
- `ParseWhileStatement` checks for `while let`; lowers as in §3.2.
- `ParseForEach` accepts a *pattern* (irrefutable) in lieu of the bare
  identifier; lowers as in §3.3.
- Function/lambda parameter parsing accepts a tuple-pattern / list-pattern /
  struct-pattern in lieu of a bare identifier; lowers as in §3.4.

---

## 6. Runtime / engine changes

`MatchNodeVisitor.TryMatch` extended with:

- `case OrPatternNode op`: snapshot `bindings.Count`; for each alt try in
  order; on failure, truncate bindings back to the snapshot; on success,
  return true. If all alts fail, restore and return false.
- `case RangePatternNode rp`: evaluate `Lo` / `Hi` once via the same
  pure-literal evaluator used today; route through the scrutinee's comparison
  operators (`GetComparisonLT` / `GetComparisonLTE`); accept all numeric and
  ordered-string scrutinees.
- `case RelationalPatternNode rop`: evaluate operand; route through the
  matching comparison operator.
- `case AliasPatternNode ap`: try inner pattern; on success, push
  `(BinderName, scrutinee)` to bindings.
- `case MapPatternNode mp`: scrutinee must be `MapValue`. For each `(key, vp)`
  entry, evaluate the key literal, look up the value in the map (using map's
  equality semantics), match `vp` against the value. If `HasOpenRest`, ignore
  extra keys; else require key set equality.

`DestructuringDeclarationNode` visitor:

- Evaluate initializer once.
- Run `MatchNodeVisitor.TryMatch` in *irrefutable* mode (a failure becomes
  a `RuntimeError`, but for a well-typed program the static analyzer should
  have already rejected refutable patterns).
- Commit every binding to the *enclosing* scope (not a child scope) using
  the chosen `BindingKind`.

---

## 7. Diagnostics

New parser diagnostics (`ParserDiagnostics.cs`):

- *Pattern: refutable in irrefutable context.* `help: "use 'if let' or 'match'
  instead of 'let'"`.
- *Or-pattern alternatives bind different names: {a, b} vs {a}.*
- *Duplicate binding name `x` inside a single alternative.*
- *Range pattern: low bound must be ≤ high bound.* (when both literals are
  numeric and statically comparable).

New analyzer diagnostics (`NarrowingAnalyzer.cs`):

- *Enum match is non-exhaustive: missing variant(s) `…`.*
- *Boolean match is non-exhaustive: missing `…`.*
- *Unreachable arm: this case is fully covered by an earlier arm.*
- *Or-pattern binding type mismatch: `x` is `T` here but `U` in another
  alternative.*

All diagnostics carry source spans, a `DiagnosticCode`, and an actionable
`help` field.

---

## 8. IR lowering (forward design — not in v1)

Today's `MatchNode` is interpreted directly by `MatchNodeVisitor.Apply`,
called from the IR-fast path through `OP_NATIVE_DEFINE`. `Opcode.cs` already
reserves `MatchBegin (0x90)` / `MatchArm (0x91)` for a future lowering.

The forward plan:

1. Lower a `match` to a decision tree (Maranget '08): each internal node is a
   probe (type tag, field, list-length, literal compare); each leaf points to
   an arm body. Or-patterns are flattened; duplicate sub-pattern prefixes are
   shared.
2. Emit decision-tree probes as a small new opcode family:
   - `MATCH_TYPE_TAG slot, expected_tag_id, miss_offset`
   - `MATCH_LITERAL slot, const_idx, miss_offset`
   - `MATCH_RANGE_INT slot, lo, hi, kind, miss_offset` (kind: inclusive/exclusive both ends)
   - `MATCH_LIST_LEN slot, exact_len, miss_offset` / `MATCH_LIST_MIN_LEN slot, min, miss_offset`
   - `MATCH_FIELD_LOAD slot, shape_id, field_idx, dst_slot` (reuses existing
     hidden-class shape ICs)
   - `MATCH_BIND src_slot, dst_slot`
3. Leaves transfer control to the arm body via a normal `Jump`.

Critical invariant: existing visitor-driven `MatchNodeVisitor` must remain
correct as the *reference semantics*. The IR path is purely a performance
extension. v1 ships the spec and the visitor — IR lowering is a follow-up.

---

## 9. Implementation milestones

1. **Design doc** (this file).
2. **Pattern AST extensions** — add the five new node classes.
3. **Parser extensions** — or-pattern, range, relational, alias, map; integrate
   into `ParsePattern`. Diagnostics for refutable-in-irrefutable.
4. **Destructuring `let`** — new AST node + visitor + parser trigger from
   `let (`, `let [`, `let IDENT {`. Refutability check.
5. **`if let` / `while let`** — parser desugar.
6. **`foreach` destructuring** — parser desugar.
7. **Fn / lambda parameter destructuring** — parser desugar.
8. **MatchNodeVisitor** — implement the new pattern kinds.
9. **NarrowingAnalyzer** — enum + boolean exhaustiveness, reachability,
   or-pattern coherence, pattern-binding narrowing.
10. **Tests** — `tests_patterns.ra` covering every pattern kind and every
    pattern-position context; `tests_patterns_diag.ra` covering the new
    diagnostics. `bench_patterns.ra` for steady-state cost.
11. **Build & regress** — `dotnet build -c Release`; run every existing
    `tests_*.ra` once with the interpreter and diff outputs.

Each milestone is independently shippable. The grammar evolves additively; no
existing valid program changes meaning.

---

## 10. Risks & mitigations

| Risk | Mitigation |
|---|---|
| `|` ambiguous between or-pattern and lambda opener | `\|` in pattern position is always or-pattern; lambdas only legal in expression position. |
| `{` ambiguous between map pattern and block | Map pattern only legal in pattern position. Block follows arm `->` so no conflict. |
| Range literal evaluation order may surprise users | Bounds are *literal-only* in v1 (no general expressions); evaluated once at engine entry. |
| Refutable patterns leaking into irrefutable contexts | Static analysis pass rejects them up-front with a help text suggesting `match` / `if let`. |
| Or-pattern binding coherence regressions | Analyzer is the gate; runtime falls back to the existing match error if missed. |
| AOT / trimming hazards | All new code follows existing visitor patterns — no reflection, no dynamic codegen. Decision-tree lowering (future) is also AOT-safe. |
| `match` runtime cost grows with arm count | Pattern engine is allocation-light; binding list reuse + early-exit. Decision-tree IR lowering is the long-term path. |

---

## 11. Acceptance criteria

The feature ships when:

- every pattern category in §1 parses, runs, and is covered by tests;
- destructuring works in `let`, `foreach`, fn params, lambda params;
- `if let` and `while let` desugar correctly;
- analyzer reports enum / boolean exhaustiveness and arm reachability;
- or-pattern alternatives backtrack correctly without leaked bindings;
- no regression on `tests_unions.ra`, `tests_lambdas.ra`, `tests_validation.ra`,
  `tests_properties.ra`, `tests_events.ra`, `tests_shifts.ra`,
  `tests_annotations*.ra`;
- `dotnet build -c Release` is clean.

---

## 12. Implementation status (v1 shipped)

The following are implemented end-to-end (parser → engine → analyzer → tests):

### Patterns
- [x] **Wildcard** `_`
- [x] **Literal** (number, string, bool, null) — null path bypasses operator dispatch (identity)
- [x] **Variable** binding (with zero-arity-variant runtime disambiguation)
- [x] **Type test** `is T` and `is T as v` (narrowing binder)
- [x] **Relational** `< 5`, `<= 0`, `>= -1`, `!= 0`, `== 0`, `> 7`
- [x] **Range** `1..10`, `1..=10`, `..10`, `..=10`, `5..` — numeric + string-ordered
- [x] **Or-pattern** `A | B | C` with backtracking binding rollback
- [x] **Alias** `P as n` over arbitrary pattern (composes with all others)
- [x] **Tuple** `(p1, p2, ...)` with nesting
- [x] **List** `[a, b, c]` and `[h, ..t]` (head + rest, with optional bind)
- [x] **Struct** `Name { f1, f2: p }` field-shorthand and pattern
- [x] **Class** instance destructuring (same syntax as struct)
- [x] **Record** instance destructuring (positional via variant + by-name via struct)
- [x] **Variant** `Variant(p1, p2)` and qualified `Enum.Variant(...)`
- [x] **Map** `{ "key": p, .. }` with closed-set and open-rest forms

### Pattern contexts
- [x] `match` expression (with guards)
- [x] `if let PAT = EXPR { ... } else { ... }`
- [x] `while let PAT = EXPR { ... }` (sentinel-flag lowering — see §6)
- [x] `let PAT = EXPR;` destructuring declaration
- [x] `var PAT = EXPR;` / `const PAT = EXPR;` / `final PAT = EXPR;`
- [x] `for let PAT in EXPR { ... }` destructuring foreach
- [x] `fn f(PAT: T) { ... }` fn parameter destructuring
- [x] `|PAT| body` lambda parameter destructuring

### Analyzer diagnostics
- [x] Union exhaustiveness — missing alternative names listed
- [x] Boolean exhaustiveness — missing `true` / `false` reported
- [x] Enum exhaustiveness — missing variants listed by name
- [x] Unreachable arm after total fallback (wildcard / bare variable / `is any`)
- [x] Duplicate literal arm
- [x] Range overlap (int-bounded ranges, half-open interval intersection)
- [x] Pre-existing `is`-test always-true / always-false / impossible

### Narrowing
- [x] `is T as v` introduces `v: T` in arm
- [x] Variable pattern over typed scrutinee carries scrutinee type forward
- [x] Tuple destructure narrows each element to corresponding generic arg
- [x] List destructure narrows head to elem type, rest to list type
- [x] Struct / class / record field shorthand narrows to declared field types
- [x] Variant payload narrows to declared payload type list
- [x] Alias pattern declares binder

### Tests
- 33 pattern regression tests in `tests_patterns.ra`
- 8 analyzer-warning tests in `tests_patterns_diag.ra`
- 200k-iter `bench_patterns.ra` (parity with chained `if`)
- 0 regressions across `tests_unions.ra`, `tests_lambdas.ra`, `tests_properties.ra`,
  `tests_events.ra`, `tests_shifts.ra`, `tests_delegates.ra`,
  `tests_unions_diag.ra`, `tests_adt_match.ra`.

## 13. v2 features (shipped)

The original v1 roadmap deferred several items as "design only"; v2
landed every one of them.

- [x] **`not` pattern** — `not P` succeeds iff P fails. Inner pattern
  must be binding-free (parser enforces).
- [x] **`and` pattern** — `P1 & P2` succeeds iff both succeed. Both
  sides contribute bindings; right-most wins on name clash. Precedence:
  `or` (lowest) > `and` > `not` (highest), so `A | B & C` parses as
  `A | (B & C)`.
- [x] **Or-pattern binding-coherence diagnostic** — analyzer flags
  alternatives that bind different name sets, listing the missing /
  extra names per alternative.
- [x] **Generic enum payload substitution** — when scrutinee is
  `Result<int, string>`, `case Ok(v)` narrows `v` to `int`. Done via a
  `T → concrete` substitution applied to the declared payload type.
  Built-in `Result<T,E>` and `Option<T>` are pre-seeded into the
  analyzer state so the narrowing fires for them too.
- [x] **Literal vs. range overlap (bidirectional)** — a literal inside
  an earlier range is flagged as unreachable; a range covering an
  earlier literal is flagged as a partial-shadow warning (the literal
  still wins by source order).
- [x] **Sealed class hierarchies exhaustiveness** — class declarations
  marked with the built-in `@sealed` annotation close their inheritance
  set; the analyzer enumerates direct subclasses (collected through
  `ClassDefinitionNode.BaseType`) and warns when a match on the sealed
  base doesn't cover every subclass.
- [x] **Pattern in `catch` clause** — `catch (Variant(payload)) { … }`,
  `catch (Type { field }) { … }`. Implemented by extending
  `RuntimeError` with an optional `ThrownValue`. `throw` (both the AST
  visitor and the IR `Opcode.Throw` fast path) stores the raw runtime
  value; the catch handler — both visitor-side and VM-side EhTable
  dispatch — prefers `ThrownValue` over the stringified diagnostic
  when binding the catch slot.
- [x] **IR-level match optimisation** — pragmatic decision: rather
  than ship a half-baked Maranget decision-tree opcode family, the
  `MatchSimplifier` AST pass lowers eligible `match` expressions to
  equivalent `if/elif/else` chains *before* the IR compiler runs.
  Eligibility: every arm is guard-less and its pattern is leaf
  (literal / range / relational / wildcard / or over those). The
  IR compiler's existing if-chain optimiser (jump-table layout,
  typed-accumulator merging, comparison peephole) then runs verbatim.
  Patterns with bindings, type tests, variants, etc. keep the
  visitor path (still correct, still allocation-light). The reserved
  opcode family (`MatchBegin/MatchArm/MatchEnd` + the probe family)
  stays available for a future full Maranget lowering.

## 14. Tests (v2)

- 36 pattern regression tests in `tests_patterns.ra` (+3 vs. v1: not/and,
  catch pattern, sealed runtime).
- 13 analyzer-warning tests in `tests_patterns_diag.ra` (+5: lit-in-range,
  range-covers-lit, generic narrowing, or-coherence, sealed exhaustiveness).
- 0 regressions across `tests_unions.ra`, `tests_lambdas.ra`,
  `tests_properties.ra`, `tests_events.ra`, `tests_shifts.ra`,
  `tests_delegates.ra`, `tests_unions_diag.ra`, `tests_adt_match.ra`.

## 15. Forward roadmap (post-v2)

Only true "horizon" work remains.

- **Full Maranget decision-tree IR lowering** with proper opcode
  emission for the residual visitor path (variants / structs / lists /
  nested patterns / type tests). Estimated 3-5 days; benefits the
  hottest patterns in idiomatic ADT code by ~3-5×.
- **`@` binding inside pattern** (Rust-style) — `n @ 1..10`. Today's
  trailing `as n` is the spelling; both could coexist.
- **Pattern macros / user-defined pattern syntax** — sugar for
  domain-specific destructure shapes.
- **Re-throw on catch-pattern mismatch** — today a catch whose
  pattern doesn't match raises a fresh RuntimeError; ideally the
  original exception should propagate to the next outer try.
- **Cross-module sealed hierarchies** — `CollectEnums` only sees the
  current compilation unit. A multi-module sealed-set requires the
  module manager to expose subclass lists across imports.

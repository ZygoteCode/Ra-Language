# Ra Records — Design Notes (v2)

Internal design document. Captures the semantics, runtime, IR/VM wiring,
and trade-offs of the records feature in Ra Language. Mirrors the spirit
of `RA_VM_MIGRATION.md`.

**v2 additions:** controlled inheritance for `record class`,
`@derive(equals=false, to_string=false)` opt-out, built-in
`deconstruct()` method, and pattern-match support for record
positional destructure.

## 1. Goal

A first-class **record** construct in Ra, fit for production use. Records
must:

- look natural alongside Ra's existing `struct` / `class` / `enum`,
- generate `==`, `hash`-equivalent and `to_string` without surprises,
- support a `with`-expression for copy-with-overrides,
- be **fast on NativeAOT** — no runtime reflection, no `MakeGenericType`,
- integrate with the IR + VM through the existing `OP_NATIVE_DEFINE`
  pipeline (no patchy escape hatch),
- avoid the pitfalls programmers complain about in other ecosystems
  (C#, Java, Kotlin, Swift, F#, Rust).

## 2. Syntax

```ra
// Value record — IsCopy=true, sealed, structural equality.
record Point(x: int, y: int)

record Vec3(x: float, y: float, z: float) {
    pub fn magnitude_sq(): float {
        ret (self.x*self.x + self.y*self.y + self.z*self.z) as float;
    }
}

// Default-valued primary field.
record Config(retries: int = 3, label: string = "default")

// Reference record — IsCopy=false, mutation through .field= propagates.
record class Counter(mut n: int)

// Generics.
record Pair<T, U>(first: T, second: U)

// Operator overloads in the body.
record Money(amount: int, currency: string) {
    operator +(other: Money): Money {
        ret Money(self.amount + other.amount, self.currency);
    }
}

// `with` produces a copy-with-overrides.
var p2 = Point(1, 2) with { x: 10 };       // Point(x=10, y=2)
```

Modifier vocabulary inside the primary-constructor parameter list:

| Modifier | Meaning |
|----------|---------|
| (default) | public, immutable (FINAL) |
| `pub`     | explicit public (no-op against default, retained for clarity) |
| `priv`    | private — soft modifier, no new keyword |
| `mut`     | mutable. Mostly useful on `record class` because value records lose mutations once the binding's slot returns a fresh `Aliased()` copy. |

Body items allowed: `fn` methods, operator overloads. **No extra
instance fields** — the primary-constructor list is the sole source of
truth for the auto-generated equality / hash / to_string. The parser
rejects `var`/`let`/`const`/`final` inside record bodies with a
dedicated diagnostic.

## 3. Semantics

### 3.1 Two flavors, one runtime

| Flavor | Keyword           | `IsCopy` | Aliasing | Identity rule |
|--------|-------------------|----------|----------|---------------|
| value  | `record`          | `true`   | reads call `Copy()` (no-op for primitives, structural clone for nested values) | nominal: `Point(1,2) != Pair(1,2)` |
| ref    | `record class`    | `false`  | reads alias (`this`) | nominal: same |

Both produce a `RecordInstanceValue` at runtime, distinguished by
`Definition.IsRefRecord`.

### 3.2 Construction

`record Name(...)` registers a `RecordTypeValue` in the symbol table
(`isLet: true`, statically typed by name, public-by-default modifier
respected). Calling the type as a function `Name(arg1, arg2)` invokes
`RecordTypeValue.Execute(args)`, which:

1. binds positional args by primary-field order,
2. accepts named-arg refinements for missing slots,
3. evaluates default-value expressions when neither positional nor
   named arg covers a slot,
4. type-checks each value against the field's declared type via the
   existing `TypeSystem.IsAssignable`,
5. emits precise diagnostics for missing fields, unknown named args,
   and positional+named double-binding.

The constructed `RecordInstanceValue` uses
`FieldDeclarationType=FINAL` for default (immutable) primary fields
and `VARIABLE` for `mut` fields. `MemberAssignmentHelper` already
enforces the right rules at write time (FINAL → reject;
VARIABLE → allow), so we don't introduce a separate immutability
mechanism — we reuse the one already proven by struct/class.

### 3.3 Equality

`RecordInstanceValue.GetComparisonEq(other)` (and the
`StrictEq` / `Ne` / `StrictNe` siblings):

1. If the user provided a custom `operator ==` / `operator !==` for
   the matching parameter type, defer to the struct-base dispatch
   (which finds and runs that overload).
2. Otherwise, require **nominal identity**: both sides must be
   `RecordInstanceValue` with the same `Definition` reference.
3. Then compare each primary field pairwise via the field's own
   `GetComparisonEq` (or `GetComparisonStrictEq` in strict mode),
   propagating strictness recursively into nested records.
4. Any field comparison error (mismatched types deep down) is treated
   as inequality rather than a runtime crash — equality is a
   total operation.

Definition-reference identity is preserved by overriding
`RecordTypeValue.Copy()` to return `this`. Without that, `Aliased()`
on the IsCopy=true type would mint a fresh `RecordTypeValue` per
construction site and `Point(1,2) == Point(1,2)` would be silently
false. (Discovered live during testing — this is exactly the kind of
identity-leak bug C# fixed with `EqualityContract`.)

### 3.4 Hashing

Ra's collections (`Set`, `Map`, `in`) compare with `Equals` not via
`GetHashCode` — see the M6 milestone note on `OP_NEW_SET` using
linear-search dedupe to dodge the GetHashCode/Equals-contract trap.
`Equals` on `RuntimeValue` dispatches to `GetComparisonStrictEq`,
which records override structurally. So records work in sets and as
map keys without us needing a parallel hash-table contract.

### 3.5 to_string

`StringConversionUtility.ConvertToString` already routes
`StructInstance` through `TryCallToString()` so a user-provided
`fn to_string(): string` overrides the default. We widened that
branch to accept `RecordInstance` too. Default format:
`Name(field1=value1, field2=value2, …)` — chosen to match the
C# `record` `ToString` shape, which is the most familiar to the
broadest audience.

`to_string` shape validation (parameterless, returns `string`) is
applied at definition time, mirroring the struct / class rule.

### 3.6 `with`-expression

`expr with { name1: v1, name2: v2 }` is parsed as a postfix on any
primary expression. The receiver must evaluate to a
`RecordInstanceValue` at runtime. The visitor:

1. Shallow-clones the receiver via
   `RecordInstanceValue.ShallowCloneForWith()` — preserves
   declaration-type metadata and slot-array layout.
2. Walks the update list. Each pair is validated against the record's
   primary-field shape (unknown names raise a precise diagnostic
   pinned to the offending pair) and against the field's declared
   type via `TypeSystem.IsAssignable`.
3. Writes overrides via the existing `SetField` (keeps the FieldSlots
   index in sync).

Duplicate names inside a single update list are a parse error — no
silent last-write-wins.

### 3.7 What the parser rejects

- `record Foo()` { var x = 1; } → "records cannot declare extra
  instance fields in the body — primary-constructor parameters are
  the single source of truth"
- `record Foo(x: int) { Foo() { ... } }` → "Records cannot declare
  explicit constructors. The primary-field list defines '<Name>'s
  only constructor."
- `record Foo(x: int, x: string)` → "duplicate primary-field name"
- `expr with { x: 1, x: 2 }` → "record-update lists may not name the
  same field twice"

## 4. Runtime / IR / VM integration

- **AST nodes:** `RecordDefinitionNode`, `RecordPrimaryFieldNode`,
  `WithExpressionNode`. New `AstNodeType` enum entries
  (`RecordDefinition`, `RecordPrimaryField`, `WithExpression`).
- **Value types:** `RecordTypeValue` extends `StructTypeValue`,
  `RecordInstanceValue` extends `StructInstanceValue`. New
  `RuntimeValueType` enum entries (`RecordType`, `RecordInstance`).
  Inheriting from the struct hierarchy makes member access, method
  binding (`BoundStructMethodValue`), operator dispatch
  (`TryOperatorDispatch`), and the hidden-class shape index all
  Just Work™ without duplicate plumbing.
- **Resolver:** new `WalkRecord` mirrors `WalkStruct` — walks
  default-value expressions and assigns each body method/operator
  its frame/param bindings so the IR compiler can lower them
  normally.
- **IR compiler:** `RecordDefinition` is routed through
  `OP_NATIVE_DEFINE` exactly like `StructDefinition`. `WithExpression`
  too — the visitor's `Apply` is invoked from the VM dispatch
  switch.
- **VM dispatcher (`VmExecutor`):** new cases for
  `AstNodeType.RecordDefinition` and `AstNodeType.WithExpression`
  in the `OP_NATIVE_DEFINE` switch, invoking the visitor's
  `Apply` directly.
- **Type system:** `TypeSystem.IsAssignable` accepts
  `RecordInstance` against a `StructTypeValue`/`RecordTypeValue`
  symbol with the same name. `BuildDescriptor` / type-name
  resolution accept `RecordInstance` / `RecordType`. This is what
  unblocks records as function parameter / return types.
- **Member access / assignment:** `MemberAccessHelper` and
  `MemberAssignmentHelper` (the shared bodies behind both the
  visitor and the VM opcodes) and the AST-only
  `MemberAssignmentNodeVisitor` got widened to accept
  `RecordInstance`.
- **Operator dispatch:** `RuntimeValue.GetTypeName` adds the
  `RecordInstance → record-name` mapping so user-provided
  `operator +(other: A): A` overloads resolve.
- **`StringConversionUtility`:** widened to route
  `RecordInstance` through `TryCallToString()` (user-override
  hook for `to_string`).
- **IrExpressionEvaluator:** `RecordDefinition` registered as a
  statement-only node (cannot appear in expression position; the
  evaluator distinguishes statement-only AST kinds).

Two `sealed override` markers in `StructTypeValue` and
`StructInstanceValue` were unsealed (`Type`, `IsCopy`, `Execute`,
`Copy`, `ToString`, and the comparison ops on the instance). No
behaviour change for plain structs — sealing was a "don't let
anyone override" hint, not a contract.

## 5. v2 additions

### 5.1 Controlled inheritance for `record class`

Reference records (`record class`) opt into a *single-parent* hierarchy
via an `abstract record class` base and a `: Base(args)` clause on the
child:

```ra
abstract record class Shape(name: string)
record class Circle(radius: float) : Shape("Circle")
record class Disc(radius: float)   : Shape("Disc")
```

Rules enforced at definition time:

- Only `record class` may participate. Value records (`record`) are
  always sealed (no `: Base(...)` clause allowed).
- The base must be `abstract record class`. Concrete records are
  sealed by construction — inheriting from them is rejected.
- The child must NOT redeclare an inherited primary field. The
  visitor merges parent.PrimaryFields ++ child.PrimaryFields and
  raises `RA0401`-style diagnostic if a name appears twice.
- Methods and operators are merged: parent items are inherited, child
  re-declarations override by name.

Equality remains **type-restricted**: two record instances compare
equal only when their `Definition` reference matches *and* every
primary field is pairwise-equal. Parent vs child comparison is always
false. This sidesteps the C# `EqualityContract` trap entirely — there
is no virtual property to align across derivations, and the runtime
does not need to inspect the inheritance chain at comparison time.

Abstract bases cannot be instantiated; `Execute` checks `IsAbstract`
up-front and emits a precise diagnostic. `BaseRecord` on the
`RecordTypeValue` carries the parent definition for future
introspection (reflection / `is`-checks) but is not consulted by the
equality engine.

### 5.2 `@derive(equals=false, to_string=false)` opt-out

Records auto-derive equality and `to_string` by default. The
`@derive` annotation flips those auto-generators off using
**named-args** (positional flags would collide with the class form
which uses positional opt-IN strings):

```ra
@derive(equals=false)
record class NoEq(x: int, y: int)              // == falls back to ref identity

@derive(to_string=false)
record Opaque(secret: int)                     // (o as string) -> "<Opaque>"

@derive(equals=false, to_string=false)
record Plain(x: int)
```

The `DeriveTransformer` pre-pass walks `RecordDefinitionNode`s and
consumes the `@derive` annotation, setting `AutoEquals` /
`AutoToString` flags on the node. These flags are plumbed through
`RecordTypeValue` and consulted by `RecordInstanceValue`:

- `AutoEquals = false`, no user `operator ==` → reference identity
  fallback (`ReferenceEquals(this, other)`). Useful for `record
  class` where a meaningful identity exists; on value records the
  user is expected to provide their own overload.
- `AutoToString = false`, no user `to_string()` → returns
  `"<Name>"`. This deliberately avoids leaking primary-field values
  into diagnostics (a security-adjacent property for records that
  store secrets).

User-provided `operator ==` / `fn to_string()` always win regardless
of the opt-out flags.

### 5.3 Built-in `deconstruct()` method

Every record gets a synthetic zero-arg `deconstruct()` method that
returns a `TupleValue` of the primary fields in declaration order:

```ra
var p = Point(3, 4);
var t = p.deconstruct();    // (3, 4)
```

Implementation avoids AST surgery: `MemberAccessHelper` resolves
`record.deconstruct` to a `BoundRecordDeconstructValue` (callable
wrapper that captures the receiver). The IC fast-path tags the slot
with `BR_RECORD_DECONSTRUCT` for cache hits. Critically, the lookup
checks `Definition.Methods.GetMethod(memberName)` first — if the
user defines their own `deconstruct` method, that wins and the
synthetic fallback never fires.

For inherited records, `Definition.PrimaryFields` is already the
merged (base ++ child) sequence, so the returned tuple naturally
includes inherited fields without record-class-specific glue.

### 5.4 Pattern match for records

Pattern matching supports positional destructure of records:

```ra
match point {
    case Point(x, y) -> handle(x, y)
    case _           -> fallback()
}
```

The `VariantPatternNode` already parsed `Name(p1, p2, ...)` — we
extend `MatchNodeVisitor.TryMatchVariant` to resolve the pattern's
`VariantName` against the symbol table. If it resolves to a
`RecordTypeValue` matching the scrutinee's `Definition`, the engine
binds sub-patterns left-to-right against the primary fields. Cross-
type record patterns reject (nominal identity required). Arity
mismatches raise a precise diagnostic with a suggested rewrite.

Brace-style struct destructure (`case User { name, age }`) continues
to work via `TryMatchStruct` since `RecordInstanceValue` inherits
from `StructInstanceValue`.

## 6. What we deliberately did NOT do (and why)
- **Multiple inheritance for `record class`.** Single base only; the
  child's `BaseRecord` is one parent. Multi-parent diamond shapes
  would re-introduce the per-leaf `EqualityContract` alignment
  problem we explicitly avoided.
- **`hash()` builtin.** Ra's collections do not depend on
  `GetHashCode` — see §3.4. Adding a `hash()` builtin that uses
  reflection would be NativeAOT-unfriendly. If Ra ever grows
  hash-based collections, the hash will derive from the same
  primary-field walk as equality.
- **`let`-position tuple patterns.** `match` patterns (`case
  Point(x, y) -> ...`) and the explicit `point.deconstruct()`
  method now both work. `let (x, y) = point` requires the
  declaration-position tuple pattern grammar that is still
  outside the parser; tracked separately.
- **Custom `mut` semantics on value records.** Value records cannot
  meaningfully implement in-place `.field = ...` because the
  binding's read path returns a fresh `Aliased()` copy — the
  mutation lands on a transient and disappears. We document this
  in the test and reserve `mut` semantics for `record class`. The
  parser accepts `mut` on value records (with no error) precisely
  so the grammar stays consistent, but the recommended idiom is
  `c = c with { n: 5 }`.

## 7. Test coverage

`bin/x64/Release/net10.0/tests/types/test_records.ra` — 32+ cases
covering:

1. Primary-field constructor + field reads (R1)
2. Auto `to_string` format (R2)
3. Structural equality positive case (R3)
4. Structural inequality (different value) (R4)
5. Cross-type inequality (R5)
6. `with`-expression — single override (R6)
7. `with`-expression — multiple overrides, receiver unchanged (R7)
8. `with`-expression — produces a structurally-equal sibling (R8)
9. `with`-expression — unknown field raises a precise error (R9)
10. Default-immutable primary fields reject in-place writes (R10)
11. `record class` + `mut` allows in-place writes (R11)
12. Body method using `self` (R12)
13. Body operator overload — `+` (R13)
14. Generic record (R14)
15. Default-value primary field (R15)
16. Nested records: equality drills down (R16a/b)
17. Container-field records: structural equality element-wise (R17)
18. `record class` ref-flavor still uses structural equality (R18)
19. Records as function parameters / return types (R19)
20. User-provided `to_string` overrides the auto format (R20)
21. Built-in `deconstruct()` returns tuple of primary fields (R21)
22. Pattern match — `case Point(x, y) -> ...` positional bind (R22)
23. Pattern match — arity mismatch raises diagnostic (R23)
24. Pattern match — cross-type record never matches (R24)
25. `@derive(equals=false)` — auto equality off, ref-identity (R25)
26. `@derive(to_string=false)` — auto format off, "<Name>" (R26)
27. Both opt-out flags together (R27)
28. Abstract record class cannot be instantiated (R28)
29. Controlled inheritance: child merges inherited primary fields
    into the visible declaration order (R29)
30. Type-restricted equality under inheritance: sibling
    concrete records never equal (R30)
31. Inheritance refuses to redeclare inherited primary fields (R31)
32. Value records cannot inherit (always sealed) (R32)

Wider corpus regression: 138 OK / 0 FAIL across `annotations /
async / collections / control_flow / edge_cases / errors /
functions / integration / lexer / modules / numbers / operators /
parser / pattern_match / reflection / scoping / semantics /
strings / types`, plus 30 OK / 0 FAIL across `other_tests`.

## 8. Performance posture

- Records reuse the **shape-indexed slot array** from M38/M41
  (`StructInstanceValue.FieldSlots`). Field reads from a
  monomorphic IC site are O(1) and allocation-free.
- `RecordTypeValue.Copy()` returns `this` instead of cloning. Reads
  of the type symbol (`Point` in `Point(1, 2)`) are now
  allocation-free regardless of how many times the constructor is
  invoked.
- Equality is a tight loop over the primary-field list, no
  reflection. Each step dispatches through the existing
  `GetComparisonEq` virtual already optimized for primitives.
- `with`-expression is a shallow clone + targeted writes, no extra
  allocations beyond the new instance.
- Inheritance has zero runtime cost: parent fields are flattened into
  the child's `PrimaryFields` at definition time, so equality / to_string /
  deconstruct walk a single list with no v-table indirection.

## 9. Future directions

- **`let`-position tuple patterns** — extend `let` to accept the
  same record positional grammar as `match`, e.g. `let Point(x, y)
  = p`. Requires parser changes outside the records subsystem.
- **Module-level visibility refinements** — `record class` with
  private primary fields should still get auto-equality but maybe
  not auto-`to_string` (avoids leaking private state into
  diagnostics).
- **`hash()` built-in** — only if Ra grows hash-based collections
  whose semantics genuinely require an O(1) hash; the structural
  walk that powers equality already determines the value identity,
  so a future hash would derive from the same primary-field walk.

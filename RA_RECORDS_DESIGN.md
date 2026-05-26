# Ra Records — Design Notes (v1)

Internal design document. Captures the semantics, runtime, IR/VM wiring,
and trade-offs of the records feature in Ra Language. Mirrors the spirit
of `RA_VM_MIGRATION.md`.

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

## 5. What we deliberately did NOT do (and why)

- **Inheritance for `record class`.** C#'s record-class inheritance
  carries the `EqualityContract` trap: cross-type comparisons can
  return true unexpectedly, and the runtime has to keep an extra
  virtual property aligned at every derivation. We chose to ship
  records as *nominally sealed* — every record (value or ref) is its
  own equivalence class. The `IsRefRecord` flag is already in the
  data model, so v2 can add a controlled abstract/sealed inheritance
  story without breaking v1.
- **`hash()` builtin.** Ra's collections do not depend on
  `GetHashCode` — see §3.4. Adding a `hash()` builtin that uses
  reflection would be NativeAOT-unfriendly. If Ra ever grows
  hash-based collections, the hash will derive from the same
  primary-field walk as equality.
- **Built-in deconstruction syntax (`let (x, y) = point`).** Ra's
  tuple-pattern grammar already exists in `match`, but
  declaration-position tuple patterns are not in the language yet.
  Records ship with positional field access (`point.x`, `point.y`)
  and the `with`-expression; once Ra grows let-pattern destructure,
  primary-field positional unpacking will follow automatically — no
  record-specific work needed.
- **Custom `mut` semantics on value records.** Value records cannot
  meaningfully implement in-place `.field = ...` because the
  binding's read path returns a fresh `Aliased()` copy — the
  mutation lands on a transient and disappears. We document this
  in the test and reserve `mut` semantics for `record class`. The
  parser accepts `mut` on value records (with no error) precisely
  so the grammar stays consistent, but the recommended idiom is
  `c = c with { n: 5 }`.

## 6. Test coverage

`bin/x64/Release/net10.0/tests/types/test_records.ra` — 21 cases
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

Wider corpus regression: 516 OK, 0 FAIL, 0 errors across
`annotations / async / collections / control_flow / errors /
functions / integration / modules / numbers / operators /
pattern_match / reflection / scoping / strings` plus all the
`types / semantics` tests.

## 7. Performance posture

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

## 8. Future directions

- **Pattern-matching destructure** for records — when Ra grows
  let-position tuple patterns, extend `match`/`let` to accept
  `let Point(x, y) = p` shorthand.
- **`record class` inheritance** — add an `abstract record class`
  + child inheritance, but with type-restricted structural equality
  (no `EqualityContract` slip).
- **Auto-derive opt-ins** — `@derive(equals=false, to_string=false)`
  on a record to skip auto-generation when the user wants full
  control.
- **Module-level visibility refinements** — `record class` with
  private primary fields should still get auto-equality but maybe
  not auto-`to_string` (avoids leaking private state into
  diagnostics).
- **Builtin `deconstruct()`** — a method on every record returning
  a `tuple(field1, field2, ...)` for explicit destructuring,
  callable until pattern-matching catches up.

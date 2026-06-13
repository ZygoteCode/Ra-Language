# Ra Introspection & Type-Operator System — Design + Roadmap

Status: **Tier 1 + Tier 2 (identity / defaults / signatures) + Tier 3
(handle-based reflection) shipped.** This document is the audit + design spec +
roadmap for Ra's introspection / type-query / cast / layout operator family.
**No new opcodes, no new AST node kinds** (reuses `nameof`/`as` nodes + builtins;
Tier 3 adds one leaf `RuntimeValue` subtype + builtins, no opcode/AST change).

## 1. Audit — what already exists (≈80% of the ask)

| Family | Existing surface |
| ------ | ---------------- |
| **Symbol / type query** | `nameof`, `typeof` (keywords); `type_of`/`type_name`/`type_kind`, `class_of`/`base_class`/`super_classes`/`traits_of`/`interfaces_of`/`generics_of` (builtins) |
| **Type test / cast** | `is` / `is not` (`IsTypeNode`, `OP_IS_TYPE`, full type coverage incl. union/iface/trait/generic); `as` (`CastNode`, conversions) |
| **Reflection / metadata** | ~94 `std.prelude.reflect` builtins — 30+ `is_*` predicates, member enumeration (`fields_of`/`methods_of`/`members_of`), member-by-name, `function_arity`/`params`/`return_type`, enum reflection, **first-class annotations** (`annotations_of` → `AnnotationInstanceValue`), symbol-table reflection. AOT-safe (precomputed `MetadataRegistry`, zero `System.Reflection`) |
| **Layout / ABI** | `native_sizeof`, `struct_size`, `struct_offset_of` (FFI, `NativeStructLayout`) |

The families are present; the gaps are sharp. Per the brief's "no magic without
measurable benefit", Tier 1 fixes the highest-value ones rather than adding ~150
speculative aliases.

## 2. Tier 1 — shipped

### 2.1 `nameof` — now compile-time folded + member chains

* **Compile-time fold.** The symbol's textual name is known at parse time, so
  `nameof` no longer emits a runtime `OP_Nameof` opcode — the IR folds it to a
  `LoadConst` string ([`IrCompiler`](Interpreter/IR/IrCompiler.cs) `AstNodeType.Nameof`),
  and the AST-fallback path returns the same constant. **Zero runtime cost, zero
  allocation beyond the interned literal** — the brief's zero-cost thesis.
* **Member chains.** `nameof(a.b.c)` → `"c"` (the final segment, matching C#).
  Previously only a single bare identifier parsed. Functions (`nameof(f)`) and
  types (`nameof(T)`) already worked as identifiers and continue to.
* **Semantics:** input = a symbol or member-access path; output = the final
  segment's name as a `string`; pure, side-effect-free, constant-foldable; no
  generic / overload interaction (it's purely textual).
* **Tradeoff (explicit):** the previous *runtime* "symbol exists" check is
  dropped in favour of compile-time folding (the name is textually present
  regardless). **Member-chain targets are now statically validated** (§2.6); full
  bare-symbol binder validation remains deferred (§4) — it would add a
  *compile-time* diagnostic, strictly better than the old runtime error.

### 2.2 `as?` / `as!` — safe and explicit casts

* **`as?`** — safe cast: on conversion failure yields `null` instead of raising.
  Non-exceptional, the Swift/Kotlin idiom. `CastNode.Safe = true`.
* **`as`** — unchanged throwing cast. **`as!`** — explicit throwing form
  (same semantics as `as`, signals intent).
* Parsed as a marker (`?` / `!`) between `as` and the target type
  ([`Parser.Expressions`](Parser/Parser.Expressions.cs)); unambiguous because a
  type can never start with `?`.
* **Robustness fix (was a crash):** a checked numeric narrowing (e.g.
  `300 as byte`) previously threw an *unhandled* host `OverflowException` that
  crashed the interpreter. Both VM and AST cast paths now catch any `CastTo`
  exception and turn it into a clean, positioned Ra error
  ([`VmExecutor.CastThrewError`](Interpreter/Vm/VmExecutor.cs)) — `as` surfaces
  it, `as?` swallows it to `null`.
* **Semantics:** `value as? T` = `T`-converted value, or `null` if not
  convertible; `value as T` / `value as! T` = converted value, or a runtime
  error. Null input to `as?` → `null`.

### 2.3 `alignof` — exposed

`NativeStructLayout` already computed alignment; it's now reachable, completing
the `sizeof`/`alignof`/`offsetof` trio, matching the existing
`native_sizeof`/`struct_size`/`struct_offset_of` builtin convention (Ra has no
`sizeof` *keyword*):

* `native_alignof(typeStr)` — alignment of a primitive ("i32"→4, "i64"→8, …).
* `struct_align(type|instance)` — a struct's ABI alignment.

Both AOT-safe, group `ffi` → `std.sys.ffi`, results are runtime constants
(Windows x64 ABI, matching the existing FFI layout model).

## 2.4 Tier 2 — type identity, defaults, signatures (builtins, group `reflect`)

All pure builtins — **no syntax / IR / VM changes**, reusing `BuiltinUtils.TypeName`
and the existing function reflection:

* **`type_id(x)`** → a stable, compact **integer** identity for `x`'s type (or
  `x` itself when it is a type value). Same type ⇒ same id ⇒ O(1) int equality
  and dense map keys. A type and its instances share an id. Backed by a
  process-wide canonical-name → int intern table (thread-safe; ids are unique +
  stable, density not guaranteed under races). The brief's "type identity, fast
  comparison, interned, stable IDs" — done without a heavyweight first-class
  type object.
* **`type_key(x)`** → the canonical type-name **string** (interned), stable
  across runs and usable directly as a map key.
* **`signature_of(fn)`** → the structural signature `fn(P…) -> R` (untyped slots
  render `any`; varargs as `...T`). Same renderer as the callable diagnostics.
* **`default_of(T)` / `zero_of(T)`** → the default value for a type, given a
  type-name string (the `native_sizeof("i32")` convention) or a type value:
  numeric → `0`, `bool` → `false`, `string` → `""`, every reference / composite
  / unknown type → `null`.

Note Ra's numerics share one canonical bucket (`type_of(5)` == `"number"`), so
`type_id`/`type_key` treat all numeric values as one identity — consistent with
the rest of the type model.

## 2.5 Tier 2 — qualified names (namespace-aware)

Type values now carry their declaring namespace — a `DeclaringNamespace` field on
`ClassTypeValue` / `StructTypeValue` / `EnumTypeValue`, stamped once when the type
registers into a `namespace` body ([`NamespaceDeclarationNodeVisitor`](Interpreter/Visitors/Namespaces/NamespaceDeclarationNodeVisitor.cs),
mirroring how function closures are frozen there). Two builtins read it:

* **`qual_name_of(x)`** → the namespace-qualified type name: `"A.B.Foo"` for a
  type declared in `namespace A.B`, else the bare canonical name. Accepts a type
  value or an instance (resolved to its type).
* **`full_name_of(x)`** → the qualified name prefixed with the declaring module
  (source-file basename): `"mymod::A.B.Foo"`; falls back to the qualified name
  when the declaring file is unknown.

Additive (the field defaults `null`; global-scope types are unchanged).

## 2.6 `nameof` member-chain validation (compile-time diagnostic)

`nameof(base.f1.f2…)` is now statically validated against the *known* types of
its segments — the only slice of binder validation that can be done without a
full binder (§4), and done without false positives. Lives in the
[`NarrowingAnalyzer`](Interpreter/Runtime/Narrowing/NarrowingAnalyzer.cs)
(diagnostics-only pass, same place `is`-narrowing checks live), reusing its
`CollectEnums` struct/record field maps:

* `NameofNode.Path` carries the full segment list (`["u","email"]`); `Name`
  stays the folded final segment, so the **fold is unchanged**.
* Validation walks the chain only when the base symbol has a *statically known*
  struct/record type (`state.Lookup` + `state.Structs`). Each hop must be a real
  field of the current type; the first miss emits `nameof: 'X' is not a member of
  'T'`. A bare symbol (`Path.Count < 2`), an untyped/global base, or a non-struct
  hop **stops silently** — no diagnostic, so there are zero false positives on
  symbols the pass can't resolve.
* Pure compile-time: emits a warning-level diagnostic, never changes the folded
  string or runtime behaviour. This is the anti-fragile partial — it fires only
  where it is provably correct, exactly the line §4 draws.

## 2.7 Tier 3 — handle-based reflection (first-class MethodInfo/FieldInfo)

The string-keyed reflection builtins (`methods_of`/`fields_of` return *names*;
`invoke_method(recv, "m")` re-does a string lookup on every call) are replaced —
additively — by a first-class **member handle**: the `MethodInfo`/`FieldInfo` of
Ra. A handle bundles `(owner type, member name, kind, static?, public?)` into one
value that can be stored, passed around and *used* without re-naming the member.

* **Value type** — [`MemberHandleValue : RuntimeValue`](Interpreter/Values/Reflection/MemberHandleValue.cs)
  (`RuntimeValueType.MemberHandle`, `MemberKind` = Method/Field/Property/Unknown).
  Holds a reference to the already-built type value plus precomputed metadata —
  **no `System.Reflection`, no codegen, AOT-clean**, cheap (one small object).
* **Create** (group `reflect`, in `ReflectionBuiltins.cs`): `method_handle(type|instance, name)`,
  `field_handle(type|instance, name)`, `member_handle(…)` (method first, then
  field), `members_of_handles(type)` (all methods + fields as handles). A missing
  member yields `null` (ergonomic, no throw). The owner may be a type value *or*
  an instance (resolved to its definition).
* **Query** (read the bundled metadata, zero re-lookup): `member_name`,
  `member_kind`, `member_owner` (→ the type value), `member_is_static`,
  `member_is_public`, `is_member_handle`. Flags are read off the resolved
  declaration at creation, so they are accurate, not guessed.
* **Use** (delegate to the existing runtime builtins — one code path, no
  duplicated dispatch): `member_invoke(h, receiver, …args)` → `invoke_method`,
  `member_get(h, receiver)` → `get_field`, `member_set(h, receiver, value)` →
  `set_field`. Wrong-kind use (e.g. `member_get` on a method handle) is a clean
  positioned error.
* **Resolution** mirrors the existing reflection: `ClassTypeValue` instance
  methods → static methods → instance fields → static fields; `StructTypeValue`
  methods → fields. Enums (whose "members" are values, not methods/fields) are
  served by the existing `enum_*` builtins, so they are intentionally not handle
  targets.

**Tradeoff (explicit):** handles are *resolved-by-name at creation* and carry a
reference to the live type value — they are not a separate precomputed metadata
table, because the type values already are that table. This keeps Tier 3 purely
additive (zero changes to the type-build path) at the cost of a name lookup at
`*_handle(...)` time (not per use). Overloaded methods bind the first match (same
contract as `invoke_method`); a richer overload-selecting handle is a future
extension if a use case appears.

## 3. Formal semantics

| Op | Input | Output | Eval | Pure | Null | Fold |
| -- | ----- | ------ | ---- | ---- | ---- | ---- |
| `nameof x` / `nameof(a.b.c)` | symbol / member path | `string` (final segment) | compile-time | yes | n/a | **constant** |
| `e as? T` | value, type | `T` or `null` | runtime | yes | `null`→`null` | no |
| `e as T` / `e as! T` | value, type | `T` or error | runtime | yes | error on incompatible | no |
| `native_alignof(s)` | type-name string | `int`/`null` | runtime const | yes | unknown→`null` | no |
| `struct_align(t)` | struct type/instance | `int` | runtime const | yes | error if not a struct | no |

## 4. Roadmap (deferred, with rationale)

* **Tier 2** — ✅ SHIPPED (§2.4–2.5): `type_id`/`type_key` (interned stable
  identity → O(1) eq + map keys), `default_of`/`zero_of`, `signature_of`,
  `qual_name_of`/`full_name_of` (namespace-qualified via a stamped
  `DeclaringNamespace`). **`nameof` member-chain validation** ✅ SHIPPED (§2.6).
  ⏳ REMAINING: **full bare-symbol binder validation of `nameof`** (compile-time
  "unresolved symbol" diagnostic for the single-identifier case). Genuinely
  architectural — it needs the binder's unresolved-symbol set **plus** an
  allowed-values filter (builtins / imports / types) to avoid false positives:
  the StaticAnalyzer can't see user symbols pre-interpretation (it would
  false-positive on every user name), and the Resolver has full scope but
  deliberately emits no diagnostics. The LSP's `StaticDiagnostics` already flags
  undefined symbols (RA0402) **in editors**, so the DX is partly covered;
  correct interpreter-side validation is a focused binder-diagnostics change,
  not a builtin. Shipping a fragile partial (false-positives on imports) is
  explicitly declined per the design's anti-fragility rule; the member-chain
  slice (§2.6) is the part that *can* be done without false positives, so it was.
* **Tier 3** — ✅ SHIPPED (§2.7): **handle-based reflection** — first-class
  `MemberHandleValue` (MethodInfo/FieldInfo) replacing string-based member
  enumeration, create/query/use builtins, AOT-clean (no `System.Reflection`),
  use-ops delegating to the existing `invoke_method`/`get_field`/`set_field`.
* **Deliberately declined** — the bulk of the proposed ~150 ops are aliases
  (`samekindof`, `derivedof`, `mutableof`, `instanceof`=`is`, …) or low-benefit;
  shipping them violates "no magic without measurable benefit". `bitcast`/
  `reinterpret` are deferred until a real unsafe-memory use case exists (they're
  genuinely dangerous and need a clear model, not a speculative keyword).
* **Runtime flow-typing for `is`** — declined: Ra's runtime is duck-typed
  (member access works regardless of declared type) and there's no static type
  checker to benefit; the `is` *diagnostics* (NarrowingAnalyzer) already deliver
  the static win.

## 5. NativeAOT / performance

`nameof` is now a literal — strictly faster (no opcode, no symbol lookup) and
zero-alloc beyond the interned string. `as?`/`as!` add a parser flag + a branch
on the (already-present) cast error path — no hot-path cost for `as`. `alignof`
reuses the cached `NativeStructLayout`. No reflection, no new opcodes.

## 6. Tests

[`tests/reflection/test_introspection_ops.ra`](bin/x64/Release/net10.0/tests/reflection/test_introspection_ops.ra)
— 31 checks: nameof (bare/paren/member-chain/function/type), `as?` success +
out-of-range→null + `as`/`as!` throw (no crash), `native_alignof`/`struct_size`/
`struct_align`, Tier-2 `type_id`/`type_key`/`signature_of`/`default_of`/`zero_of`,
and `qual_name_of`/`full_name_of` (namespaced).

[`tests/reflection/test_member_handles.ra`](bin/x64/Release/net10.0/tests/reflection/test_member_handles.ra)
— 24 checks (Tier 3): method/field/`member_handle` create + null-on-miss, the
five query ops, static-flag, `member_invoke`/`get`/`set` (incl. arg-forwarding +
mutation), instance-derived handles, `members_of_handles`, wrong-kind rejection,
`member_owner` round-trip, `type_of(handle)`.

Full corpus: **288 files / 3266 assertions pass, zero new failures**;
`--selftest-stdlib` complete + exact (`reflect` group = 112 builtins).

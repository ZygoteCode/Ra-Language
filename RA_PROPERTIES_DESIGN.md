# Ra Language — Properties Design

Internal design document. Locks the grammar, semantics, AST shape, runtime
representation, and IR lowering strategy for **properties** in Ra. Reads as a
spec, not a tutorial — the user-facing tutorial belongs elsewhere.

Status: **locked**, in implementation.

Authoring principle: properties in Ra are a *first-class type-member kind*, not
syntactic sugar. They unify stored fields, computed accessors, validated
writes, lazy initialization, init-only assignment, observation hooks, abstract
contracts, and interface/trait requirements behind one orthogonal declaration
form. The implementation reuses the existing field-slot shape system so that
auto-stored properties cost the same as fields once the inline cache is
primed; only custom accessor bodies pay an extra dispatch.

---

## 1. TL;DR

```
class Account {
    pub prop balance: float { get; priv set; } = 0.0
    pub prop owner:   string { get; init; }

    pub prop fee_rate: float = 0.025 {
        get;
        set {
            if (value < 0.0) { throw "fee_rate must be non-negative" }
            field = value
        }
    }

    pub prop full_label: string => "${owner}: $${balance}"

    pub lazy prop history: List<string> = compute_history()

    pub prop balance_observed: float = 0.0 {
        get;
        set;
        observe {
            print("balance ${old} -> ${value}")
        }
    }

    abstract pub prop kind: string { get; }
}
```

The seven flavors above (read-write auto, init-only, validated, computed,
lazy, observed, abstract) all share the **same declaration form**: `prop NAME:
TYPE { accessors } [= default]`. Accessor blocks express the differences. No
hidden magic, no `_backing` field convention, no separate stored-vs-computed
syntax fork.

---

## 2. Why properties (and not just fields + methods)

The codebase already has fields and methods. Properties pull their weight
because:

- **Observability**: change the implementation without changing the syntax at
  the call site (`acc.balance` continues to compile when `balance` becomes a
  computed expression).
- **Contract enforcement at the boundary**: `init` accessors guarantee one-shot
  assignment without leaking a setter; `set` with validation can refuse bad
  values without forcing every caller to wrap an explicit method.
- **Symmetric read/write surface**: callers write `acc.balance = 10` and read
  `acc.balance`. No `get_balance()` / `set_balance(10)` asymmetry, no Java-style
  getter/setter boilerplate, no JavaScript-style `Object.defineProperty`
  metaprogramming.
- **Interface ergonomics**: an interface can declare `prop name: T { get; }`
  and any concrete provider — field, computed, lazy, anything — satisfies the
  contract uniformly.

What properties are **not**: they are not a replacement for methods. A
property with side effects beyond observation/validation is bad style. A
property is a *named, value-shaped member*; calls go through methods.

---

## 3. Grammar

EBNF, embedded inside class / struct / record / interface / trait member lists.

```
PropertyDecl     := PropertyModifiers "prop" Ident TypeAnnotation? PropertyDefault?
                    PropertyBody? Terminator

PropertyModifiers:= ( "pub" | "priv" | "static" | "abstract" | "override" | "lazy" )*

TypeAnnotation   := ":" Type

PropertyDefault  := "=" Expression

PropertyBody     := "{" Accessor ( ";" | NEWLINE )* "}"
                  | "=>" Expression       (* short-form readonly computed *)

Accessor         := AccessorModifiers AccessorKind AccessorBody?

AccessorModifiers:= ( "pub" | "priv" )?

AccessorKind     := "get" | "set" | "init" | "observe"

AccessorBody     := "{" StatementList "}"
                  | "=>" Expression
                  | (nothing)              (* auto accessor — only legal for
                                              get/set/init *)

Terminator       := NEWLINE | ";" | end-of-block
```

### 3.1 Form summary

| Form                                               | Meaning                                             |
| -------------------------------------------------- | --------------------------------------------------- |
| `prop x: int`                                      | Stored, public auto-get and auto-set                |
| `prop x: int { get; set; }`                        | Same as above (explicit)                            |
| `prop x: int { get; }`                             | Readonly auto (settable in constructor only)        |
| `prop x: int { get; init; }`                       | Init-only auto                                      |
| `prop x: int => self.y + 1`                        | Computed readonly, no storage                       |
| `prop x: int { get => self.y + 1 }`                | Same                                                |
| `prop x: int { get; set { ... } }`                 | Stored, custom setter                               |
| `prop x: int = 0`                                  | Stored auto with default value                      |
| `lazy prop x: int = expensive()`                   | Lazy single-shot evaluation                         |
| `abstract prop x: int { get; }`                    | Abstract contract                                   |
| `override prop x: int { get => ... }`              | Override in subclass                                |
| `static prop x: int { get; set; }`                 | Static (per-type) auto                              |
| `prop x: int { get; priv set; }`                   | Public read, private write                          |
| `prop x: int { get; set; observe { ... } }`        | Stored auto + observer hook                         |

### 3.2 Keywords introduced

`prop`, `get`, `set`, `init`, `observe`, `lazy`. The first five are
contextual *property keywords* — they are real lexer keywords but they only
have special meaning inside property declarations and property bodies. Their
existence outside that context is a parser-level diagnostic. `lazy` is a
top-level keyword used as a property modifier.

`field` and `value` are **not** keywords; they are implicit identifiers
injected into the accessor body's symbol table:

- inside `set`, `init`: identifier `value` is the bound parameter.
- inside `set`, `init`, `observe`: identifier `field` reads/writes the
  underlying backing slot directly (bypassing the accessor pipeline). Only
  legal in stored properties; the parser rejects `field` use in computed
  bodies.
- inside `observe`: additionally `old` is bound to the pre-update value.

### 3.3 Ambiguity notes

- `prop x: T = expr` vs `prop x: T { get => expr }`: the former allocates a
  backing slot initialized with `expr`; the latter is a computed property
  with no storage. Both compile; both are unambiguous from the syntax.
- Single-statement accessor `=> expr` is permitted for `get` (returns expr),
  `set` (evaluates expr — typically an assignment), and `init` (same as set).
- An accessor list may end with or without trailing semicolons.

---

## 4. Semantics

### 4.1 Member-name namespace

Property names live in the same name space as fields and methods of the
declaring type. A property and a field with the same name on the same type is
a **compile error**. A property and a method with the same name on the same
type is a **compile error**.

A class may define a property whose name matches an inherited field — that is
shadowing, treated identically to method shadowing today (without `override`
it is an error; with `override` the type/contract must match).

### 4.2 Backing storage

A *stored* property materializes a backing slot in the declaring instance,
indexed by the property's own name. The slot lives in the same shape-indexed
slot array (`ClassInstanceValue.FieldSlots`, `StructInstanceValue.FieldSlots`)
used for fields. The shape pass (`ClassTypeValue.BuildFieldShape`) is extended
to include stored properties.

This means:

- Auto-properties are **memory-identical to fields**.
- The inline cache (M28.1 PIC) treats a stored-property hit the same as a
  field hit and reuses `BR_CLASS_FIELD` / `BR_STRUCT_FIELD` once the IC has
  primed past the descriptor lookup.

A *computed* property has no backing slot. Reads run the getter body each
time. Writes run the setter body each time. There is no observable storage to
serialize or compare.

A *lazy* property is stored but tagged. Reads first check an
`InitializedLazies` bitset on the instance; the initializer runs at most once
per instance lifetime; subsequent reads serve the cached slot value. The
bitset is intentionally per-instance so two instances of the same class can
have independent lazy-init states.

### 4.3 Read semantics

For `obj.prop`:

1. Resolve the property descriptor by walking the type and its bases for the
   first declared `prop NAME`. Found = stop.
2. Visibility check: if the property has a getter, use the getter's
   per-accessor visibility (default = property's visibility, default = `priv`
   unless `pub` is present). If denied, raise a private-access error
   identical to the one for fields/methods.
3. Dispatch by descriptor kind:
   - **AutoStored** / **AutoReadonly** / **AutoInitOnly**: load the backing
     slot. Return value with the same alias/copy rules used for fields
     (`IsCopy` → `Copy()`, else share reference).
   - **Computed** (custom getter body): evaluate body with `self` bound to
     `obj` (already provided by the visitor `Context`). Return the body's
     value.
   - **Lazy**: check bit; if unset, evaluate the initializer expression,
     store into the slot, set the bit; return slot.
   - **Abstract**: raise runtime error "cannot read abstract property X".
4. No property → fall through to fields, then methods, then extensions, then
   record-deconstruct (existing chain).

### 4.4 Write semantics

For `obj.prop = expr`:

1. Resolve descriptor.
2. Visibility check on the setter (or `init`-er when applicable).
3. Dispatch by descriptor kind + write kind:
   - **AutoStored**: validate via `AnnotationValidator.CoerceAndValidate`
     (using metadata key `prop:Type.name`), then write the slot, then run
     observer block (if any) with `old`, `value`, `field` bound.
   - **AutoReadonly** (no setter declared): allowed only when
     `context.IsInConstructor`. Validates as above, writes slot. Caller
     setting `acc.balance = 5` outside the constructor is rejected with the
     same diagnostic shape as `final` field today.
   - **AutoInitOnly**: allowed when `context.IsInInitBlock || context.IsInConstructor || context.IsInWithExpr`. After construction completes, locked. (See §4.7.)
   - **Computed setter**: evaluate body with implicit `value` bound to the
     new value, `field` mapped through to backing slot (if a backing slot
     exists for this property — i.e. the property also has an auto-stored
     getter or an explicit storage hint). If the body never assigns to
     `field`, the property has no storage and the write is purely the body's
     side effects.
   - **Lazy**: writes are not allowed by default. `lazy` implies one-shot
     initialization. Allowing writes after init would silently invalidate the
     "memoized" contract. Setter must be explicitly declared on a lazy
     property to permit it.
   - **Abstract**: error.

### 4.5 Observer semantics

```
prop x: int = 0 {
    get;
    set;
    observe {
        // here: `old` is the value before the write; `value` is the new
        // value; `field` reads/writes the slot (post-write at this point).
        ...
    }
}
```

The observer block runs *after* the backing slot has been mutated. If the
observer modifies `field` it modifies the just-written slot. Observers can
not abort a write — they are notifications, not validators. To abort a write,
use a custom setter.

Observer execution is part of the set operation atomically — there is no
"slot has been written but observer not yet run" state visible to other
code (the implementation runs both inside the `Apply` body before returning).

### 4.6 Lazy semantics

```
lazy prop history: List<string> = compute_history()
```

- The initializer expression is evaluated **at most once** per instance.
- Initialization is **synchronous** within the calling thread; concurrent
  reads from two different fibers are not guaranteed to share the result —
  the Ra runtime is single-threaded except for explicit `spawn`, and within
  a single fiber chain the bit-and-store sequence is non-interruptible.
- Re-entrant access during initialization raises a "lazy property accessed
  during its own initialization" error. This is detectable with a per-thread
  set of "currently-initializing" `(instance, name)` pairs guarded behind the
  bit flag.

### 4.7 Init-only semantics

`init` accessors are settable only during construction. "Construction"
covers:

- inside the body of any `fn ClassName(...)` constructor;
- inside the implicit field-initializer chain (`= expr` defaults on the
  property declaration);
- inside the `with { ... }` literal used in with-expressions on records
  (treated as a fresh construction over the modified field list).

After the constructor returns, the slot is locked and any subsequent
`obj.x = v` is rejected with the same diagnostic shape used for `final`
fields today. `IsInConstructor` is already plumbed in `Context`; init-only
properties piggyback on that flag with no new infrastructure.

### 4.8 Visibility

- The property's overall visibility (`pub` / `priv`) defaults the visibility
  of every accessor.
- Each accessor can be individually qualified: `pub get; priv set;`.
- An accessor cannot be **wider** than the property. `priv prop x { pub get;
  }` is a compile error.
- Outside the declaring type, denied accessor → access error identical in
  shape to a private-field error.
- Inside the declaring type (and friend types — for now, only the declaring
  type), all accessors are reachable regardless of `priv`.

### 4.9 Override

- `override prop` requires a matching `prop` (or compatible `field`) on the
  base. The override's type must equal the base's type (no covariance in v1
  to keep the type system simple — to revisit).
- An override cannot remove an accessor the base provided (a `{get; set;}`
  base cannot be overridden by `{get;}` only).
- An override can **add** an accessor the base did not provide, but only if
  the base was abstract.
- Visibility cannot be narrowed by an override.

### 4.10 Abstract

- `abstract prop` lives in `class` and `abstract record class` only.
  Interfaces and traits use the *requirement* form, which is syntactically
  identical (`prop x: T { get; }` declaration in the interface body) but
  semantically a contract, not a stored slot.
- An abstract property has no backing slot. The shape pass does not allocate.
- A non-abstract subclass must `override` every abstract property in the
  hierarchy. Verified by the existing abstract-requirements machinery (which
  is extended to enumerate properties alongside methods).

### 4.11 Static

- Static properties live on the type definition value (`ClassTypeValue`,
  `StructTypeValue`, `RecordTypeValue`). Storage uses the existing
  `StaticFields` dictionary keyed by name.
- Static properties cannot be `init` — there is no constructor to scope to —
  but they can be `lazy`, `readonly` (get only), or fully custom.

### 4.12 Generic types

A property's type may reference the declaring type's type parameters. No new
machinery is required: the existing field-type plumbing already resolves
type parameters through the instance's `GenericBindings`.

### 4.13 Self-reference & recursion

A property body can read other properties of the same instance via `self.X`.
The visitor detects re-entrant access (same instance + same property
descriptor on the dispatch stack) and raises a
"recursive property access on '{name}'" error. The detector lives in a thin
per-fiber stack — `AsyncContext.PropertyAccessStack` — pushed/popped around
the body call. Cost is one stack push/pop per *custom* accessor invocation
(stored auto-properties bypass the body entirely so they pay nothing).

### 4.14 Pattern matching, deconstruction, serialization

- Pattern matching: properties participate in object-pattern matches
  (`obj is Account { balance: 0.0 }`). The pattern reads through the getter,
  same as any other member access.
- Deconstruction: records' positional deconstruction is over **primary
  fields only**, unchanged. A record's *body* properties are not
  deconstructed positionally (preserves the rule "auto-derived equality is
  over primary fields").
- `to_string`, `equals`, `hash`: properties contribute to the auto-derived
  forms **only when stored**. Computed and lazy properties are skipped.
- `@derive(equals=false)` / `@derive(to_string=false)` continue to suppress
  the derive pass as today.

---

## 5. AST shape

Two new node kinds:

```csharp
enum AstNodeType { ..., PropertyDefinition, PropertyAccessor }
```

```csharp
public sealed class PropertyDefinitionNode : AstNode {
    public Token NameTok;
    public TypeDescriptor? PropertyType;
    public AstNode? DefaultValueNode;
    public List<PropertyAccessorNode> Accessors;
    public bool IsPublic;
    public bool IsStatic;
    public bool IsAbstract;
    public bool IsOverride;
    public bool IsLazy;
}

public sealed class PropertyAccessorNode : AstNode {
    public Token KindTok;                 // get / set / init / observe
    public PropertyAccessorKind Kind;
    public PropertyAccessorVisibility Visibility;
    public AstNode? BodyNode;             // null = auto accessor
    public bool IsAuto;                   // shorthand for BodyNode == null
}

public enum PropertyAccessorKind { Get, Set, Init, Observe }
public enum PropertyAccessorVisibility { Default, Public, Private }
```

Both are registered in `Interpreter.RegisterVisitors`. The accessor node is
never directly dispatched at top level — its visitor is a no-op stub for the
sake of the registration contract (mirroring how `Argument` /
`InterfaceMethodSignature` exist).

`StructFieldDefinitionNode` is **not** reused. Properties are a distinct kind
so the parser, static analyzer, and visitor pipeline can keep their concerns
clean. (Earlier prototypes tried to reuse the field node — the resulting
visitor became a switch on a `IsProperty` flag and the logic split was
worse.)

### 5.1 Container nodes updated

`ClassDefinitionNode`, `StructDefinitionNode`, `RecordDefinitionNode`,
`InterfaceDefinitionNode`, `TraitDefinitionNode` gain
`List<PropertyDefinitionNode> Properties`. The lists are append-only at parse
time and read-only thereafter. They live next to existing `Fields` /
`Methods` lists.

`ICallableMethodDefinition` is **not** extended. Property accessors are not
exposed via the method dispatch surface — they are accessed through the
property access pipeline. Bound accessor values (for callable references like
`Account::balance.set`, if we add that later) would be a future-extension
work item.

---

## 6. Runtime model

### 6.1 PropertyDescriptor

```csharp
public sealed class PropertyDescriptor {
    public string Name;
    public TypeDescriptor? PropertyType;
    public bool IsPublic;
    public bool IsStatic;
    public bool IsAbstract;
    public bool IsOverride;
    public bool IsLazy;

    public PropertyAccessor? Getter;        // null = no getter
    public PropertyAccessor? Setter;        // null = no setter
    public PropertyAccessor? Initter;       // null = no init accessor
    public PropertyAccessor? Observer;      // null = no observer

    public AstNode? DefaultValueNode;       // = expr at decl site

    public bool HasBacking =>               // does this prop store anything?
        IsLazy || (Getter?.IsAuto ?? false) || (Setter?.IsAuto ?? false) || (Initter?.IsAuto ?? false);

    public bool IsComputed => Getter != null && !Getter.IsAuto && !HasBacking;
}

public sealed class PropertyAccessor {
    public PropertyAccessorKind Kind;
    public PropertyAccessorVisibility Visibility;
    public bool IsAuto;
    public AstNode? Body;                   // null when IsAuto
}
```

Per declaring-type values (`ClassTypeValue`, `StructTypeValue`,
`RecordTypeValue`, `InterfaceTypeValue`, `TraitTypeValue`) gain:

```csharp
public List<PropertyDescriptor> Properties { get; }
public Dictionary<string, PropertyDescriptor> PropertyByName { get; }
public PropertyDescriptor? GetProperty(string name);     // walks bases
```

The base-walk for class / record-class is identical to the field/method
base-walk (visited-set guard, then `BaseClass`/`BaseRecord` recursion).

### 6.2 Slot allocation

`ClassTypeValue.BuildFieldShape` (already present) is renamed in spirit to
"shape build" and extended to allocate one slot per **stored** property in
addition to one slot per field. The slot key is the property's `Name`. So
the read/write fast path for an auto-stored property is identical to a
field read/write: the same `FieldSlots[idx]` indirection.

A property cannot share a slot with a same-named field — the
`prop` / `field` name collision is a parse error (§4.1).

### 6.3 Lazy-init bitmap

```csharp
public class ClassInstanceValue {
    ...
    public uint[]? LazyInitBits;   // null when class has no lazy props
}
```

Allocated lazily on first lazy-init mutation. One bit per lazy property,
indexed by the lazy slot's *lazy-only* dense index (computed at shape build
time, not the same as the regular slot index). Test/set with
`Interlocked.Or` is not required (single-threaded fiber model), so a plain
`bits[idx >> 5] |= 1u << (idx & 31)` suffices.

### 6.4 Per-fiber recursion guard

```csharp
public class AsyncContext {
    ...
    public Stack<(object instance, string propName)> PropertyAccessStack;
}
```

The stack is push-pop'd around every custom accessor body invocation. Auto
accessors skip the push/pop — they cannot recurse.

### 6.5 Initialization order

When constructing an instance, the existing `InitializeFieldChain` walks
base-first and assigns each field its default. The new pass runs after the
field pass and follows the same base-first order:

1. For each stored property in declaration order: write its
   `DefaultValueNode` evaluation to the backing slot (validate via the
   annotation pipeline using key `prop:Type.name`). Lazy properties skip
   this and stay in their uninit-bit state.
2. For each abstract property: ensure a non-abstract override exists in the
   most-derived type. (The existing
   `GetUnresolvedAbstractRequirements` is extended to enumerate property
   contracts too.)

This pass runs *before* user-defined constructors so that the body of the
constructor sees property defaults already populated.

---

## 7. Pipeline integration

### 7.1 Parser

`ParseClassDefinition`, `ParseStructDefinition`, `ParseRecordDefinition`,
`ParseInterfaceDefinition`, `ParseTraitDefinition` gain a `prop` branch in
their member-loop. After the existing modifier scanning (`pub override
abstract static`) the parser checks for `lazy` (optional) followed by `prop`
and dispatches to `ParsePropertyDeclaration`.

`ParsePropertyDeclaration` returns a `PropertyDefinitionNode`. The accessor
loop allows accessors in any order, separated by `;` or newlines, until `}`
or the implicit terminator from a `=> expr` short form.

### 7.2 DeriveTransformer

Untouched. Property declarations do not interact with derive macros in v1
(open work item: `@derive(properties=true)` for record-style auto-props from
primary fields — out of scope here).

### 7.3 StaticAnalyzer

Extended to warn on:

- Property whose getter and setter visibilities differ in a way that exposes
  a `priv` setter to the outside (when overall property is `pub`, setter
  must be explicitly `pub` or `priv`; `priv set;` makes the asymmetry
  intentional — no warning).
- Lazy property without a default initializer (compile error).
- Computed property with `field` referenced in body (compile error).
- Observer that throws (warning only; observers should not abort writes —
  fix the design to use a setter instead).

### 7.4 Resolver

Extended to assign `BindingId`s to the implicit `value`, `field`, `old`
parameters inside accessor bodies. These bindings live on a per-accessor
frame analogous to a method frame.

### 7.5 Interpreter / Visitors

New visitor: `PropertyDefinitionNodeVisitor.Apply(node, context, type)` is
called by the class/struct/record/interface/trait visitor while building the
type definition; it constructs the `PropertyDescriptor` and adds it to the
type's `Properties` list. It does not allocate any per-instance state — that
happens in the construction path through the shape system.

`PropertyAccessorNodeVisitor` is registered to keep the
`RegisterVisitors` map dense; calling it directly is a runtime error (same
shape as the `Argument` visitor today).

`MemberAccessHelper.ApplyAndPrime`: insert a property-descriptor probe at
the *start* of the StructInstance / ClassInstance / RecordInstance branches,
before the field probe. On hit, the IC `BranchKind` is one of:

- `BR_PROP_AUTO_STORED` — slot read via cached `FieldIndex`. Cheap; same
  cost as `BR_CLASS_FIELD`.
- `BR_PROP_COMPUTED` — full getter-body call. CachedAux pins the accessor
  node so the descriptor lookup is one-shot.
- `BR_PROP_LAZY` — bitmap probe, possible init, slot read. CachedAux pins
  the accessor for re-init.
- `BR_PROP_ABSTRACT` — error path; should never reach a real instance.
- `BR_PROP_STATIC_GET` — static read on `ClassType` target.

`MemberAssignmentHelper.Apply`: parallel property probe before the field
probe, with branches:

- `BR_PROP_AUTO_SET` — slot write with optional observer.
- `BR_PROP_AUTO_INIT` — slot write gated on `IsInConstructor`.
- `BR_PROP_CUSTOM_SET` — setter-body call.
- `BR_PROP_READONLY` — error.
- `BR_PROP_LAZY_WRITE` — error unless explicit setter.

### 7.6 IR / VM

No new opcodes in v1. Existing `OP_GET_MEMBER`, `OP_SET_MEMBER`,
`OP_SET_INDEX` route through the helpers above, which now know about
properties.

Once the IC primes a `BR_PROP_AUTO_STORED` hit, the actual machine work on
the dispatch hot path is **identical** to a primed `BR_CLASS_FIELD` hit —
one bounds check, one slot read. The IC slot machinery (M28.1, M42 PIC) is
untouched; properties are just two more branch tags.

In a later milestone we may add `OP_GET_PROP` / `OP_SET_PROP` opcodes that
skip even the descriptor probe by baking the branch tag into the opcode
stream. The shape system is already a precondition; the cost is one new
opcode pair and a small IR-compiler change. Not in scope for the initial
landing.

### 7.7 Annotations

`MetadataTarget.BuildKey` gains a new target kind: `Property`. Key shape
`prop:TypeName.propName` (and `staticprop:TypeName.propName` for statics).
The annotation pipeline already routes through
`AnnotationValidator.CoerceAndValidate(key, value, label, context)`; the
helper feeds the property key into it on set/init.

Built-in introspection functions (`annotations_of`, `has_annotation`,
`annotation_arg`) are extended to accept property targets via the existing
mechanism (the targets are just metadata keys; once
`MetadataKeyResolver` can build a property key from a textual selector
like `"prop:Account.balance"` we're done).

### 7.8 BorrowChecker

Property reads/writes feed the borrow checker the same way field
reads/writes do today — they read or write storage of the same `ClassInstance`
/ `StructInstance`. The borrow checker already treats them as field
operations; no new rules.

---

## 8. Diagnostics

Each diagnostic carries the existing `RuntimeError` / parser error shape
(position, primary label, help).

| Code (informal)              | Trigger                                                               |
| ---------------------------- | --------------------------------------------------------------------- |
| `prop/field-name-collision`  | property with same name as field/method on the same type              |
| `prop/no-such-accessor`      | get on prop with no getter (only `set/init` declared)                 |
| `prop/readonly-write`        | write on prop with no setter or init (outside constructor)            |
| `prop/init-only-write`       | write on init-only prop after construction                            |
| `prop/abstract-read`         | read on abstract prop (instance method called pre-override)           |
| `prop/abstract-not-impl`     | non-abstract subclass missing required abstract-prop override         |
| `prop/lazy-write`            | write on lazy prop without explicit setter                            |
| `prop/lazy-recursive`        | re-entrant read on lazy prop during initialization                    |
| `prop/recursive-access`      | re-entrant read on custom-getter prop                                 |
| `prop/accessor-wider`        | accessor visibility wider than property visibility                    |
| `prop/override-mismatch`     | override prop type does not match base                                |
| `prop/override-missing-acc`  | override drops accessor that base provided                            |
| `prop/field-keyword-misuse`  | `field` used in computed property body (no backing slot)              |

---

## 9. Migration & compatibility

- Existing fields and methods are untouched. No code that doesn't use
  `prop` changes behavior.
- The `field` and `value` identifiers are not new keywords; they are
  contextual bindings inside accessor bodies. Code that uses `value` or
  `field` as a regular variable name elsewhere keeps working.
- Reading `obj.x` continues to look up the field first if there is no
  property `x`. The property probe runs **only when a descriptor exists**,
  and the descriptor is on the type definition — there is no per-instance
  cost.
- The new `prop`/`get`/`set`/`init`/`observe`/`lazy` keywords steal those
  identifiers globally (consistent with how `class`, `struct`, `record`,
  etc. already steal theirs). Migration burden: rename any user-defined
  identifiers that conflict. The existing test corpus has zero collisions.

---

## 10. Comparison to other languages

| Concern                         | C#                                  | Kotlin                       | Swift                          | Dart                       | Ra                                          |
| ------------------------------- | ----------------------------------- | ---------------------------- | ------------------------------ | -------------------------- | ------------------------------------------- |
| Stored auto                     | `int X { get; set; }`               | `var x: Int`                 | `var x: Int`                   | `int x`                    | `prop x: int { get; set; }`                 |
| Readonly auto                   | `int X { get; }` + ctor init        | `val x: Int`                 | `let x: Int`                   | `final int x`              | `prop x: int { get; }`                      |
| Init-only                       | `int X { get; init; }` (C# 9+)      | n/a (val + constructor)      | `let x: Int` (init in init)    | `final` + ctor             | `prop x: int { get; init; }`                |
| Computed                        | `int X => …;`                       | `val x get() = …`            | `var x: Int { … }`             | `int get x => …;`          | `prop x: int => …` or `{ get => … }`        |
| Validated set                   | `set { if (…) throw … }`            | `set(value) { … }`           | `didSet`/`willSet`             | `set x(int v) { … }`       | `set { if (…) throw … field = value }`     |
| Lazy                            | `Lazy<T>` wrapper                   | `by lazy { … }`              | `lazy var x = …`               | `late T x` (not really)    | `lazy prop x: T = …`                        |
| Observable                      | INotifyPropertyChanged event boilerplate | `observable` delegate    | `didSet`                       | `setter notify`            | `observe { … }` block on prop               |
| Delegated                       | bespoke                             | `by Delegate()`              | property wrappers              | n/a                        | future work (out of v1 scope; see §11)      |
| Abstract                        | `abstract X { get; set; }`          | `abstract var x: Int`        | `var x: Int { get set }` (proto) | `abstract X x;`           | `abstract prop x: int { get; set; }`        |
| Accessor visibility split       | `get; private set;`                 | `private set`                | `private(set)`                 | `_x` convention            | `pub get; priv set;`                        |
| Backing-field access            | `_x` convention                     | `field` keyword inside accessor | implicit, no explicit name | `_x` convention            | `field` keyword inside accessor             |
| Interface contract              | `int X { get; }` in interface       | `val x: Int` in interface    | property in protocol           | abstract getter            | `prop x: int { get; }` in interface         |

Ra's design takes the **`field` keyword** idea from Kotlin (cleanest
backing-field access), the **uniform accessor list** from C# (one
declaration form for stored, computed, init, etc.), the **observation
block** from Swift's `didSet` (but as a single `observe` block rather than
willSet/didSet split — observers are notifications, validations live in
setters), and the **per-accessor visibility** from C# / Kotlin.

Key differentiators:

- Ra unifies `lazy` as a *modifier on the property*, not a separate
  declaration (Kotlin's `by lazy`) or a wrapper type (C#'s `Lazy<T>`).
- Ra unifies validation and the annotation pipeline. `@min(0)` on a property
  is enforced on every write, including init, without a custom setter.
- Ra's `observe` block has explicit `old`, `value`, `field` — no name-collision
  with willSet / didSet's implicit `newValue` / `oldValue`.
- Ra distinguishes `init-only` (settable in constructor only, no setter
  generated) from `readonly` (settable only by the in-class field
  initializer or constructor assignment). Same surface to the outside, but
  the two intentions are spelled out separately in the declaration.

---

## 11. Out of scope (future work)

The following are intentionally **not** in v1. Each is feasible but not on
the critical path; tracked here so the future implementations have a
landing pad.

- **Delegated properties** (`prop x: T by Lazy<T>(...)` style). Requires a
  small protocol for delegate objects (`get(self, name)`, `set(self, name,
  value)`). Adds dispatch indirection but is clean to layer.
- **Indexer-like properties** (`prop this[i: int]: T { get; set; }`).
  Promising; needs a separate AST node for the index-parameter list.
- **Covariant property override** (override prop returns a more derived
  type). Requires type-checker work that overlaps with the broader
  variance story.
- **Property references** as first-class values (`Account::balance`
  resolving to a `PropertyRef<Account, float>`). Useful for binding, MVC
  patterns, reflection. Needs a new `PropertyRefValue` runtime type and a
  parser hook.
- **Per-property change-tracking annotation** (`@track`) emitting an event
  on every set. Layerable on top of `observe`.
- **`OP_GET_PROP` / `OP_SET_PROP` IR opcodes** — bypass the descriptor
  probe; descriptor lookups already-cached in the IC. Worth measuring;
  defer until profiling shows the probe to be hot.

---

## 12. Implementation milestones (this PR)

P1 — lexer keywords (`prop`, `get`, `set`, `init`, `observe`, `lazy`).
P2 — AST nodes + AstNodeType enum extension + visitor registration stubs.
P3 — parser: property declaration grammar inside class/struct/record/interface/trait member lists.
P4 — runtime: `PropertyDescriptor`, `PropertyAccessor`, type-value
     storage (`ClassTypeValue.Properties` etc.), `BuildFieldShape`
     extension.
P5 — visitor: `PropertyDefinitionNodeVisitor` registers descriptors
     into the type-value during class/struct/record/interface/trait build.
P6 — read path: `MemberAccessHelper.ApplyAndPrime` property probe and
     new IC branch kinds (`BR_PROP_*`). Stored-auto path reuses the
     existing slot read.
P7 — write path: `MemberAssignmentHelper.Apply` property probe;
     auto/init/readonly/custom/lazy branches.
P8 — construction-time initializer pass: defaults applied base-first
     through the existing field-init chain, extended for stored
     properties.
P9 — abstract & override enforcement (extended `GetUnresolvedAbstractRequirements`).
P10 — interface / trait satisfaction (extended `ImplementsInterface` /
      `SatisfiesTrait` to require matching property descriptors).
P11 — tests: `tests_properties.ra` covering every form and every
      diagnostic.
P12 — corpus parity sweep.
P13 — docs (this file).

Each milestone is a single self-contained step. Build green after every
milestone. Corpus parity at every milestone.

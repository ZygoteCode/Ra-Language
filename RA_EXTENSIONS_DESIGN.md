# Ra Extensions — Design

`extend T { ... }` adds members to an existing type **without** mutating
the type's storage shape or member chain. The receiver's runtime
identity, hidden-class shape and method-table stay intact; extension
members are dispatched through a separate registry that the
member-access pipeline consults *after* every native probe.

The model has four design pillars:

1. **Composition, not surgery.** An extension never alters the
   declaring type. Two modules can extend the same class with
   disjoint member sets and never collide unless they pick the same
   name; even then, the registration order is deterministic and the
   resolution rule is explicit.
2. **Determinism over magic.** The resolution chain is fixed: native
   member → extension property → extension method. Within
   extensions, **local-first** beats imported, and along the class
   hierarchy **derived-first** beats base. No reflection. No
   speculative lookup. No version-specific tie-breakers.
3. **Visibility is first-class.** `pub extend` exports every member;
   `extend` (no `pub`) is private to the declaring module. A `pub`
   on an individual `fn` / `prop` lifts that member into the export
   set even when the surrounding block is private — the same
   override-up rule the rest of the language uses.
4. **AOT-friendly storage.** Extensions never allocate new slots on
   the receiver. Properties are computed-only; their getter and
   setter execute against `self` and may read/write existing
   fields, but the receiver's hidden class never gains a new entry.
   No reflection, no dictionary-of-instances side maps, no
   per-instance map invalidations.

---

## 1. Grammar

```
extend       := ('pub')? 'extend' TYPE '{' (member NEWLINE*)* '}'
member       := method | property
method       := ('pub')? fn_decl                  -- see Functions
property     := ('pub')? prop_decl                -- see RA_PROPERTIES_DESIGN
```

Inside the extension body:

- **Methods** follow the same grammar as a class/struct method. They
  must have a body (no `abstract`), cannot be constructors.
- **Properties** follow the property grammar with these
  extension-specific constraints (rejected at registration):
  - No `lazy` — no slot to memoise into.
  - No `static` — extensions extend instances, not type members.
  - No `abstract` / `override` — there is no override chain to fulfil
    or replace.
  - No `init` / `observe` accessors — both require backing storage.
  - No auto `get` / auto `set` — implies backing storage.
  - No `= default` — same reason.

The supported property shapes are therefore **computed**:

```ra
extend Box {
    pub prop scaled: int {
        get => self.n * 2;
        set { self.n = value / 2; }
    }

    pub prop tripled: int => self.n * 3;   // arrow shorthand
}
```

## 2. Registry

`ExtensionRegistry` ([Interpreter/Runtime/ExtensionRegistry.cs](Interpreter/Runtime/ExtensionRegistry.cs))
holds two dictionaries keyed by **target type name** (a bare type
identifier, not a fully-qualified or generic-bound form):

- `_methods : Dictionary<string, List<ExtensionMethodEntry>>`
- `_properties : Dictionary<string, List<ExtensionPropertyEntry>>`

Each `*Entry` carries:

| Field            | Purpose |
|------------------|---------|
| `IsBlockPublic`  | The `pub` on the surrounding `extend` block. |
| `IsLocal`        | `true` when the entry was registered by *this* module. `false` after it crosses a module boundary via import. |
| `DeclaringModule`| Absolute path of the declaring module. Carried for future diagnostics. |
| `Method` / `Descriptor` | The AST node / runtime descriptor. |

`Register*` is monotonic — entries are never removed. The
register-time validator only catches duplicate **local** property
declarations on the same target; duplicate method overloads are
allowed (deliberate — overload resolution is by argument-binding).

### Per-module storage

Each loaded module gets its **own** `ExtensionRegistry`
([Interpreter/Modules/ModuleManager.cs](Interpreter/Modules/ModuleManager.cs))
allocated alongside its `SymbolTable`. The `Context.Extensions`
property is the registry of the currently executing scope; child
contexts inherit it by reference.

### Import semantics

| Form                                       | Behaviour |
|--------------------------------------------|-----------|
| `import * from "x"`                        | Merges every **effectively-public** extension from `x` into the importer's registry, with `IsLocal = false`. |
| `import { Name } from "x"`                 | Imports the named binding only. **Does not** import extensions — extensions are not first-class import targets in v1. |
| `import "x" as alias`                      | Does not import extensions into the importer. The alias module wrapper retains its own registry, queryable via `alias.member(x)`. |

Effective public = `block.IsPublic OR member.IsPublic`. This
mirrors how the rest of the language treats type-level vs
member-level visibility.

## 3. Resolution

Member access (`receiver.name`) consults the chain in this order
([Interpreter/Runtime/MemberAccessHelper.cs](Interpreter/Runtime/MemberAccessHelper.cs)
+ [Interpreter/Visitors/Structs/MemberAccessNodeVisitor.cs](Interpreter/Visitors/Structs/MemberAccessNodeVisitor.cs)):

1. Native property (`PropertyDescriptor` declared on the type).
2. Native event.
3. Native field.
4. Native method.
5. **Extension property** (this work — `ExtensionDispatch.TryGetProperty`).
6. **Extension method group** (`Extensions.Resolve`).

For both extension steps the registry walks two passes:

- **Pass 1 — local.** For each candidate target key
  (`Dog → Animal → Object` for a Dog instance), look up entries
  with `IsLocal == true`.
- **Pass 2 — imported.** Same walk, `IsLocal == false`.

Within a pass, the more-derived target wins. Within a single
target bucket, the registration order is preserved so methods
form a deterministic overload set; properties return the first
hit (duplicates were rejected at registration when local).

`MemberAssignmentHelper.cs` mirrors the structure for the
write path: native field/property set → extension property
set → error. Native fields beat extension properties so writes
land on storage when both share a name (test E13).

### Method dispatch

`BoundExtensionMethodGroupValue.Execute`
([Interpreter/Values/Classes/BoundExtensionMethodGroupValue.cs](Interpreter/Values/Classes/BoundExtensionMethodGroupValue.cs))
takes the ranked candidate list, picks the first whose signature
binds with `MethodCallBinder.CanBind`, and runs the IR-compiled
body in a child context with `self` bound to the receiver. The
chosen candidate is honest about its declared target — `selfTypeName
= TypeSystem.GetExtensionTargetName(receiver)` records the runtime
type so inside the body `self`'s static type matches the dynamic
shape (a `Dog` calling an `extend Animal` method still sees `self
: Dog`).

### Property dispatch

`ExtensionDispatch` ([Interpreter/Runtime/ExtensionDispatch.cs](Interpreter/Runtime/ExtensionDispatch.cs))
forwards to `PropertyAccessOps.Get` / `Set` with the receiver as
both `instance` and `self`. Because every supported extension
property is computed (no backing slot), `PropertyAccessOps`'
slot-write paths are unreachable; the `ExecuteGetterBody` /
`ExecuteWriterBody` paths run the accessor body verbatim with
`self` (and `value` for setters) in scope.

`isInsideDeclaringType` is set to `entry.IsLocal`. The intent: a
`priv` accessor on a local extension only applies "from outside
this module"; since extensions don't have a declaring *type*, the
declaring module is the relevant boundary. Imported entries are
always treated as outside — `priv get` / `priv set` on an
imported extension is therefore inaccessible.

## 4. Inline cache

The IC ([MemberAccessHelper.ApplyWithIc](Interpreter/Runtime/MemberAccessHelper.cs))
**does not** cache extension property hits. Property reads are
value-dependent (they execute an arbitrary expression body), and
the existing IC opts out for native properties too. Caching
would only shave the dispatch lookup itself, which is already a
single `ResolveProperty` over small lists.

Extension method hits keep the existing `BR_*_EXT` branches:
when an IC slot is primed for an extension dispatch, the slot
refreshes lazily by re-querying the registry, so adding new
extensions at runtime still appears immediately at every
already-primed call site.

## 5. Diagnostics

- **Duplicate property** (local-local) → registration error with
  the property name and target type.
- **Duplicate property** (imported) → silently dropped; the
  importer's existing entry wins, matching the local-first rule.
- **Invalid extension property shape** (lazy / static / abstract /
  override / init / observe / auto / default value) → registration
  error with a concrete suggestion (e.g. *"use `get => <expr>`"*).
- **Member not found** → existing "no such field, method or
  extension" error. Already directs the user to the `extend` block.

## 6. Trade-offs

| Choice                                   | Why |
|------------------------------------------|-----|
| Computed-only extension properties.     | Stored properties require either layout mutation (breaks AOT shape) or per-instance side maps (allocation + IC thrash). Computed access stays O(1) lookup + arbitrary expression cost — same as a native property body. |
| String-keyed registry (no `<T>` arity). | Generic dispatch by target arity adds complexity without changing observable behaviour for v1 — `extend Box<T> { ... }` already registers under `Box` (the bare name) and the body operates on the receiver's structural members. Specialised generic extensions are a follow-up. |
| First-match overload binding.           | Mirrors method-group dispatch on classes. Surface an ambiguity diagnostic when two same-tier candidates bind identically; this can be layered in without churn because the resolution list is already ranked. (Not implemented in v1.) |
| Module-scoped privacy boundary.         | Treating modules as the privacy unit fits Ra's existing import model (per-module `SymbolTable`, per-module `ExtensionRegistry`). A type-scoped boundary would require tracking "which type declared this extension" for non-type targets (primitives), which doesn't generalise. |
| Selective imports don't pull extensions.| `import { x } from "y"` is value-import. Adding extension-import-by-name would require a separate `use ext T.name from "y"` clause; the wildcard form covers the common case and keeps the grammar lean. |

## 7. v2.2 additions

### Generic specialization

Extension blocks may bind generic args explicitly:

```ra
extend Box<int>    { pub fn squared(): int { ret (self.v as int) * (self.v as int); } }
extend Box<string> { pub fn shout():   string { ret (self.v as string) + "!"; } }
```

The receiver's runtime `GenericBindings` map is compared against the
declared `TargetType.GenericArgs` at resolution time. Entries with no
generic args always match; entries with generic args only match when
the receiver's class definition was instantiated with the same type
arg names in declaration order. Only class/struct receivers carry
generic bindings — `extend list<int>` is currently a no-op specialisation
because primitive containers do not preserve element type per-instance.

### `@sealed extend`

```ra
@sealed extend Locked {
    pub fn one(): int { ret 1; }
}

// Any subsequent declaration on `Locked` — in this module or any
// importer — raises a registration error.
```

The seal lives in `ExtensionRegistry._sealedTargets`. `MergeExtensions`
propagates the seal across module boundaries: once an imported module
seals a target, the importer adopts the seal too and refuses local
follow-ups. Already-registered entries from before the seal are
unaffected.

### Extension operators

```ra
extend Vec2 {
    pub operator+(other: Vec2): Vec2 { ret Vec2(self.x + other.x, self.y + other.y); }
}
```

Stored as `ExtensionOperatorEntry`. The fallback path in
`RuntimeValue.TryOperatorDispatch` consults the registry when the
receiver's native operator table has no match. The operator body
binds the receiver as `self` and the arg as `other` — same convention
as native class/struct operators. Comparison operators must return
`bool` per the existing rule.

### Extension indexers

```ra
extend Grid {
    pub fn op_index(i: int): int       { ... }   // grid[i]
    pub fn op_index_set(i: int, v: int) { ... }   // grid[i] = v
}
```

Indexers are extension methods with reserved names `op_index` and
`op_index_set`, surfaced through `ExtensionIndexerEntry` in the
registry. The parser scans the method list for those names and tags
them as indexers; they are still callable by name when needed for
disambiguation. `ClassInstanceValue.ListAccess` /
`StructInstanceValue.ListAccess` (and the corresponding `ListSet`)
probe the registry before reporting the default `IllegalOperation`.

### Extension events

```ra
extend Bus {
    pub event Beat(n: int) { pub raise; }
    pub fn fire(n: int) { self.Beat(n); }
}

var bus = Bus();
bus.Beat.on(|n| handle(n));
bus.fire(42);
```

Stored as `ExtensionEventEntry`. Subscriber lists piggy-back on the
existing per-instance `EventSubs` dictionary already maintained by
`ClassInstanceValue` / `StructInstanceValue` for native events.
`MemberAccessHelper` checks for an ext-event after the native event
probe and before the ext-property/method probes; finding one
returns an `EventSubscriptionValue` indistinguishable from one
returned for a native event. Subscribe/raise visibility honors
the same accessor syntax as native events (`pub subscribe;` /
`pub raise;`).

### Cross-module ambiguity diagnostic

`BoundExtensionMethodGroupValue` now carries an optional
`Entries: List<ExtensionMethodEntry>`. When two candidates from the
same tier (both imported, both local) come from **different**
declaring modules and both bind to the call, dispatch refuses with:

```
ambiguous extension method 'foo' — multiple imported overloads bind to this call:
  - foo(a, b) from /path/to/mod_a.ra
  - foo(a, b) from /path/to/mod_b.ra
help: shadow the ambiguous import with a local 'extend' declaration,
      or disambiguate by calling the method via the module wrapper
```

The local-first / derived-first tiers absorb the common cases; the
diagnostic only fires in the genuinely ambiguous remainder.

## 8. Test coverage

`tests_extensions.ra` (21 cases) covers:

- E1–E15: original methods + properties + visibility + shadowing.
- **E16**: generic specialization (`extend Box<int>` vs `extend Box<string>`).
- **E17**: extension operator `+`.
- **E18–E19**: extension indexer get + set.
- **E20**: extension event subscribe + raise.
- **E21**: `@sealed extend` registration.

Existing regression suites pass: `test_extension.ra`, `tests_properties.ra`,
`tests_lambdas.ra`, `tests_unions.ra`, `tests_patterns.ra`,
`tests_delegates.ra`, `tests_events.ra`.

## 9. Follow-up

- **`use ext T.name from "y"`** for surgical extension imports.
- **Generic specialisation for primitive containers** (`extend list<int>`).
  Blocked on the runtime not preserving element type per-instance for
  built-in list / map / set.
- **Ambiguity diagnostic for properties/operators/events/indexers**
  (currently only methods).

## 10. Extension fields (v2.3)

Extension fields turn `extend T { var x: int = 0 }` into real
storage **without** mutating the receiver's hidden-class shape.

```ra
pub class Box { pub Box() { pass; } }

extend Box {
    pub var counter: int = 0;            // mutable, default 0
    pub const TAG: string = "boxed";     // immutable, frozen after eval
    pub final id: int;                   // single-shot write
    pub var tags: list = [];             // any RuntimeValue type
    pub var pin: Tag;                    // class-typed
    pub var doubled: int = self.base * 2;// default may reference self
}
```

### Storage model

Every registered ext-field is assigned a **globally stable slot
index** by `ExtensionFieldStorage.AllocateSlot`. The key
`(targetName, generic-spec, fieldName)` is hashed into a
process-wide `ConcurrentDictionary<string, int>`; first registration
mints, subsequent registrations from any module / re-load collapse
onto the same index. Slot numbers are monotonic — never reclaimed.

Per-instance state lives on two **lazy-allocated** arrays hanging off
the receiver:

| Field                          | Type             | Purpose |
|--------------------------------|------------------|---------|
| `ClassInstanceValue.ExtFieldSlots` / `StructInstanceValue.ExtFieldSlots` | `RuntimeValue?[]?` | the values; `null` until first ext-field write |
| `.ExtFieldInitBits`             | `ulong[]?`       | initialisation bitset (1 bit per slot) — gates default-eval and let/final/const writes |

Read = O(1) direct index. Write = O(1) array store + bit-flip.
Both arrays grow geometrically (next power of two) on first slot
overflow; common case is a couple of ext-fields per instance so the
storage stays cache-line-sized.

An instance with zero ext-fields touched costs **zero extra bytes**:
the lazy arrays stay null. GC reclaims them with the instance, no
side-table sweep needed.

### Default-value evaluation

Defaults are lazy: the `DefaultValueNode` AST is held on the
`ExtensionFieldDescriptor`, evaluated on the **first read** with
`self` bound to the receiver, and the result is stored back into the
slot. Subsequent reads are pure slot loads.

Writes before any read skip the default — the explicit value wins.

### Mutability rules

| Modifier | Reassign after first write | Notes |
|---|---|---|
| `var`   | yes | unrestricted |
| `final` | no  | one explicit write OR one lazy default — whichever happens first |
| `let`   | no  | identical to `final` in this scope; reserved for future move semantics |
| `const` | no  | second write throws; default mandatory |

`final`/`let` consume their write quota the first time the
initialisation bit flips. Re-reading a `final` field after the
default-eval populates the slot is fine; explicitly writing it
afterwards throws.

### Type checks

When a `FieldType` is declared, `TrySetField` calls
`TypeSystem.IsAssignable(context, FieldType, value)` before writing.
Untyped fields accept any `RuntimeValue`.

### Visibility & imports

- `pub` on the field (or on the surrounding `extend`) makes the
  field crossing-module-visible.
- Private fields are accessible only from the declaring module
  (`entry.IsLocal == true`).
- `MergeExtensions` copies the descriptor verbatim — slot indices
  are global, so imported and local entries share the same slot
  storage on the instance.

### Sealed propagation

Sealed targets refuse new ext-field declarations at registration
time, same rule applied to other ext-member kinds.

### NativeAOT compatibility

- No reflection.
- No `DynamicMethod` / `Reflection.Emit`.
- No process-wide instance side maps (storage is per-instance arrays).
- Slot allocation is a simple counter + dictionary — both AOT-friendly.

### Dispatch order (with fields)

```
native property → native event → native field → native method
→ ext-field      → ext-event   → ext-property → ext-method
```

Native always wins. Among extensions, field access is the most
"like a value" form, so it surfaces first; the rest follow the
previous v2.2 ordering.

### Reset semantics

`ExtensionFieldStorage.Reset()` clears the slot map + descriptor
table. Call it whenever the program tears down its global state —
mirrors how `Program.InitializeSymbolTable` already clears
`MetadataRegistry.Global` between menu-driven runs.

### v2.4 additions

#### `static` ext-fields

```ra
extend Reg {
    pub static var count: int = 0;
}
Reg.count = Reg.count + 1;
```

`static` flips storage from per-instance to per-type. The
ClassTypeValue carries `StaticExtFieldSlots` / `StaticExtFieldInitBits`
/ `StaticExtFieldLazyBits` paralleling the per-instance arrays. The
registry's `ResolveFieldEntry` filters by `descriptor.IsStaticField`
against the receiver kind: ClassType receiver → static-only;
instance receiver → instance-only. No mixing.

#### `lazy` ext-fields

```ra
extend Tree {
    pub lazy var derived: int = self.seed * 100;
}
```

Default expression deferred until first read; subsequent reads
hit the cached slot. Explicit write before any read suppresses
default eval. Re-entrant access during the default raises a
dedicated error (`recursive access to lazy extension field …
during its own initialization`), backed by a parallel
`ExtFieldLazyBits` bitset.

#### IC fast path

A new `BR_EXT_FIELD = 17` IC branch caches:

- `FieldIndex` = the global slot index
- `CachedAux`  = the resolved `ExtensionFieldEntry` (for slow-path
                 re-dispatch when init-bit is still unset)
- `Shape`      = the instance's definition / ClassType

Hit path: read `ExtFieldSlots[slot]` after a single init-bit check.
No registry lookup, no dictionary walk, no descriptor probe. Miss
path on uninit slot reuses the cached entry to evaluate the default
without re-resolving. PIC machinery (M42) supports up to 3 shapes
per call site before falling through to megamorphic dispatch.

#### Cross-run reset

`ExtensionFieldStorage.Reset()` is invoked from
`Program.InitializeSymbolTable`. Menu mode `[2]` / `[3]` re-executes
the same `extend` blocks on each tick; without the reset, slot
indices would climb unbounded and per-instance arrays would grow
proportionally. The reset wipes both the slot map and the
descriptor cache.

### Test coverage

`tests_extensions.ra` E22–E35 cover: default value, var write,
string field, list field, class-typed field, const-immutability,
final single-shot, struct receiver, generic specialization,
`self.<member>` in default expression, per-instance independence,
**static field sharing**, **lazy default deferred eval**, **lazy
write-before-read skips default**, **IC hot-loop stability** (50
reads, single ext-field).

## 8. Test coverage

`tests_extensions.ra` (15 cases) covers:

- Methods on class / struct / string / list (E1–E4).
- Method overload by arity (E5).
- Base-class extension visible on derived instances (E6).
- Computed properties (get-only) on class / struct / string (E7–E9).
- Property with custom setter (E10).
- Arrow shorthand (E11).
- Method + property coexistence (E12).
- Native field beats extension property of the same name (E13).
- Property with explicit `{ get { } set { } }` block form (E14).
- Extension property accessed inside extension method body (E15).

Existing regression sets pass: `test_extension.ra` (legacy X1/X2),
`tests_properties.ra` (P1–P14), `tests_lambdas.ra` (L1–L30),
`tests_unions.ra`, `tests_patterns.ra`, `tests_delegates.ra`,
`tests_events.ra`.

# Ra Language — Events Design

Internal design document. Locks the grammar, semantics, AST shape, runtime
representation, and IR/VM integration for **events** in Ra. Reads as a spec,
not a tutorial.

Status: **locked**, in implementation.

Authoring principle: events in Ra are a **first-class type-member kind**, not
syntactic sugar over a "list of callbacks" field. They model the *publish /
subscribe* contract directly — the declaring type owns emission, callers own
subscription, and the visibility split between the two is part of the type
definition.

---

## 1. TL;DR

```ra
class Button {
    pub event Click(x: int, y: int)
    pub cancellable event PreClick(x: int, y: int)
    priv event InternalRepaint()

    pub fn handle_native_click(x: int, y: int): void {
        if (self.PreClick(x, y)) { ret; }   // a subscriber cancelled
        self.Click(x, y);                    // sync emission
    }
}

var b = Button()

// subscribe — returns a Subscription handle (use it to unsubscribe deterministically)
var sub = b.Click.on(fn(x: int, y: int) { print("clicked $x, $y"); })

// modifiers
b.Click.on(fn(x: int, y: int) { print("once"); }, once: true)
b.Click.on(fn(x: int, y: int) { print("priority"); }, priority: 10)
b.Click.on(fn(x: int, y: int) { print("weak"); }, weak: true)

// unsubscribe by handle (works even for lambdas — no closure identity hack)
b.Click.off(sub)

// bookkeeping
print(b.Click.count())   // 2
b.Click.clear()          // remove all
```

The seven flavors above (sync, cancellable, private, instance/static,
once, priority-ordered, weak) all share the **same declaration form**:
`event NAME(PAYLOAD_PARAMS)`. Modifiers express the differences. No
delegate fields, no manual handler-list bookkeeping, no `Action<T>`
wrapper types.

---

## 2. Why events as first-class members

Ra already has `fn` (callable), `prop` (named value-shaped), and
`field` (storage). Events occupy a fourth role:

- **Asymmetric visibility**. The "subscribe" and "raise" capabilities
  are different operations with different audiences. Modelling them
  as a function pair (`add_listener(h)` + `_emit_click(...)`) leaks
  the raise surface; modelling them as a delegate field leaks the
  ability to *replace* the entire handler list. Events make both
  asymmetries syntactically explicit.
- **Contract enforceability**. An interface / trait can declare
  `event X(...)` and an implementer must provide a matching event.
  That's a contract a delegate-field convention can't express.
- **Identity-free unsubscribe**. Lambdas don't have a comparable
  identity, so C#-style `-= lambda` is brittle. Ra returns a
  `Subscription` handle from `on(...)` — `off(sub)` is unambiguous
  even when the handler is an inline lambda.
- **Cancellation is a property of the contract, not the call site**.
  Declaring `cancellable event` baked into the type tells every
  caller that propagation can stop; the engine enforces handler
  return-type and emit short-circuit at one place.

What events are **not**: they are not async streams. Ra already has
`async stream` + `emit value` for backpressure-aware producers; events
are the synchronous, broadcast counterpart. The two coexist.

---

## 3. Grammar

EBNF, embedded inside class / struct / record / interface / trait /
abstract-class / abstract-record-class member lists.

```
EventDecl       := EventModifiers "event" Ident "(" PayloadParams? ")" EventBody? Terminator

EventModifiers  := ( "pub" | "priv" | "static" | "abstract" | "override" | "cancellable" )*

PayloadParams   := PayloadParam ( "," PayloadParam )*
PayloadParam    := Ident ":" Type

EventBody       := "{" EventAccessor ( ( ";" | NEWLINE )+ EventAccessor )* "}"
EventAccessor   := AccessorVisibility ( "subscribe" | "raise" )
AccessorVisibility := ( "pub" | "priv" )

Terminator      := NEWLINE | ";" | end-of-block
```

### 3.1 Form summary

| Form                                                          | Meaning                                                |
| ------------------------------------------------------------- | ------------------------------------------------------ |
| `pub event Click(x: int, y: int)`                             | Public subscribe, private raise (default)              |
| `pub event Click(x: int, y: int) { pub raise; }`              | Public subscribe AND public raise                      |
| `priv event Beat()`                                           | Private subscribe + raise — internal channel           |
| `pub static event AppStarted()`                               | Static instance — subscribed via type                  |
| `pub abstract event Resize(w: int, h: int)`                   | Abstract contract                                      |
| `pub override event Resize(w: int, h: int)`                   | Override of abstract / interface / trait event         |
| `pub cancellable event PreClick(x: int, y: int)`              | Handlers return `bool`; emit short-circuits on `true`  |

### 3.2 Keywords introduced

`event` — new top-level keyword (lexer).

`cancellable`, `subscribe`, `raise` — *contextual* keywords. They are
matched as identifiers at lex time and treated as event-specific
keywords only inside event declarations or accessor bodies. Outside
that context they remain valid identifiers (no migration burden).

### 3.3 Ambiguity notes

- `event Name()` — empty payload list is allowed. Useful for "ping"
  events.
- `cancellable` may appear in any position among the modifiers but
  conventionally goes last (closest to `event`).
- The default raise visibility is **priv**, even when the property's
  overall visibility is `pub`. This mirrors C# (`+=` is public, `?.Invoke`
  is internal) but Ra makes it spell-able with `{ pub raise; }`.

---

## 4. Semantics

### 4.1 Member-name namespace

Event names live in the same namespace as fields, methods, and
properties of the declaring type. A collision with any of them is a
**compile error**. The check runs in the type-definition visitor
after all members have been registered.

### 4.2 Where events may be declared

| Type kind                              | Events allowed | Notes |
| -------------------------------------- | -------------- | ----- |
| `class`                                | yes            | full power |
| `abstract class`                       | yes            | `abstract event` supported |
| `record class`                         | yes            | reference record |
| `abstract record class`                | yes            | abstract events allowed |
| `interface`                            | yes (contract) | declaration only — no body, no raise |
| `trait`                                | yes (contract) | same as interface |
| `struct`                               | **no**         | `IsCopy=true` — subscribers on a copy do not reach the original |
| `record` (value record)                | **no**         | same reason |

Declaring an event in a disallowed type is a parser-level diagnostic
(`event/value-type-disallowed`).

### 4.3 Subscription

`obj.E` evaluates to an `EventSubscriptionValue` carrying
`(instance, descriptor)`. The value exposes a small fixed set of
synthetic methods:

```
obj.E.on(handler[, once: bool=false, priority: int=0, weak: bool=false]): Subscription
obj.E.off(sub_or_handler): bool                  # true if removed
obj.E.clear(): int                               # returns count removed
obj.E.count(): int                               # current subscribers
```

`Subscription` is a small runtime value:

```
struct Subscription {
    pub event_name: string
    pub fn dispose(): void              # equivalent to obj.E.off(self)
    pub fn is_active(): bool
}
```

Subscription handles are **stable across collection iteration order** —
`on(...)` returns the same instance every call, and `off(sub)` removes
by reference identity in O(N).

#### 4.3.1 Handler signature

The handler must be callable with the event's declared payload
parameter types. For non-cancellable events the return value is
ignored. For cancellable events the handler must return `bool`
(non-bool returns are a runtime error at first emission — there is no
static handler-signature checker in v1; the runtime is the
authority).

#### 4.3.2 Modifiers

- **once**: the subscription is removed *before* the handler runs
  (so reentry from inside the handler does not see it). Idempotent
  across multiple emissions.
- **priority**: handlers fire in descending priority order. Ties
  break by subscription order (FIFO).
- **weak**: handler held via `System.WeakReference<BaseFunctionValue>`.
  On each emission, dead refs are pruned. Useful for UI subscriber
  patterns where the subscriber owns its lifetime independently.

### 4.4 Emission

`obj.E(args)` raises the event. The call must satisfy the **raise
visibility** check (see §4.7). The dispatch sequence is:

1. Take a snapshot of the current subscriber list. Mutation of the
   list during the loop (subscribe / unsubscribe from inside a
   handler) is visible only on the *next* emission, never the
   current one. **This is the same rule C# uses for `event +=` and
   is essential for predictable behaviour.**
2. For weak handlers, drop dead refs from the snapshot.
3. Sort the snapshot by descending priority (stable).
4. Run each handler synchronously with the supplied payload args.
5. For non-cancellable events: any handler throw aborts the loop and
   propagates the error to the raiser. The remaining handlers do not
   run.
6. For cancellable events: handlers return `bool`; the first `true`
   short-circuits the loop and emit returns `true`. If no handler
   returns `true`, emit returns `false`.
7. `once` subscriptions are removed from the *live list* (not the
   snapshot) before their handler runs.

Return value of emit:

| Event flavor       | Return type | Value                          |
| ------------------ | ----------- | ------------------------------ |
| non-cancellable    | `null`      | always `null`                  |
| cancellable        | `bool`      | `true` iff some handler cancelled |

### 4.5 Reentrancy

Snapshot semantics make event re-entry safe:

```ra
b.Click.on(fn(x: int, y: int) {
    b.Click(x + 1, y + 1)      // re-enters: new emission, new snapshot
})
```

This terminates on its own (because the inner call uses a fresh
snapshot taken *after* the outer subscription was already counted),
unless the user explicitly builds a fixpoint. No depth limit is
imposed by the language; the .NET stack catches runaway recursion.

### 4.6 Error handling in handlers

- Default (non-cancellable): first handler error aborts emission and
  surfaces. Subsequent handlers do not fire. This is the predictable
  default that matches C# behaviour.
- The handler stack trace is preserved across the raise site.

Future work: a `tolerant` event-level modifier that collects errors
into a list and continues. Not in v1 to keep the semantics small.

### 4.7 Visibility model

Two visibilities per event:

- **subscribe visibility**: who can call `obj.E.on(...)` / `off(...)` /
  `clear()` / `count()`. Defaults to the event's overall visibility
  (`pub event` → public subscribe).
- **raise visibility**: who can call `obj.E(args)`. Defaults to **priv**
  regardless of the event's overall visibility. This is the C# rule
  recast in syntax.

The accessor block can override:

```ra
pub event Click(x: int, y: int) { pub raise; }       // expose raise externally
pub event Click(x: int, y: int) { priv subscribe; }  // hide subscribe externally
```

Inside the declaring type (and any subclass body), both visibilities
are reachable regardless of the modifier — matching the visibility
model used for fields / methods / properties.

### 4.8 Override

- `override event` requires a matching abstract / interface / trait
  `event` with the **same name, same payload arity, same payload
  types pairwise, and same `cancellable` flag**.
- An override cannot narrow visibility or change the `cancellable`
  flag.
- An override can add an accessor block to expose raise that was
  default-private.
- Visibility cannot be narrowed.

### 4.9 Abstract

- `abstract event` lives in `class` and `abstract record class`. The
  declaring type cannot raise it (there is no underlying subscriber
  store) and `obj.E(...)` on the abstract base raises a runtime
  error.
- Interfaces and traits use the *requirement* form, which is
  syntactically identical (`event X(...)` declaration in the
  interface/trait body) but semantically a contract, not storage.

### 4.10 Static

- Static events live on the type definition value (`ClassTypeValue`,
  `RecordTypeValue`, etc.). Subscriber list is stored on the type,
  not on instances.
- Subscribed via `MyClass.Event.on(handler)`. Raised via
  `MyClass.Event(args)` from inside a static method (or
  `self.Event(args)` from an instance method on the declaring type
  — both forms route to the same per-type subscriber list).
- Static events cannot be abstract (there is no instance-level
  override site).
- Static events on classes participate in inheritance: derived
  classes share their base's static event subscriber list (one list
  per type definition; do **not** duplicate on each derived class).

### 4.11 Generics

An event's payload may reference the declaring type's type
parameters:

```ra
class Channel<T> {
    pub event Message(payload: T)
}
```

No new machinery: the existing field-type plumbing already resolves
type parameters through the instance's `GenericBindings`. Handler
signature is checked structurally at emission time (Ra does not
yet enforce static handler-type checks at subscription).

### 4.12 Pattern matching, deconstruction, serialization

- Events are **not** part of pattern matches (they have no value).
- Events do **not** participate in `to_string`, `equals`, `hash`,
  or `deconstruct`. They are pure behaviour and have no observable
  identity that survives equality.
- `@derive(...)` does not touch events.

### 4.13 Borrow checker

`obj.E.on(handler)` borrows `obj` immutably for the duration of the
call. Subscribers themselves capture references per the standard
closure-capture rules; no special handling.

`obj.E(args)` likewise borrows `obj` immutably during the emit. If
a handler tries to mutate `obj` through a borrowed reference, the
borrow checker catches it as it would for any other access.

---

## 5. AST shape

Two new node kinds:

```csharp
enum AstNodeType { ..., EventDefinition, EventAccessor }
```

```csharp
public sealed class EventDefinitionNode : AstNode {
    public Token NameTok;
    public List<EventPayloadParam> PayloadParams;
    public bool IsPublic;
    public bool IsStatic;
    public bool IsAbstract;
    public bool IsOverride;
    public bool IsCancellable;
    public List<EventAccessorNode> Accessors;     // visibility splits
}

public sealed class EventPayloadParam {
    public Token NameTok;
    public TypeDescriptor? Type;
}

public sealed class EventAccessorNode : AstNode {
    public Token KindTok;                  // subscribe / raise
    public EventAccessorKind Kind;
    public EventAccessorVisibility Visibility;
}

public enum EventAccessorKind { Subscribe, Raise }
public enum EventAccessorVisibility { Default, Public, Private }
```

Both are registered in `Interpreter.RegisterVisitors`. The accessor
node is never directly dispatched at top level — its visitor is a
no-op stub for the sake of the registration contract.

### 5.1 Container nodes updated

`ClassDefinitionNode`, `StructDefinitionNode`, `RecordDefinitionNode`,
`InterfaceDefinitionNode`, `TraitDefinitionNode` gain
`List<EventDefinitionNode> Events`. The lists are append-only at
parse time and read-only thereafter. They live next to existing
`Fields` / `Methods` / `Properties` lists.

`StructDefinition` and `RecordDefinition` reject non-empty `Events`
at type-build time (no value-type events).

---

## 6. Runtime model

### 6.1 EventDescriptor

```csharp
public sealed class EventDescriptor {
    public string Name { get; }
    public List<EventPayloadParam> Parameters { get; }
    public bool IsPublic { get; }
    public bool IsStatic { get; }
    public bool IsAbstract { get; }
    public bool IsOverride { get; }
    public bool IsCancellable { get; }
    public bool RaiseIsPublic { get; }        // resolved from accessors
    public bool SubscribeIsPublic { get; }    // resolved from accessors
    public string DeclaringTypeName { get; }
    public EventDefinitionNode SourceNode { get; }
}
```

Per declaring-type value (`ClassTypeValue`, `StructTypeValue`,
`RecordTypeValue`, `InterfaceTypeValue`, `TraitTypeValue`) gain:

```csharp
public List<EventDescriptor> Events { get; }
public Dictionary<string, EventDescriptor> EventByName { get; }
public EventDescriptor? GetEvent(string name);     // walks bases
```

For **static** events, the type itself owns the subscriber list:

```csharp
public class ClassTypeValue {
    public Dictionary<string, EventSubscriberList>? StaticEventSubs;   // lazy
}
```

### 6.2 Per-instance subscriber storage

```csharp
public class ClassInstanceValue {
    public Dictionary<string, EventSubscriberList>? EventSubs;  // lazy
}
// same on StructInstanceValue (only ref-record uses it; structs are
// rejected at definition time)
```

`EventSubscriberList`:

```csharp
public sealed class EventSubscriberList {
    public List<EventSubscription> Items;
    public long NextToken;
}

public sealed class EventSubscription {
    public long Token;
    public BaseFunctionValue? StrongHandler;
    public WeakReference<BaseFunctionValue>? WeakHandler;
    public bool Once;
    public int Priority;
    public bool Disposed;     // set by Subscription.dispose() to skip on next snapshot
}
```

### 6.3 EventSubscriptionValue

Runtime value returned by `obj.E` member access. Carries an
instance-or-type pointer and the descriptor, plus a cached reference
to the synthetic method group so dispatch can short-circuit the
descriptor lookup on hot paths.

```csharp
public sealed class EventSubscriptionValue : RuntimeValue {
    public RuntimeValue Owner;            // instance OR type value
    public EventDescriptor Descriptor;
    public bool IsStatic;

    public override bool IsCopy => false;  // alias semantics
    public override RuntimeValue Aliased() => this;
}
```

Method dispatch on `EventSubscriptionValue.X.on(...)` is routed by
the existing `BoundExtensionMethodGroupValue`-style path. The four
methods (`on`, `off`, `clear`, `count`) are synthetic and live in
`EventAccessOps`. They are NOT registered as extension methods —
they are first-class so they take precedence over any user-defined
extension method named `on` on an `EventSubscriptionValue`.

`EventSubscriptionValue` is **callable** — its `Execute(args)`
performs the raise. The visibility check on raise is enforced
inside `Execute` against the call site (the resolver passes the
caller context via the FunctionCallExecutor pipeline).

### 6.4 Subscription value

```csharp
public sealed class SubscriptionValue : RuntimeValue {
    public EventSubscriptionValue Source;
    public long Token;
    public override bool IsCopy => false;
}
```

Method `Subscription.dispose()` → `Source.off_by_token(Token)`.
Method `Subscription.is_active()` → returns `!Disposed && in list`.

### 6.5 Initialization

When a class instance is constructed, the `EventSubs` dictionary stays
`null` until the first subscribe. There is no per-event slot
allocation at construction time — events are not stored data; they
are subscription lists that only exist if someone subscribes.

For static events, the subscriber list lives on the
`ClassTypeValue` once the first `MyClass.Event.on(...)` call happens.

---

## 7. Pipeline integration

### 7.1 Lexer

Add `event` to the keyword table (one entry). `cancellable`, `subscribe`,
`raise` remain identifiers at lex time — they are matched contextually
by the parser inside event productions.

### 7.2 Parser

`ParseClassDefinition`, `ParseStructDefinition`, `ParseRecordDefinition`,
`ParseInterfaceDefinition`, `ParseTraitDefinition` gain an `event`
branch in their member loop. After the existing modifier scan (`pub
override abstract static`) the parser checks for the contextual
`cancellable` keyword (optional) followed by `event` and dispatches
to `ParseEventDeclaration`.

`ParseEventDeclaration` returns an `EventDefinitionNode`. The
accessor loop allows accessors in any order, separated by `;` or
newlines, until `}`. Reject:

- non-empty body on `abstract event`
- `subscribe` / `raise` accessors with bodies (only visibility prefix
  allowed)
- duplicate accessor of the same kind
- event in `struct` body or value `record` body
- empty event name
- malformed payload parameter (missing type after `:`)

A new file `Parser/Parser.Events.cs` holds `ParseEventDeclaration` and
`ParseEventAccessorList`.

### 7.3 Resolver

`Resolver.WalkClass` / `WalkStruct` / `WalkRecord` / `WalkInterface` /
`WalkTrait` gain a pass that ignores event declarations for name
binding (events do not introduce locals into the type body).

### 7.4 Interpreter / Visitors

New visitor: `EventDefinitionNodeVisitor` is called by the
class/struct/record/interface/trait visitor during type build; it
constructs the `EventDescriptor` and adds it to the type's `Events`
list. It does not allocate per-instance state — that happens lazily
on first subscribe.

`EventAccessorNodeVisitor` is registered to keep the
`RegisterVisitors` map dense; calling it directly is a runtime error.

`MemberAccessHelper.ApplyAndPrime`: insert an event-descriptor probe
*after* the property probe (since properties and events can't share
a name, the order does not matter for correctness; we go properties
→ events → fields → methods for predictability). On hit, return a
fresh `EventSubscriptionValue`. The IC does not currently cache event
hits (event-subscription-value allocation per access; acceptable for
v1, optimisable later by pooling).

`MemberAssignmentHelper.Apply`: detect event member by descriptor
lookup and reject plain `=`. Compound `+=`/`-=` are not supported in
v1 — the parser will emit a `MemberAssignmentNode` with `=`-form
(compound member assignment is desugared at parse time anyway), so
the runtime path stays simple.

### 7.5 IR / VM

No new opcodes in v1. The event-definition node is routed through
`OP_NATIVE_DEFINE` exactly like properties, classes, structs, etc.
Read paths use the existing `OP_GET_MEMBER`. Call paths use the
existing `OP_CALL` against the callable `EventSubscriptionValue`.

A later milestone may add `OP_GET_EVENT` + `OP_EVENT_EMIT` to bake
the descriptor lookup into the bytecode stream; defer until profiling
shows it.

### 7.6 Annotations

`MetadataTarget.BuildKey` gains a new target kind: `Event`. Key
shape `event:TypeName.eventName` (and
`staticevent:TypeName.eventName` for statics). Future work would
make `@deprecated` / `@validator` / `@intercept` apply to events.

### 7.7 BorrowChecker

Event read = immutable borrow of receiver (same as method access).
Event raise = immutable borrow of receiver (handlers cannot freeze
their own subscriber list at the C# level — they may modify the
*next* snapshot, never the current). No new rules.

---

## 8. Diagnostics

| Code (informal)              | Trigger                                                                      |
| ---------------------------- | ---------------------------------------------------------------------------- |
| `event/name-collision`       | event with same name as field / method / property on same type               |
| `event/value-type`           | event declared in `struct` or value `record` body                            |
| `event/raise-private`        | raise from outside declaring type and without `{ pub raise; }`               |
| `event/subscribe-private`    | subscribe from outside declaring type and accessor block is `{ priv subscribe; }` |
| `event/abstract-raise`       | raise on abstract event (no concrete override in scope)                      |
| `event/abstract-not-impl`    | concrete subclass missing override for abstract event                        |
| `event/override-mismatch`    | override mismatches base on name / arity / payload type / cancellable flag   |
| `event/handler-arity`        | subscribed handler accepts wrong number of args                              |
| `event/handler-must-return-bool` | cancellable event handler returned non-bool                              |
| `event/off-unknown`          | `off(sub)` called with handle not in the list                                |
| `event/once-and-weak`        | not an error — combination is allowed                                        |
| `event/interface-not-impl`   | interface event not satisfied by implementer                                 |

---

## 9. Comparison to other languages

| Concern                   | C#                                       | Dart streams                       | Ra                                              |
| ------------------------- | ---------------------------------------- | ---------------------------------- | ----------------------------------------------- |
| Declare                   | `public event Action<T> X;`              | `Stream<T> get x => ...;`          | `pub event X(payload: T)`                       |
| Subscribe                 | `obj.X += h;`                            | `obj.x.listen(h)`                  | `obj.X.on(h)` (returns Subscription)            |
| Unsubscribe lambda        | broken — can't recover identity          | `sub.cancel()`                     | `obj.X.off(sub)` (handle)                       |
| Raise from outside        | `obj.X?.Invoke(...)` (must be public via property) | controller pattern         | `{ pub raise; }` modifier                       |
| Once                      | manual self-unsubscribe inside handler   | `take(1)`                          | `on(h, once: true)`                             |
| Priority                  | manual `Delegate.GetInvocationList()` reorder | n/a                          | `on(h, priority: 10)`                           |
| Weak                      | `WeakEventManager` (verbose)             | n/a (Streams hold strong refs)     | `on(h, weak: true)`                             |
| Cancellable               | bespoke `Handled` flag in EventArgs      | n/a (streams don't compose like that) | `cancellable event X(...)` + bool handler     |
| Reentrancy safety         | snapshot semantics implicit              | broadcast streams snapshot         | snapshot semantics on every emit                |
| Abstract / interface req  | yes (`abstract event`)                   | abstract `Stream` getter           | `abstract event`, `event` in interface/trait    |
| Static                    | yes                                      | static `StreamController` field    | `static event`                                  |
| Async                     | n/a directly (handlers may be `async`)   | the model                          | future work (sync in v1)                        |

Ra's design takes:

- **identity-free unsubscribe** from observer-pattern best practice
  (handles, not handler equality)
- **two-axis visibility** as a first-class concept (subscribe vs
  raise), spelled out where C# leaves it implicit
- **`cancellable` declared on the type** instead of bolted on via
  ad-hoc `EventArgs.Cancel` fields
- **`once` / `priority` / `weak` as subscription options**, not
  separate decorator types
- **snapshot semantics** as a hard guarantee, documented and tested,
  so user code can subscribe/unsubscribe from inside handlers without
  hidden risk

Key differentiators vs C# specifically:

- Ra unifies subscription handles. C# leaves user code to invent
  them.
- Ra makes raise-visibility explicit and authorable. C# hides it
  behind the `event` keyword's compiler-magic semantics.
- Ra's cancellable events have a typed return contract; C#'s
  cancellation is a convention.

---

## 10. v1.1 — extensions (implemented)

Six originally-deferred features are now part of the v1.1 contract.

### 10.1 Async events

`async event` modifier. Emit returns a `task`; users `await` it.
Handlers may return a `TaskValue`; emit awaits sequentially. The
task is constructed via a TaskValue-owned `RaTaskCore` to avoid the
auto-recycle race that would otherwise reset the status to Pending.

### 10.2 Tolerant emission

`tolerant event` modifier. Per-handler errors are caught, collected
into a `list[string]`, and emit continues. Returns the list (or
`tuple(bool, list[string])` when combined with `cancellable`).

### 10.3 First-class event refs

`Type.Event` for an INSTANCE event yields an `EventRefValue` instead
of erroring. Calling `ref(instance)` returns the bound
`EventSubscriptionValue`. Static events keep their v1 surface.

### 10.4 Annotation hooks

`AnnotationTargetKind.Event` / `.StaticEvent` added. Metadata keys
`event:Type.name` / `static_event:Type.name`. `RaiseDirect` calls
`AnnotationInterceptors.RunBefore` / `RunAfter`. `@deprecated` emits
a stderr warning on every raise, with an optional `reason`.

### 10.5 Strict payload-type equality

`EventDescriptor.SignatureMatches` compares name + arity +
cancellable + payload types pairwise. Enforced in
`SatisfiesTrait`, `ImplementsInterface`, and the override-check
inside `ClassDefinitionNodeVisitor`.

### 10.6 IR opcodes + IC tags

`Opcode.GetEvent` (0x93), `Opcode.EmitEvent` (0x94) reserved.
MemberAccess IC adds `BR_EVENT_INSTANCE` / `BR_EVENT_STATIC` /
`BR_EVENT_REF` tags so cache hits skip the GetEvent dictionary walk.

---

## 11. Out of scope (future work)

- **Handler-side filtering** — `on(h, where: pred)` predicate.
- **Concurrent async dispatch** — current async emit is sequential.
- **OP_GET_EVENT / OP_EMIT IR-gen emission** — opcode slots reserved;
  emission requires PGO-driven specialisation (Ra has no static
  event-type detection).
- **Generic payload-type resolution** — `SignatureMatches` compares
  the syntactic `TypeDescriptor.Name`; resolving type parameters
  through `GenericBindings` would tighten the check for generic
  records.

---

## 11. Implementation milestones (this PR)

P1 — lexer keyword `event`.
P2 — AST nodes + AstNodeType enum extension + visitor registration stubs.
P3 — parser: event declaration grammar inside class/record class/interface/trait member lists; rejection in struct/value-record.
P4 — runtime: `EventDescriptor`, `EventSubscriberList`, `EventSubscription`, type-value storage (`ClassTypeValue.Events` etc.).
P5 — visitor: `EventDefinitionNodeVisitor` registers descriptors into the type-value during class/record class/interface/trait build.
P6 — read path: `MemberAccessHelper.Apply` (+ ApplyAndPrime) event probe returning `EventSubscriptionValue`.
P7 — `EventSubscriptionValue` runtime — `Execute` (raise), synthetic methods (`on`, `off`, `clear`, `count`), bound to `BoundEventMethodValue`.
P8 — `SubscriptionValue` runtime — `dispose()`, `is_active()`.
P9 — abstract & override enforcement (extended `GetUnresolvedAbstractRequirements`).
P10 — interface / trait satisfaction (extended `ImplementsInterface` / `SatisfiesTrait`).
P11 — tests: `tests_events.ra` covering every form and every diagnostic.
P12 — corpus parity sweep.
P13 — docs (this file).

Each milestone is a single self-contained step. Build green after every
milestone.

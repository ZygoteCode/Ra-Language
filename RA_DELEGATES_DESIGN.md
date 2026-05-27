# Ra Delegates — Design (v1)

Status: shipping in this milestone. Ships alongside Records, Events, Properties, Shifts.

## Mission

Ra Delegates are not "yet another callable wrapper." They are the **single
first-class function citizen** of the language. Every function, method,
lambda, bound method group, built-in, and partially-applied closure shares
**one** runtime spine (`BaseFunctionValue`) and **one** structural type spine
(`fn(T1, T2) -> R`).

The C# / Java / Dart pain we explicitly avoid:

* "Delegate type explosion" (`Func<>` vs `Action<>` vs custom named delegate
  vs functional interface) — Ra has exactly one form, `fn(...) -> R`. Named
  aliases via `delegate` are pure ergonomics and unify under that form.
* Wrapping adapters between `Func<int,int>` and `Predicate<int>` — Ra
  structural-equivalence makes them the same type.
* Re-allocating a multicast wrapper on every `+=` — Ra fuses singleton fast
  paths so a one-handler "delegate" is just a `BaseFunctionValue`.
* Magic implicit method-group conversions that surprise the programmer —
  Ra surfaces a real value (`obj.method` is a `BoundClassMethodGroupValue`)
  before any conversion happens.

## Syntax

### Function-type literal — works in any type position

```ra
let on_click: fn(int, int) -> bool = ...

fn invoke(action: fn() -> void) -> void { ret action() }

class Worker {
    var handler: fn(string) -> bool
}
```

### `void` is sugar

`fn(T) -> void` and `fn(T)` (no return arrow) both desugar to the **null-return
delegate shape**. Calling them just returns `null` if the body has no
`return`, exactly as today's functions do.

### Named delegate alias

```ra
delegate Predicate<T> = fn(T) -> bool;
delegate Action = fn();
delegate Reducer<T, U> = fn(U, T) -> U;
```

A `delegate` declaration registers `Predicate` in the symbol table as a
**type alias**. `Predicate<int>` and `fn(int) -> bool` unify at the type
system layer — they are not nominally distinct.

### Method references

The existing member-access pipeline already returns `BoundClassMethodGroupValue`
for `obj.method`. Ra delegates lean on that: no new `::` syntax (which is
already reserved for `as` casts in Ra).

```ra
let f: fn(int) -> int = obj.process
let g: fn(int) -> int = MyClass.static_helper   // static method
```

### Lambdas + inference

Lambdas already exist (`fn(x: int) -> int { ret x * 2 }` and arrow form
`fn(x) => x + 1`). With delegates they pick up:

* Parameter-type inference from the target delegate context — write
  `let pred: fn(int) -> bool = fn(x) => x > 0` and `x` infers to `int`.
* The same closure / capture / move rules already in force.

### Multicast — `+` / `-` on delegate values

```ra
var bus: fn(string) = log_to_console
bus = bus + log_to_file
bus = bus + audit_emit
bus("hello")                 // fires all three in declaration order
bus = bus - log_to_file
```

* `+` of two callables produces a `MulticastDelegateValue` containing both,
  preserving order.
* `+` collapses adjacent multicasts so chaining stays flat.
* `-` removes the **last** occurrence of the right operand (compared by
  identity, then by name) and returns either the resulting multicast, the
  surviving singleton, or `null` if every handler is gone.
* The return value of a multicast call is the return value of the **last**
  handler — matches user intuition for "the final word wins."

### Partial application

```ra
let add5 = partial(add, 5)
add5(7)                        // 12
let say_hi = partial(greet, _, "hi")  // _ is a placeholder identifier sugar
say_hi("Alice")                // greet("Alice", "hi")
```

`partial` is a built-in. The `_` placeholder is a special identifier in
argument positions: it leaves a hole. v1 ships the no-placeholder form
(positional binding from the left); the placeholder form is reserved.

### Composition

```ra
let h = compose(f, g)          // h(x) = g(f(x))
let h2 = f |> g                // pipeline operator already exists
```

`compose` builds a `ComposedFunctionValue` that calls `f` on its input then
threads the result into `g`. Pipeline `|>` keeps working for the
mid-expression case.

## Type semantics

### Structural equivalence

Two function types `fn(P1, …, Pn) -> R` and `fn(Q1, …, Qm) -> S` are
**assignable** when:

1. `n == m` (arity match), and
2. each `Q_i` is assignable to `P_i` (parameters are **contravariant**), and
3. `R` is assignable to `S` (return is **covariant**).

`any` matches both directions. A formal parameter typed `fn(int) -> int`
accepts any callable whose declared parameters are looser (e.g. `fn(number) -> int`)
and whose return is tighter — Liskov-safe.

### Assignability from non-`fn` callables

When the target is a structural `fn` type and the value is a
`BaseFunctionValue`:

* If the value has a declared signature (FunctionValue, BoundClassMethodValue
  with non-null types), the variance rules above run on declared signatures.
* If the value is built-in / arity-only (e.g. `BuiltInFunctionValue`), the
  arity check passes; the type check defers to runtime.
* Multicast / Partial / Composed values carry their effective signature so
  they compose without ad-hoc wrapping.

### Diagnostic quality

When a structural conversion fails, the error names both signatures and
flags the offending parameter / return slot, e.g.:

```
type mismatch assigning callable to 'fn(int) -> bool'
  → got 'fn(string) -> bool'
  → parameter #1 differs: expected 'int', value declares 'string'
```

## Runtime model

Class hierarchy (all `BaseFunctionValue`):

```
BaseFunctionValue                ← existing
├── FunctionValue                ← existing (user fns, lambdas)
├── BuiltInFunctionValue         ← existing
├── BoundClassMethodValue        ← existing (instance method ref)
├── BoundClassMethodGroupValue   ← existing (overload set, member access)
├── BoundExtensionMethodGroupValue ← existing
├── EventSubscriptionValue       ← existing (events are callable)
├── EnumVariantConstructor       ← existing
├── MulticastDelegateValue       ← NEW
├── PartialFunctionValue         ← NEW
└── ComposedFunctionValue        ← NEW
```

* **MulticastDelegateValue** — owns a flat `List<BaseFunctionValue>`. Single
  Execute path: iterate, propagate errors fast, keep the last return value.
* **PartialFunctionValue** — owns the target `BaseFunctionValue` plus two
  arrays: prefix positional args and a named-arg dictionary. Execute folds
  the user's call-site args on top and dispatches to the target.
* **ComposedFunctionValue** — owns `Inner` (called first) and `Outer`
  (called with `Inner`'s result). Stateless beyond those two refs.

All three are **IsCopy = false** (handles, not values).

### Operators on BaseFunctionValue

`AddedTo` / `SubbedBy` are overridden once on `BaseFunctionValue` to produce
`MulticastDelegateValue`s. Single-element subtraction returns the lone
handler or `NullValue.Null`. Adjacent multicasts collapse:
`(a+b) + (c+d)` = a flat `[a,b,c,d]`, never nested.

### Composition with existing CALL infrastructure

Delegate values are still `BaseFunctionValue`s — the existing `OP_CALL`,
`OP_TAIL_CALL`, `OP_CALL_METHOD`, and `FunctionCallExecutor.Invoke` path
runs unchanged. No new opcodes ship in v1. This is the central win: any
optimisation already in CALL (IC priming, overload PIC, frame pooling)
applies to delegates for free.

### Performance contract

* **Singleton hot path**: assigning a `FunctionValue` directly to a
  `fn(...)`-typed variable is a no-op at runtime — same value, same Execute
  call. Zero wrapper allocation.
* **Multicast small-N**: backed by a `List<BaseFunctionValue>` (typical
  Capacity 2–4). Per-call cost is `O(N)` Execute dispatches, no per-call
  allocation.
* **Partial / Composed**: one allocation at construction; per-call cost is
  one extra Execute hop. The current CALL IC primes on the inner target.
* **Type check**: `IsAssignable` on a structural fn type is `O(arity)`
  string compares. Caching is left to the existing Resolver — no new cache
  surface.

## NativeAOT considerations

* No reflection on delegate signatures at runtime. All structural checks
  walk `TypeDescriptor` instances, which are POCOs.
* No `Delegate.DynamicInvoke`. Every callable is a `BaseFunctionValue` and
  goes through the same `Execute` method that user functions use.
* No `IL.Emit` / `DynamicMethod`. We reuse the IR/VM that already ships AOT.

## Out of scope for v1 (explicitly deferred)

* **`_` placeholder partial application** — parser plumbing for `fn(_, x)`
  syntax. Reserved.
* **Variance on generic delegate aliases** (e.g. declaration-site
  `in`/`out`). Use-site variance via structural rules already covers most
  cases.
* **Async multicast** — multicast of `async fn` values fires sequentially.
  A parallel mode is a follow-up.
* **Curry built-in** — `compose` + `partial` cover the same ground in v1.

## Migration / compatibility

* The existing `function` nominal type continues to work; it is now an
  alias for `fn(...) -> any` (any callable). Code that declared `: function`
  keeps compiling.
* No bytecode changes — opcode table is untouched.
* Existing tests for events, properties, async, records continue passing —
  delegate machinery is additive.

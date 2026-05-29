# Ra Constructors — Generative, Named & Factory (Design)

Status: implemented (v1). Smoke tests: `tests_constructors.ra` (C1–C20, hard-asserted).
Microbench: `bench_constructors.ra`.

This document specifies Ra's object-construction model: **generative**, **named**, and
**factory** constructors, plus **private** constructors and **redirection**. It is the
single source of truth for the syntax, the precise semantics, the cross-layer
implementation, the edge cases, and the test matrix.

---

## 1. Motivation & principles

Ra already had one shape of constructor — the *generative* constructor declared with the
type name (`Point(x, y) { ... }`) and invoked positionally as `Point(3, 4)`. It allocates
`self`, runs after field initialisation, cannot return a value, and can chain to a base
class via `super(...)`. Records synthesise their own primary constructor.

What was missing, and what every top-tier modern language offers, is a way to:

- give *distinct construction intents* their own readable name (`Color.rgb`, `User.fromJson`,
  `Duration.zero`) — **named constructors**;
- separate *deciding how to build* from *building* — caching, pooling, singletons, subtype
  dispatch, validation-before-allocation — **factory constructors**;
- *hide* the raw allocator and force callers through a safe public API — **private
  constructors**;
- avoid duplicating parameter lists and defaults across constructors — **redirection**
  (handled by forwarding factories, see §3.4).

Design priorities, in order: **semantic coherence with the rest of Ra** › ergonomics for the
author and reader › optimisability on IR + VM › minimal surface area. The feature reuses
Ra's existing dispatch wholesale — there are **zero new opcodes** and **zero new AST node
kinds**; everything rides the call + member-access machinery that already exists.

---

## 2. Syntax

All forms live in the class body alongside fields, methods, properties and operators.

```ra
class Point {
    pub final x: int
    pub final y: int

    // (1) Generative, unnamed — the canonical allocator.
    pub Point(x: int, y: int) {
        self.x = x
        self.y = y
    }

    // (2) Generative, NAMED — same semantics, distinct intent. `self` is bound,
    //     field defaults already ran, no `ret`.
    pub Point.origin() {
        self.x = 0
        self.y = 0
    }
    pub Point.diagonal(v: int) {
        self.x = v
        self.y = v
    }
}

Point(3, 4)        // unnamed generative
Point.origin()     // named generative   →  (0, 0)
Point.diagonal(5)  // named generative   →  (5, 5)
```

Factory constructors are introduced by the `factory` keyword. They have **no `self`**, run
**no automatic field initialisation**, and **must `ret`urn** a value assignable to the
enclosing type (a subtype is allowed). They may be unnamed (`Logger()`) or named
(`Color.rgb(...)`).

```ra
class Logger {
    static var _made = false
    static var _instance = null

    Logger.make() { }                  // private named generative allocator (no `pub`)

    pub factory Logger() {             // unnamed factory — singleton reuse / caching
        if Logger._made == false {
            Logger._instance = Logger.make()
            Logger._made = true
        }
        ret Logger._instance
    }
}

Logger()   // always the same instance
```

```ra
class Color {
    pub final r: int
    pub final g: int
    pub final b: int

    Color.raw(r: int, g: int, b: int) {            // private named allocator
        self.r = r ; self.g = g ; self.b = b
    }

    pub factory Color.rgb(r: int, g: int, b: int) {     // public validated factory
        if r < 0 || r > 255 { throw "r out of range" }
        ret Color.raw(r, g, b)                          // implicit return type is `Color`
    }

    // factory returning a SUBTYPE, with an explicit nullable return for
    // expected-failure parsing. Arrow body = forwarding factory.
    pub factory Color.parse(s: string) -> Color? => parse_or_null(s)
}

Color.rgb(255, 128, 0)
Color.parse("#fff")     // Color?  — may be null
```

### 2.1 Grammar summary

```
classMember        := … | constructorDecl | factoryDecl
constructorDecl    := [pub] TypeName [ '.' Ident ] paramList ctorBody
factoryDecl        := [pub] 'factory' TypeName [ '.' Ident ] paramList [ '->' type ] fnBody
ctorBody           := block | ε                                // generative ctors may omit a body
fnBody             := block | '=>' expr                        // factory bodies, like fn bodies
```

- The leading `TypeName` must equal the enclosing class name (this is how the parser already
  recognises a constructor; a named constructor simply adds `. Ident`).
- `factory` is a new reserved keyword (see §7 — no collisions in the codebase or test suite).
- A generative constructor cannot declare a return type (`: T`) nor use an arrow body
  (`=> expr`) — both are rejected by the parser with a help message, since a generative
  constructor always produces an instance of its own class and never returns.

### 2.2 Why this syntax (alternatives considered)

**Named constructors — chosen: `Type.name(...)` dotted form.** The decisive reason is
*coherence*: Ra already dispatches `Type.member(args)` end-to-end through the VM
(`OP_GET_MEMBER` → resolve on the `ClassTypeValue` → `OP_CALL`). A named constructor reads
and resolves *exactly* like the static-method call it is adjacent to, it is discoverable by
typing `Type.` in an IDE, and it needs no new call syntax. The rejected alternative —
annotation-style `@named fn origin()` returning `Self` — is more boilerplate, hides intent
behind a method, and does not read as construction at the call site.

**Factory — chosen: `factory` keyword prefix.** Ra introduces every member kind with a
keyword (`prop`, `event`, `record`, `delegate`, `operator`). A `factory` keyword is
consistent, greppable, and gives the cleanest diagnostics ("`factory` constructor must
return…"). The rejected alternative — an `@factory` annotation on a static method — was
weaker on all three counts and blurred the line between "method that happens to return Self"
and "constructor". A keyword makes factory constructors a first-class, named concept.

**Redirection — chosen: forwarding factories, no new syntax.** A dedicated generative
redirect initializer (`Point.unit() : self(1, 1)`) was considered and rejected: the `:`
already introduces a return-type annotation, so the initializer form collides with existing
grammar and would force a peek-disambiguation plus an empty-body special case — real
complexity for marginal gain. A forwarding factory expresses the same single-source-of-truth
intent with zero new syntax: `pub factory Point.unit() => Point(1, 1)` (arrow) or a block
factory that `ret`urns `Point(...)`. This reuses the unnamed generative constructor as the
one place defaults/validation live.

---

## 3. Semantics

### 3.1 The construction matrix

| Form | Keyword | `self` bound? | Auto field-init? | `ret`? | Invoked as |
|------|---------|:-:|:-:|:-:|------------|
| Generative, unnamed | — | yes | yes | forbidden | `T(args)` |
| Generative, named | — | yes | yes | forbidden | `T.name(args)` |
| Factory, unnamed | `factory` | no | no | **required** | `T(args)` |
| Factory, named | `factory` | no | no | **required** | `T.name(args)` |

### 3.2 Generative constructors (named or unnamed)

Identical lifecycle to today's constructor, with a name attached for the named case:

1. Allocate a bare `ClassInstanceValue` (field-shape slots sized from the hidden class).
2. **Field-initialisation chain** runs base→derived, evaluating field/property defaults in
   declaration order. `self` is *not* visible here.
3. The matching constructor body runs with `self` bound, `Context.IsInConstructor = true`
   (so `final`/`let` fields are writable here and only here) and
   `Context.CurrentClassMethodOwner` set to the declaring class.
4. `super(args)` may be called inside the body to run a base constructor against the same
   instance. A `ret value` is rejected ("Constructors cannot return a value").
5. The fully-initialised instance is the result.

Named generative constructors are resolved by **name then signature**; unnamed ones by
signature only.

### 3.3 Factory constructors

A factory is a class-level creation function:

1. **No** instance is pre-allocated; **no** field-init chain runs.
2. The body runs like a static method — no `self`, `Context.CurrentClassMethodOwner` set to
   the declaring class (so the factory can reach the class's own *private* constructors).
3. The body **must** `ret`urn. The returned value must be assignable to the **effective
   return type**:
   - implicit (no `->`): the enclosing type `T` (with its bound generic args), non-null,
     subtypes allowed;
   - explicit `-> U`: any `U` the author writes (`T`, a subtype, `T?`, `Result<T,E>`, …),
     checked with the standard `TypeSystem.IsAssignable`.
4. Falling off the end without returning, or returning an incompatible value, is a runtime
   diagnostic (RA0414).

Factories typically delegate to a private generative constructor (`Color.raw(...)`) to do
the actual allocation — the "private allocator + public factory" idiom.

### 3.4 Redirection

Redirection — keeping defaults/validation in one place — is expressed by a **forwarding
factory** rather than a dedicated initializer:

```ra
pub factory Point.unit()  => Point(1, 1)     // arrow forward
pub factory Point.zero()  { ret Point(0, 0) } // block forward
```

The factory forwards to the unnamed generative constructor (the single source of truth),
so the named convenience constructors carry no duplicated field logic.

### 3.5 Private constructors & access control

Visibility is the rule that powers the private-allocator idiom while preserving backward
compatibility:

- The **unnamed** constructor `T(...)` is the public construction entry and is always
  callable. (This also means every pre-existing constructor — all of which are unnamed —
  keeps working unchanged.)
- A **named** constructor `T.name(...)` follows the standard member rule: **private unless
  `pub`**. An unmarked named constructor is a private helper, callable only from within the
  declaring class's own bodies — its methods, constructors, **and factories**. "Within the
  class" is determined by `Context.CurrentClassMethodOwner` (which works for factory/static
  contexts that have no `self`) or a `self` of the class.

An external call to a private named constructor raises RA0412 with a `pub`/factory
suggestion. The canonical pattern — a private named allocator plus a public factory — needs
no separate `priv` keyword:

```ra
class Account {
    pub final balance: int
    Account.raw(b: int) { self.balance = b }          // private (named, no pub)
    pub factory Account.open(b: int) {                  // public entry
        if b < 0 { throw "negative balance" }
        ret Account.raw(b)
    }
}
Account.open(100)   // ok
Account.raw(100)    // RA0412: constructor 'Account.raw(...)' is private
```

### 3.6 Overload resolution & diagnostics

- **Unnamed `T(args)`** — gather every unnamed candidate (generative *and* factory) whose
  signature binds the args (`CallableBinder.CanBind`, which already handles positional,
  named, optional/defaulted and variadic params). Zero ⇒ fall through to the inherited
  base-constructor chain or default-construct; exactly one ⇒ dispatch by kind; two or more ⇒
  RA0413 ambiguity listing the candidates.
- **Named `T.name(args)`** — same, restricted to constructors with that name. An unknown
  member on a type surfaces at member-access time as RA0402 with a Levenshtein "did you mean
  'T.x'?" hint computed over the type's named constructors and static members.
- A named constructor and a static method should not share a name; named-constructor
  resolution takes precedence so existing static dispatch is unaffected when names differ.

### 3.7 Generics

`Box<int>(args)` already binds `T=int` before construction. Named/factory constructors on a
generic class use the same explicit type arguments, written `Box<int>.of(7)`. The parser
commits the speculative generic args when the next token is `(` **or** `.`, so the
`<int>` rides the postfix chain onto the constructor call (a `.` after `>` only ever occurs
in this type-qualifier position, so it cannot mis-parse a real comparison). The bound
arguments flow into the generative field/`self` typing via the instance's `GenericBindings`.
Generic construction is interpreted on the AST path (not natively compiled), exactly as
`Box<int>(args)` is today.

### 3.8 Nullability & failure

A factory is an ordinary body, so it composes with Ra's existing error tools rather than
inventing a new one:
- *exceptional/invariant* failure → `throw` (caller uses `try`/`catch`);
- *expected* failure → declare `-> T?` and `ret null`, or `-> Result<T, E>` and return a
  result — the call site then sees the nullable/Result type and must handle it.
Generative constructors signal failure only by `throw` (they cannot return).

---

## 4. Cross-layer implementation

No new opcodes, no new AST node kinds, no new visitors. Touch points:

**Lexer** — add `Keyword.Factory` and map `"factory"`.

**AST** (`FunctionDefinitionNode`) — add `bool IsFactory`, `string? ConstructorName`
(null = unnamed), and `bool IsAnyConstructor => IsConstructor || IsFactory`. `IsConstructor`
keeps its original meaning — a *generative* constructor — so every current check stays
correct; factories are `IsConstructor == false && IsFactory == true`.

**Parser** — in the class member loop, recognise `factory` and the optional `.name` after
the type name; in `ParseFunctionDefinition`, parse the dotted constructor name and the
factory flag, allow an explicit return type only for factories, and emit the populated node.
Guard rails: `fn` before a constructor, `factory` combined with `static`/`abstract`/
`override`/`async`, a return type or arrow body on a generative constructor, and a factory
with no body — all rejected with helpful parser diagnostics. The expression parser commits
generic type args on a following `.` (for `Box<int>.of(...)`).

**Runtime** (`ClassTypeValue`) — one construction core,
`Construct(args, named, typeArgs, ctorName, callSite, …)`: resolve candidates → check
visibility (named-only) → (generative) allocate + field-init + run body, or (factory) run
body + enforce the return contract; with the inherited-base / default-construct fallback for
the unnamed no-candidate case. Resolution helpers `ResolveConstructorCandidates` /
`HasAnyConstructorNamed` / `SuggestMember`. `ExecuteWithNamedArgs` delegates to
`Construct(ctorName: null, callSite: Context)`.

**Runtime** (`BoundConstructorValue : BaseFunctionValue`) — a tiny thunk returned by member
access for `T.name`; on call it invokes `type.Construct(name, …, callSite: capturedCtx)`.

**Member access** (`MemberAccessHelper`) — in the `ClassType` branch, resolve named
constructors (returning the thunk) ahead of static methods; not inline-cached (visibility +
overload depend on the live call). The "no static member" failure gains a constructor-aware
"did you mean".

**Call chokepoint** (`FunctionCallExecutor.Invoke`) — when the callee is a `ClassTypeValue`,
route through `Construct(callSite: context)` so unnamed `T(args)` gets correct call-site
visibility. This is the single path shared by the VM, the AST visitor and the pipeline
operator.

**Diagnostics** (`DiagnosticCode`) — new runtime codes:
`RA0412 RuntimeConstructorPrivate`, `RA0413 RuntimeConstructorAmbiguous`,
`RA0414 RuntimeFactoryReturn`, `RA0415 RuntimeConstructorNotFound`. Errors carry a primary
label + actionable help, matching the existing private-field/method style; a local
Levenshtein powers "did you mean".

**.rac archive** — bump payload to V5; `FunctionDefinitionNode` serialises `IsFactory` +
`ConstructorName` behind a `ReaderVersion >= V5` gate, so V4 archives keep loading. A
compile→run round-trip of `tests_constructors.ra` passes all tests from the archive.

**IR/VM** — unchanged. `T(args)` = `OP_CALL` on the class value; `T.name(args)` =
`OP_GET_MEMBER` + `OP_CALL`; both already dispatch through `FunctionCallExecutor.Invoke`.

---

## 5. Edge cases

- Private named constructor reached from outside → RA0412 (label "called from outside the
  declaring class", help suggests `pub` or a factory).
- Two unnamed constructors both bind (e.g. an unnamed generative + an unnamed factory of the
  same arity) → RA0413 ambiguity, candidates listed.
- Mistyped named constructor (`Point.orogin()`) → RA0402 with "did you mean 'Point.origin'?".
- Factory falls through without `ret` → RA0414 "factory constructor must return a value".
- Factory returns a wrong/unrelated type → RA0414 return-type mismatch (expected `T`, got `U`).
- Factory returns a subtype → allowed.
- Factory returns `null` with implicit return type → RA0414 (must declare `-> T?`).
- `ret value` inside a generative constructor → "constructors cannot return a value".
- Return type / arrow body on a generative constructor → parser error.
- Generic factory/named ctor without type args on a generic class → existing "requires
  explicit type arguments".
- Abstract class: generative construction still blocked; a `factory` on an abstract class is
  allowed (it returns a concrete subtype) — the canonical abstract-factory pattern.
- A subclass with no own constructor still default-constructs even when its base exposes only
  named/factory constructors (implicit base-init chains only to an unnamed generative base).
- `final`/`let` fields: writable inside generative constructors (named included), sealed
  afterwards.
- Backward compatibility: existing unnamed generative constructors (pub or not), `super(...)`,
  records and structs are unchanged; `IsConstructor` semantics are preserved.

Out of scope for v1 (clean future extensions, noted to avoid scope creep): named/factory
constructors on **structs** (value types, separate runtime path; the spec is class-centric),
and `const`/compile-time constructors.

---

## 6. Test matrix (`tests_constructors.ra`)

Hard-asserted cases (throw + abort on failure): standard ctor (C1); named ctor without (C2)
and with (C3) params; factory returning a fresh instance (C4); cached/singleton factory (C5);
factory returning a subtype + branch (C6); private named allocator used internally (C4/C7);
illegal private-ctor access diagnosed (C13); ctor initialising `final` fields, sealed after
(C8); validating ctor (C9); abstract-class factory + abstract not directly constructible
(C15); `ret` rejected in a generative ctor (C20); generic unnamed + named ctors incl.
`Box<int>.of(...)` (C11); overload resolution across arities and a named ctor (C12);
ambiguous unnamed construction rejected (C18); defaults / partial defaults / named / full
positional params (C14); factory must-return enforced and satisfied (C17); forwarding-factory
redirection, arrow + block (C19); unknown named ctor rejected (C16). `bench_constructors.ra`
guards the hot path (tight allocation loops through unnamed/named/factory construction).

---

## 7. Keyword & compatibility check

`factory` is introduced as a reserved keyword. A scan of the C# sources and the entire `.ra`
test suite shows `factory` is never used as an identifier (only the substring inside
unrelated C# type names and one code comment), so reserving it breaks nothing. No other
keyword, token, or grammar production changes. `.rac` payload version becomes V5; V4 archives
keep loading via the reader-version gate. The full root + nested regression suites
(200+ `.ra` files) pass unchanged.

# Ra Indexers — Design

Status: shipping. Body-declared indexers on classes / structs (single- AND
multi-parameter, with arity overload and range slicing) build on the existing
native `[]` machinery and the `op_index` extension convention. **No new
opcodes, no new AST node kinds, no new visitors.**

## 1. Mission

`obj[key]` / `obj[key] = value` should work on user types as naturally as on a
list — declared right in the type body, read+write, computed, key of any type,
with overload-ready dispatch and errors that tell you exactly what to add. Ra
already had native indexing on its built-in containers and indexers via
`extend` blocks; this increment makes a type **declare its own indexer in its
body** and routes `[]` to it, unifying on one convention.

## 2. What already existed

* **Native `[]` on built-in containers** — `list`, `map`, `set`, `string`,
  `tuple`. Read lowers to `OP_LIST_GET` (0x54), write to `OP_SET_INDEX` (0x63)
  — fully IR-lowered (no AST fallback). Negative indexing (`xs[-1]`), bounds
  checks, and missing-key / out-of-range / wrong-type errors are in place.
  Tuples are immutable (write rejected).
* **Extension indexers** — `extend T { fn op_index(i) {…} fn op_index_set(i, v) {…} }`
  registered in the `ExtensionRegistry`, dispatched from
  `ClassInstanceValue` / `StructInstanceValue`.

## 3. What this adds — body-declared indexers

A class / struct declares an indexer as two ordinary methods:

```ra
class Sparse {
    var data = make_map()
    pub fn op_index(key: string): int        => map_get_or(self.data, key, 0)   // read:  s[key]
    pub fn op_index_set(key: string, v: int) { map_set(self.data, key, v) }      // write: s[key] = v
}

let s = Sparse()
s["hits"] = 42
print(s["hits"])      // 42
print(s["missing"])   // 0  (computed default)
```

* **`op_index(key…): T`** backs `obj[key]` (read). Omit it → reads on that type
  are rejected with a clear error.
* **`op_index_set(key…, value)`** backs `obj[key] = value` (write). Omit it →
  the indexer is **read-only**; assignment is rejected (a precise, catchable
  error, not a generic failure).
* The **key type is arbitrary** — `int`, `string`, an enum, a struct, anything
  the method's parameter accepts. Indexers can be **computed** (no backing
  store), expose **views/proxies** (return any value), and run validation.

### Dispatch & precedence

`obj[key]` evaluates `target.ListAccess(key)`; `obj[key] = v` evaluates
`target.ListSet(key, v)`. For a class / struct instance the override resolves,
in order:

1. **Body method** — `Definition.ResolveInstanceMethods("op_index")` (class) /
   `GetMethod` (struct). A class routes through `BoundClassMethodGroupValue`, so
   the indexer participates in **arity-based overload resolution** for free
   (an `op_index(i)` and an `op_index(i, j)` coexist and are selected by call
   shape — exercised by the multi-parameter syntax `obj[a, b]`, §7).
2. **Extension indexer** — the existing `ResolveIndexerEntry` path (backward
   compatible; ambiguous cross-module extension indexers still diagnosed).
3. **No indexer** — a specific error (§5).

Body indexers win over extension indexers, mirroring how a type's own methods
win over extension methods.

| Layer | Behaviour |
| ----- | --------- |
| Parser | `obj[i]` → `ListAccessNode`; `obj[i] = v` → `ListAssignmentNode` (unchanged) |
| IR / VM | `OP_LIST_GET` / `OP_SET_INDEX` → `target.ListAccess` / `target.ListSet` (unchanged; fully IR-lowered) |
| Runtime | `ClassInstanceValue` / `StructInstanceValue` overrides resolve the body `op_index` / `op_index_set` method, then the extension indexer, then error |

The hot path (native container indexing) is **untouched** — the new resolution
only runs for class / struct receivers, exactly where it’s needed.

## 4. Semantics

* **Read-only / write-only** — presence of `op_index` / `op_index_set` decides
  each direction independently. A write-only sink (set without get) is legal.
* **Return value** — `op_index_set` may return nothing; the assigned value is
  the expression result, as for native indexing.
* **Mutation** — a struct `op_index_set` that writes `self.field` persists
  (verified), matching struct value semantics for `var` fields.
* **Reference / null** — the key and value flow through the normal call binding;
  declared parameter types are enforced by the existing argument type checks
  (and the callable-signature diagnostics where a callable is the key/value).

## 5. Diagnostics

A missing indexer no longer surfaces as the generic “Illegal operation”:

```
type 'Grid' has no indexer: 'Grid[...]' is not defined
  help: define `fn op_index(index): T { ret … }` in the class body or an
        `extend` block (add `fn op_index_set(index, value)` to also allow
        `obj[index] = value`)

type 'Grid' has no assignable indexer: 'Grid[...] = value' is not defined
  help: define `fn op_index_set(index, value) { … }` in the class body or an
        `extend` block
```

These are catchable (`try { … } catch (e) { … }`), like the native
out-of-range / missing-key errors. Native-container index errors keep their
existing precise messages.

## 6. Competitive comparison

| Capability | C# `this[i]` | Dart `operator []` | Kotlin `get/set` | Python `__getitem__` | C++ `operator[]` | **Ra `op_index`** |
| ---------- | --- | --- | --- | --- | --- | --- |
| Body-declared get/set | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Arbitrary key type | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Read-only / write-only split | ✓ | partial | ✓ | partial | ✓ | ✓ (omit either method) |
| Same convention in extensions | ✗ (extension indexers C# 13+) | ✗ | ✓ (extension get/set) | n/a | ✗ | ✓ (identical `op_index` in `extend`) |
| Overload by arity | ✓ | ✗ (one `[]`) | ✓ | ✗ | ✓ | ✓ (class, via method group) |
| Actionable “no indexer” error | ✓ | ✓ | ✓ | ✓ (TypeError) | ✗ (compile error) | ✓ + fix-it help |

The standout: **one convention (`op_index` / `op_index_set`) spans body and
extension declarations**, and a class indexer is just a method group, so it
inherits overload resolution, generics and `self`-binding with no special case.

## 7. Multi-parameter indexers & slicing (shipped)

* **Multi-parameter `obj[a, b, …]`** — `ListAccessNode` carries
  `IReadOnlyList<AstNode> Indices`; the parser accepts a comma list; `Count == 1`
  keeps today’s `OP_LIST_GET` / `OP_SET_INDEX` hot path, and `Count > 1` lowers
  to an `op_index(a, b, …)` (read) / `op_index_set(a, b, …, value)` (write)
  method call via [`IndexDesugar`](Interpreter/Runtime/IndexDesugar.cs) — a
  synthetic but ordinary `FunctionCallNode` compiled normally, so **arity-based
  overload resolution** falls out: a class with both `op_index(i)` and
  `op_index(i, j)` is selected by the number of indices. Compound multi-index
  assignment (`m[r, c] += v`) desugars to `op_index_set(r, c, op_index(r, c) + v)`
  (mappable arithmetic/bitwise compound ops; assumes side-effect-free indices,
  which re-evaluate in the read-back). Wired in the IR compiler **and** the AST
  visitor; the Resolver walks every index.
* **Slicing** — Ra’s `..` ranges *are* the slice syntax: `xs[1..3]` returns a
  sublist (a range value flowing through `ListAccess`), and a custom
  `op_index(r: Range)` can accept one. No Python-style `:` syntax is added (it
  would duplicate ranges).

## 8. Out of scope (deferred)

* **`operator [](i): T { … }` declaration sugar** — *deliberately declined.*
  The `op_index` / `op_index_set` method convention is the single, consistent
  indexer spelling across class/struct bodies **and** `extend` blocks; a second
  `operator[]` declaration syntax would fragment that for marginal gain (and the
  operator parser is shaped for single-parameter binary operators). Documented
  as a non-goal, not a TODO.
* **Static (compile-time) indexer type checking** — the indexer’s parameter /
  return types are enforced at call binding (runtime) with the callable
  signature diagnostics; a static pass could front-load them. Low marginal value
  given Ra’s duck-typed runtime.

## 9. Tests

[`tests/types/test_indexers.ra`](bin/x64/Release/net10.0/tests/types/test_indexers.ra)
— 17 checks: class indexer (string key, computed default), read-only computed
indexer + rejected assignment, struct indexer (set persists, computed read),
no-indexer types rejecting read and write, **multi-parameter indexer** (set /
get / compound `+=`), **arity overload** (`op_index(i)` vs `op_index(i, j)`),
and **range-index slicing**. Extension-indexer tests (`tests_extensions.ra`
E18/E19) continue to pass. Full corpus: zero new failures.

## 10. NativeAOT / performance

No reflection, no new opcodes. Native-container indexing is unchanged (the same
IR-lowered `OP_LIST_GET` / `OP_SET_INDEX`). Body-indexer resolution runs only
for class / struct receivers and reuses the existing method-group dispatch (IC,
frame pool, body cache) — a class indexer call costs what a method call costs.

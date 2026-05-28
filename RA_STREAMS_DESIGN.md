# Ra Streams — Design

> Status: implemented. Companion to the runtime in
> `Interpreter/Runtime/Streams/`, the builtins in
> `Interpreter/Values/Functions/Builtins/StreamBuiltins.cs`, and the
> AST/Visitor extensions called out in §10.

## 1. Goals

Ra already has `async stream fn`, `emit`, `for await` and a buffered
`AsyncStreamCore`. That covers *push/pull* over an async channel.

It did **not** cover the common sync case — lazy pull iteration over a
collection with fused `map`/`filter`/`reduce` — and it did not expose a
user-facing operator library on either side. The Streams feature closes
both gaps with a single mental model:

* **`Stream<T>`** — synchronous, pull-based, lazy.
* **`AsyncStream<T>`** — the existing `AsyncStreamValue`, now with the
  same operator vocabulary.

The two abstractions are deliberately *distinct types* (not a union)
because their semantics differ in ways the user must see:

* a `Stream<T>` is a *protocol* that returns the next element on
  demand;
* an `AsyncStream<T>` is a *producer task* that *pushes* elements into
  a bounded channel.

Confusing them — like JS does between `Iterable` and `Observable` — is
the most common source of subtle bugs in stream libraries. Ra
distinguishes them in name and conversion (`to_async(s)`,
`to_sync(s)`).

The shared operator catalogue (`map`, `filter`, `take`, `drop`,
`flat_map`, `chunk`, `zip`, `distinct`, `scan`, `for_each`, `collect`,
`reduce`, `fold`, `count`, `sum`, …) means a pipeline written for one
form transposes to the other almost mechanically.

## 2. Non-negotiables

1. **Lazy by default.** A pipeline is built but does nothing until a
   terminal pulls.
2. **AOT-safe.** No reflection. No `dynamic`. Pull iterators are
   concrete classes implementing `IStreamSource`. Devirtualised by the
   JIT, statically discoverable by the AOT compiler.
3. **Fusion.** Each operator allocates exactly one wrapper. There is
   no per-element list materialisation between stages. `range(0,1e9)
   |> filter(even) |> take(10) |> collect` allocates 4 wrappers and a
   10-element list, regardless of the upstream size.
4. **Single-pass.** A `Stream<T>` is consumed once. Re-running a
   pipeline means rebuilding it from the source. This matches Rust and
   Java but explicitly rejects the JS surprise where a subset of
   iterators are re-iterable.
5. **Errors are values, not exceptions.** Every `PullNext` returns
   `(ok, value, done, error)`. Errors propagate immediately and the
   pipeline shuts down deterministically.
6. **Cancellation is explicit.** `stream_cancel(s)` flips a flag. The
   next pull returns `done = true`. Composed operators forward
   cancellation upstream so a `take(10)` over an infinite source stops
   producing the moment the terminal stops asking.
7. **No new opcodes.** All operators are builtins routed through
   `OP_CALL`. User lambdas use the existing closure path. Pipelines
   compose through the existing `|>` operator.

## 3. The surface in Ra

```ra
import streams;

# build
var s = stream_range(0, 1_000_000)
    |> stream_filter(|x| x % 2 == 0)
    |> stream_map(|x| x * x)
    |> stream_take(10);

# terminal
print(stream_collect(s));        # [0, 4, 16, 36, 64, 100, 144, 196, 256, 324]

# native for-loop integration
for x in stream_range(0, 5) {
    print(x);
}

# fold
var sum_sq = stream_range(1, 11)
    |> stream_map(|x| x * x)
    |> stream_reduce(0, |acc, x| acc + x);  # 385

# infinite source, fused early-stop
stream_iterate(1, |x| x * 2)
    |> stream_take(8)
    |> stream_collect();         # [1, 2, 4, 8, 16, 32, 64, 128]
```

For async:

```ra
async stream fn ticks(n: int) {
    for i in stream_range(0, n) {
        emit i;
        await sleep(10);
    }
}

await ticks(100)
    |> astream_filter(|x| x % 2 == 0)
    |> astream_map(|x| x * 2)
    |> astream_take(5)
    |> astream_collect();
```

## 4. Mental model: pull iterators

A sync stream is a state machine with one method:

```csharp
StreamPullResult PullNext(Context ctx);
//   .Done   — no more elements
//   .Value  — next element (if !Done && Error == null)
//   .Error  — propagated runtime error, terminates the pipeline
//   .Cancelled — explicit cancellation
```

Sources allocate one wrapper. Operators allocate one wrapper that
**owns** the upstream and forwards `PullNext` through their own
transform. The chain is single-threaded; no buffering happens between
stages; no allocation per element except the values themselves.

Compare to LINQ: LINQ also fuses, but goes through C#'s `IEnumerator`
with `MoveNext`/`Current` interface dispatch. Same shape, same cost
profile. The `StreamPullResult` struct gives us the early-exit and
error-out path LINQ pays via exceptions, in a single struct return.

## 5. The op catalogue

Source operators (return `Stream<T>`):

| name | signature | semantics |
|---|---|---|
| `stream_from(coll)` | `(List/Set/Map/Tuple) -> Stream` | one pass over the materialised collection |
| `stream_range(start, end[, step])` | `(int, int[, int]) -> Stream` | half-open `[start, end)` |
| `stream_iterate(seed, fn)` | `(T, fn(T) -> T) -> Stream` | infinite `seed, fn(seed), fn(fn(seed)), …` |
| `stream_repeat(v[, n])` | `(T[, int]) -> Stream` | `n` copies (infinite if omitted) |
| `stream_once(v)` | `(T) -> Stream` | single-element |
| `stream_empty()` | `() -> Stream` | zero elements |
| `stream_generate(fn)` | `(fn() -> Option<T>) -> Stream` | pull-driven; `None` ends |

Intermediate operators (return a new `Stream<T>`):

| name | signature | semantics |
|---|---|---|
| `stream_map(s, fn)` | `(Stream<A>, fn(A) -> B) -> Stream<B>` | per-element |
| `stream_filter(s, pred)` | `(Stream<T>, fn(T) -> bool) -> Stream<T>` | per-element |
| `stream_take(s, n)` | `(Stream<T>, int) -> Stream<T>` | first `n` |
| `stream_drop(s, n)` | `(Stream<T>, int) -> Stream<T>` | skip first `n` |
| `stream_take_while(s, pred)` | `(Stream<T>, fn(T) -> bool) -> Stream<T>` | until predicate fails |
| `stream_drop_while(s, pred)` | `(Stream<T>, fn(T) -> bool) -> Stream<T>` | from first failure |
| `stream_flat_map(s, fn)` | `(Stream<A>, fn(A) -> Stream<B>) -> Stream<B>` | substream concat |
| `stream_chunk(s, n)` | `(Stream<T>, int) -> Stream<List<T>>` | fixed-size groups, last may be short |
| `stream_window(s, n)` | `(Stream<T>, int) -> Stream<List<T>>` | sliding window of `n` |
| `stream_distinct(s)` | `(Stream<T>) -> Stream<T>` | drops repeats by `==` |
| `stream_scan(s, init, fn)` | `(Stream<A>, B, fn(B, A) -> B) -> Stream<B>` | running fold |
| `stream_zip(s1, s2)` | `(Stream<A>, Stream<B>) -> Stream<(A, B)>` | stops at shorter |
| `stream_enumerate(s)` | `(Stream<T>) -> Stream<(int, T)>` | `(index, value)` |
| `stream_concat(s1, s2)` | `(Stream<T>, Stream<T>) -> Stream<T>` | second after first |
| `stream_peek(s, fn)` | `(Stream<T>, fn(T) -> void) -> Stream<T>` | side-effect, value unchanged |

Terminal operators (force the pipeline):

| name | signature | semantics |
|---|---|---|
| `stream_collect(s)` | `(Stream<T>) -> List<T>` | materialise |
| `stream_for_each(s, fn)` | `(Stream<T>, fn(T) -> void)` | drive for side-effects |
| `stream_reduce(s, init, fn)` | `(Stream<T>, B, fn(B, T) -> B) -> B` | fold |
| `stream_fold(s, init, fn)` | alias for `stream_reduce` | |
| `stream_count(s)` | `(Stream<T>) -> int` | element count |
| `stream_sum(s)` | `(Stream<numeric>) -> numeric` | numeric add |
| `stream_min(s)` / `stream_max(s)` | `(Stream<T>) -> T` | comparable |
| `stream_first(s)` / `stream_last(s)` | `(Stream<T>) -> Option<T>` | safe head/tail |
| `stream_any(s, pred)` / `stream_all(s, pred)` | `(Stream<T>, fn(T) -> bool) -> bool` | short-circuit |
| `stream_find(s, pred)` | `(Stream<T>, fn(T) -> bool) -> Option<T>` | first match |

Lifecycle:

| name | signature |
|---|---|
| `stream_cancel(s)` | `(Stream<T>) -> bool` — cooperative stop |
| `stream_is_done(s)` | `(Stream<T>) -> bool` |
| `stream_close(s)` | `(Stream<T>) -> bool` — release source resources |

Async variants exist as `astream_*` for `map`, `filter`, `take`,
`drop`, `flat_map`, `for_each`, `collect`, `reduce`, plus bridges:

| name | semantics |
|---|---|
| `to_async(s)` | sync `Stream<T>` → `AsyncStream<T>` (per-pull producer) |
| `astream_to_list(s)` | drain async stream into `List<T>` |

## 6. Type system

Two new builtin type names:

* `Stream` — single type param `Stream<T>`.
* `AsyncStream` — single type param `AsyncStream<T>` (already the
  runtime carrier for `async stream fn`).

`TypeDescriptor` already supports generics + nominal types; we register
both names so type annotations parse. Element-type inference happens at
the source: `stream_range(0, 10)` is `Stream<int>`. Operators threading
a lambda inherit the lambda's return-type annotation. Where the user
omits annotations, the descriptor falls back to `Stream<any>` — same
escape hatch as `List<any>` already takes.

## 7. Lifecycle, errors, cancellation

A `StreamValue` carries three booleans:

* `IsDone` — terminal state. Subsequent `PullNext` returns done.
* `IsCancelled` — set by `stream_cancel`. Implies `IsDone`.
* `TerminalError` — last error encountered while pulling; surfaces
  to the next terminal.

`PullNext` is **idempotent on terminal state**: once `Done`, it keeps
returning done. This lets terminals decouple end-detection from
post-loop cleanup, and lets `flat_map`/`chunk` poll without checking
the upstream's state in two places.

Errors propagate as values, not exceptions. A `stream_map(s, fn)` whose
`fn` raises a runtime error stores the error in the wrapper, marks
`IsDone = true`, and surfaces the error at the next pull. The terminal
returns a `RuntimeResult.Failure`. Side effects already executed are
not unwound — same contract Ra has elsewhere.

Cancellation is cooperative. `stream_cancel(s)` flips the bit on the
*head* of the pipeline; operators propagate cancellation by checking
their upstream on each pull. This is the same model Java's
`Stream.close()` uses and avoids the "global cancellation token"
machinery that complicates Rust-style designs.

Closing a stream calls a `Dispose` hook on the source where the source
holds an OS resource (file handle, network socket). User-level stream
sources do not allocate handles so the hook is no-op for the built-in
operator chain. For library-defined sources, `stream_close` is the
contract.

## 8. Async streams

The existing `AsyncStreamValue` (`Interpreter/Values/Async/`) wraps an
`AsyncStreamCore` (`Interpreter/Runtime/Async/`). It is push/pull: the
producer `emit`s into a bounded `AsyncChannel`; the consumer
`PullNext`s. That gives natural backpressure — the producer blocks on
a full channel.

`astream_*` operators run as **producer tasks**: each operator spawns
a child task that pulls upstream, applies the transform, and emits
downstream. The child task is scheduled through the existing
`AsyncScheduler` and inherits the parent's cancellation scope, so a
`for await` that breaks early cancels every upstream stage.

Conversion bridges:

* `to_async(stream)` spawns a producer task that pulls the sync
  stream and emits each value into a fresh async stream. Useful for
  feeding sync sources into async consumers.

We deliberately do **not** add a sync-blocking `astream_pull(s)`
because it pins a thread-pool worker on every call and undermines the
async model. If a user needs the values synchronously they should
materialise with `astream_to_list` and lose the laziness explicitly.

## 9. Fusion

Two levels of fusion in the runtime:

* **Per-step pull-through** (always). Each operator wrapper allocates
  once and forwards `PullNext` through its delegate. No intermediate
  list materialises between stages.
* **Runtime op-list fusion** for `Map` / `Filter` / `Take` / `Drop` /
  `TakeWhile` / `DropWhile` / `Peek`. When a downstream operator
  receives a `StreamValue` whose source is already a
  `FusedStreamSource`, the new op is *spliced* into the upstream's op
  list rather than allocating a new wrapper. The collapsed chain
  exposes a single virtual `PullNext` for the whole pipeline, and the
  inner switch over the op list runs in a tight C# `for` loop with
  thread-local single-element lambda arg lists to avoid per-call
  allocations.

Eligibility for op-list fusion is gated on the lambda being
capture-free (`BuiltInFunctionValue`, or a `FunctionValue` whose
explicit `CaptureList` is null/empty AND whose materialised
`CapturedValues` map is empty). Lambdas with captures still work; they
just take the dedicated per-operator wrapper path. The motivation: the
brief calls for "fusion when lambda is inlinable + capture-free",
matching the AOT-friendly case where the call to the lambda devirtualises
cleanly.

Fusion is *not* applied to `flat_map`, `chunk`, `window`, `distinct`,
`scan`, `zip`, `enumerate`, `concat` — those carry structural state
(sub-streams, buffers, accumulators, multi-input cursors) that does
not fit the linear-op-list model. They stay as dedicated wrappers.

A future IR-level pass can lift fusion to compile time — emitting one
specialised `IStreamSource` per pipeline shape, with lambdas inlined
into a generated `PullNext`. The current runtime fusion already wins
against the materialised baseline (see §13 numbers); the IR pass would
remove the remaining per-element switch dispatch.

## 9b. Lazy `for x in stream { … }`

A dedicated bytecode path makes `for`-loop iteration of a sync
`StreamValue` lazy. The IR compiler emits a runtime dispatch at the
foreach prologue:

```text
PushScope iter
LoadNull, SetLocalDirect          # bind iter name placeholder
JmpIfStream collSlot → stream_lbl # branch when collection is a Stream
# materialising path (List/Set/Map/Tuple):
ForEachIterable, ListLen, ...
[body emitted once]
PopScope, Jmp done
stream_lbl:                       # lazy stream path
PushScope body_s
loop_top:
ClearScope
ForEachStreamPull itemSlot, collSlot, continueSlot
JmpIfNot continueSlot → exit_s    # done when no more values
AssignBinding itemSlot, iterName
[body emitted once again]
Jmp loop_top
exit_s:
PopScope body_s
done:
PopScope iter
```

Two new opcodes carry this:

* `Opcode.JmpIfStream = 0xE1` — `[op:u8][slot:u8][imm16: forward
  offset]`. Branches when `locals[slot]` is a sync stream
  (`RuntimeValueType.Stream`). Same encoding shape as `JmpIfNot`.
* `Opcode.ForEachStreamPull = 0xE2` —
  `[op:u8][itemSlot:u8][streamSlot:u8][continueSlot:u8]`. Pulls one
  element synchronously; sets `continueSlot` to a boolean (true when a
  value was produced, false on done). Pairs with the next `JmpIfNot
  continueSlot, exit_offset` to terminate the loop.

The body is emitted twice (once per path) — small static cost, no
runtime overhead because only one path is taken per iteration. Both
paths share the iter scope `PushScope` / `PopScope` so the runtime
scope stack stays balanced regardless of which branch fires. Nested
foreach loops compose correctly because each level uses its own
dispatch + scope pair.

The new opcodes are registered with the CFG builder
(`JmpIfStream` as `CondJump` with PC-relative target), SSA form (reads
for `JmpIfStream`; writes for `ForEachStreamPull` — its
`continueSlot` write is implicit and SSA treats it conservatively),
LICM (`JmpIfStream` flagged as a PC-relative branch so the hoist pass
correctly relocates its target), SCCP / GVN / IrRewriter (treated as
opaque side-effecting writes), and the `.rac` bytecode verifier.

Net effect: `for x in stream_iterate(0, |y| y + 1) { if x >= 100 { break } }`
no longer hangs — the for-loop pulls one element at a time and exits
on the body's `break`. Infinite producers + early stop is now safe.

## 10. Integration points (where the code lands)

| concern | path |
|---|---|
| `StreamValue` carrier + `IStreamSource` interface + ops | `Interpreter/Runtime/Streams/` |
| sync builtins | `Interpreter/Values/Functions/Builtins/StreamBuiltins.cs` |
| async stream operators | `Interpreter/Values/Functions/Builtins/AsyncStreamBuiltins.cs` |
| `RuntimeValueType` already has `Stream` | unchanged |
| `for x in stream { … }` | extended in `Interpreter/Visitors/Statements/ForEachNodeVisitor.cs` |
| builtin registration | `BuiltInRegistry.EnsureInitialized()` |
| async builtin registration | `AsyncBuiltins.Names` + `Execute` dispatch |
| type names | `Types/TypeDescriptor.cs` (`Stream`, `AsyncStream` nominal) |
| smoke tests | `tests_streams.ra`, `tests_streams_async.ra` |
| benchmark | `bench_streams.ra` |

## 10b. Files added / touched (post-fusion)

| concern | path |
|---|---|
| Sync stream value carrier | `Interpreter/Values/Streams/StreamValue.cs` |
| Pull contract + struct | `Interpreter/Runtime/Streams/IStreamSource.cs`, `StreamPullResult.cs` |
| Source iterators | `Interpreter/Runtime/Streams/StreamSources.cs` |
| Operator wrappers | `Interpreter/Runtime/Streams/StreamOperators.cs` |
| Fused operator chain | `Interpreter/Runtime/Streams/FusedStreamSource.cs` |
| Sync builtins | `Interpreter/Values/Functions/Builtins/StreamBuiltins.cs` |
| Async builtins + `to_async` | `Interpreter/Values/Functions/Builtins/AsyncStreamBuiltins.cs` |
| Lazy foreach opcodes | `Interpreter/IR/Opcode.cs` (+0xE1, +0xE2) |
| Lazy foreach VM dispatch | `Interpreter/Vm/VmExecutor.cs` |
| Lazy foreach IR codegen | `Interpreter/IR/IrCompiler.cs` (`CompileForEach`) |
| IR analyzer wiring | `Interpreter/IR/Analysis/{CfgBuilder,SsaForm,LicmHoist,Sccp,IrRewriter}.cs` |
| `.rac` verifier wiring | `Interpreter/Archive/RacBytecodeVerifier.cs` |
| Reflection helpers | `Interpreter/Values/Functions/Builtins/ReflectionBuiltins.cs` (`is_async_stream`) |
| Type registry | `Interpreter/Values/RuntimeValueType.cs` (+`AsyncStream`), `Types/TypeSystem.cs`, `Interpreter/Values/Functions/Builtins/BuiltinUtils.cs` |
| Smoke tests | `tests_streams.ra` (30), `tests_streams_async.ra` (10), `tests_streams_foreach.ra` (10), `tests_streams_fusion.ra` (9) |
| Benchmarks | `bench_streams.ra`, `bench_streams_fusion.ra` |

## 11. Tradeoffs and explicit non-goals

* **Reactive push streams (Rx-like)** — not in scope. Ra's existing
  `event`/`emit` covers broadcast; `AsyncStream` covers backpressured
  push/pull. Adding a third "hot observable" abstraction would
  triple-count the same semantics.
* **Auto-parallelism** — not in scope. A `stream_parallel(s, n)` could
  exist later by feeding chunks into spawned fibers and merging on a
  channel. It is intentionally not in the first cut.
* **Compile-time fusion** — see §9. Possible follow-up, not needed for
  correctness.
* **Re-iterable streams** — explicitly rejected. Use `stream_from(list)`
  to rebuild from a stored source instead.
* **Try/recover operators** — `stream_map(s, |x| try { … } catch …)`
  already works at the lambda level; a dedicated `stream_recover` would
  add surface for one use case the user can already express. Skipped.

## 12. Future work

* **IR-level fusion** (compile-time): emit one specialised
  `IStreamSource` class per pipeline shape, inlining the lambda
  bodies into a generated `PullNext`. The runtime op-list fusion of
  §9 is the bridge; a static pass would remove the per-element
  `switch` dispatch entirely for the hottest chains.
* Library-defined `IStreamSource` sources (file lines, network frames,
  AI token streams) via an extension point that returns a
  `StreamValue` from native code.
* Async stream backpressure tuning knob (currently fixed buffer of 8 in
  `AsyncStreamCore`).
* `stream_parallel(s, n)` on top of the fiber scheduler.
* `stream_to_iter(s)` for FFI consumers that want a Ra-side handle they
  can pull from C# directly.

## 13. Numbers

Measured on the working machine, x64 Release, NativeAOT-style build.
All times include parser + IR compile + run (cold). Run multiple times
to verify stability of the relative ordering.

### Pipeline: `range(0, 1M) |> filter(even) |> map(square) |> take(1K) |> collect`

| variant | time | element/ms |
|---|---:|---:|
| inline imperative `while` loop | 9–14 ms | ~100k |
| **stream pipeline (fused)** | **120–215 ms** | ~5–8k |
| list materialised + manual break | 180–730 ms | ~1–5k |

### Long fused chain: `range(0, 1M) |> map(+1) |> filter(even) |> map(*3) |> take(5000) |> collect`

| variant | time |
|---|---:|
| **fused stream pipeline + collect** | **~215 ms** |
| **fused stream + lazy `for` loop** | **~201 ms** |
| list materialised + manual loop | ~726 ms |

Fusion wins ~3.4× against the materialised-list path with manual
early break. Lazy `for x in stream { }` lands within 7% of `collect`
— and unlike the materialising `ForEachIterable` opcode it composes
with infinite producers without spinning.

Numbers are illustrative — they will shift as the IR compiler grows.
The qualitative ordering (inline > fused stream > materialised list)
is robust.

# RA_REMAINING_WORK.md — Remaining work + optimization roadmap

North star (unchanged): **0.0% AST-node-walking at runtime, 100% IR-lowered**, end-to-end
across IrCompiler → IrRewriter → VmExecutor → `.rac`. When done, `OP_NATIVE_DEFINE`
is deleted, the 80 node-visitors survive only as a parity oracle (or are deleted),
and `.rac` carries **zero serialized AST** for execution.

Tags: **[impact]** hot/med/cold · **[effort]** S/M/L/XL · **[risk]** lo/med/hi

---

## 0. GOAL ASSESSMENT — are we nativizing IR+VM / killing AST-walking?

**Partially — the common path is there, the endgame + the deletion are NOT.**

- ✅ Hot core lowered: arithmetic, locals, calls, if/while/for/foreach/try-finally,
  member/cast/enum/index access, lambdas, constructors, the **entire generic-type
  surface** (construction, free-fn + method calls `obj.m<T>()`, generic-method defs,
  where-constraints), operators, **auto** properties, all one-shot defs
  (class/struct/record/enum/interface/trait/extension/annotation/delegate/namespace/
  import/using) minus the feature gates below, all `yield`/inline-asm, goto/label,
  return-through-finally, annotation-application, IsType.
- ❌ NOT yet 0%. The runtime still carries the full AST-execution substrate:
  - **`OP_NATIVE_DEFINE` (0x90) — 16 emit sites in IrCompiler still live.**
  - **VM dispatch switch** = ~56-case node-visitor dispatch (`68` `NodeVisitor.Apply`
    calls in VmExecutor) — the AST-walking engine, reached on every fallback.
  - **`IrExpressionEvaluator`** (per-construct AST→RaFunction compile cache,
    `s_cache`) — the fallback execution path.
  - **`DefineRefs` / `AstRefs`** pools — ~25 `.Add` sites park live `AstNode`s per
    function for runtime consultation.
  - **`AstNodeSerializer.cs` ~3,050 lines** serializing all 96 `AstNodeType` kinds +
    12 AST-ref pools per function in `.rac` (the binary still ships executable AST).
  - **80 node-visitor files** under `Interpreter/Visitors/`.

**Verdict:** the engine is VM-first and the *hot* path is native, but "definitively
removed AST-node-walking" is **not met** until §1 closes and §1e deletes the
substrate. Realistically multi-week of focused work remains (events + computed
properties are the deep blockers).

---

## 1. DE-AST MIGRATION — remaining to hit 0% + delete OP_NATIVE_DEFINE

### 1a. One-shot-definition feature long tail (the big blocker)
The type-def builders (`TryBuild{Struct,Class,Record,Trait,Interface,Extension,
Annotation}Def`) still `return false` (→ whole def NativeDefine's) on these member
features. Each = capture in a flat `*Def` descriptor + reconstruct + `.rac` serialize
(the established pattern); the DEEP ones also need accessor-body IR.

- **Computed / custom-body / lazy / observer PROPERTIES** — **[hot][XL][hi]** THE
  deep blocker. Accessor bodies run via the visitor (`PropertyAccessOps.Visit`); the
  resolver deliberately doesn't frame them. Needs: (1) resolver `WalkProperty` frames
  each accessor body (getter `self,field`; setter `self,value,field`; observer
  `self,old,value,field`); (2) `PropertyAccessorNode` gets FrameId/CompiledBody/
  ParamBindings; (3) `GetOrCompileAccessor`; (4) `PropertyAccessOps` runs CompiledBody
  via `VmHostPool` **+ reads the `field` slot back from the frame post-exec** (the
  `field=value` mirror-back side-channel — correctness-critical); (5) `PropertyDef`
  carries compiled accessor bodies + reconstruct + serialize.
- **EVENTS** — **[med][L][med]** parallel to computed properties: handler bodies
  AST-walk; same resolver-framing + accessor-IR shape. Gated on all 6 def kinds.
- **INDEXERS** — **[med][M][med]** (extension `node.Indexers` gate, line ~2950) —
  `this[i]` get/set bodies, same accessor-IR shape.
- **EXTENSION-block members** — **[med][M][med]** operators / properties / events /
  ext-fields on `extend T { }` still fall back (only ext-methods + ext-operators via
  the method path lower). Reuse the OperatorDef/PropertyDef descriptors for the
  extension def.
- **CLASS inheritance / interfaces / traits-impl / static / abstract / named+factory
  ctors** — **[med][L][med]** each a `TryBuildClassDef` gate; named/factory ctors need
  `ConstructorName`/`IsFactory` capture + the redirect chain.
- **Method-level where-constraints / param-defaults / param-annotations** —
  **[cold][M][lo]** captured-data widening (no bodies for where; defaults need a
  thunk/const).
- **Non-const field / property / param defaults** — **[cold][M][med]** currently only
  const-folded defaults lower; non-const needs a compiled init-thunk RaFunction.
- **Annotation meta-annotations on defs / non-const annotation param defaults** —
  **[cold][M][med]** (`@target`/`@priority`/… on an annotation def entangle with
  `AnnotationProcessor`).
- **Trait/interface properties/events** — **[cold][M][lo]** same gates as classes.

### 1b. Soft-edge hardening (overflow → Wide-encode / spill, not feature gates)
These already lower but **silently fall back on an edge** (operand >u8/u16 or
pool/temp overflow). Each = Wide-prefix the operand or spill. **[cold][S–M][lo]** each:
- Switch (stmt+expr), Match (stmt+expr) — large arm/temp counts.
- Destructuring declaration / assignment — many bindings.
- ForAwait, Spawn, interp-asm — band/ref overflow.
- Any call/With/CallGeneric with a `DefineRefs` index > u8 (`c` operand).
- Audit: grep `NativeDefine` emit sites guarded by `> byte.MaxValue` / `IrCompileException`.

### 1c. Control-flow leftovers
- **Goto forward / undefined-label** — **[cold][M][med]** (only backward goto lowers;
  forward needs forward-jump fixups). Label non-lowerable body.
- **break / continue / yield THROUGH a finally** + **nested try-with-finally** —
  **[cold][L][hi]** (return-through-finally done; break/continue need the enclosing
  loop's jump target threaded; yield may be destined for a match/switch arm; nested
  finally needs inner→outer chaining). State-machine extension of OP_FINALLY_END.

### 1d. The 2 generic catch-alls (close LAST, after everything above)
- `CompileAsExpression` `!emitted` path → NativeDefine.
- `EmitFallback` (the universal statement escape) → NativeDefine.
These are unreachable only when 1a–1c all lower.

### 1e. THE DELETION (the payoff — only after 1a–1d)
- Delete `OP_NATIVE_DEFINE` (0x90) + its VM switch + `EmitFallback`/`HasNativeDefineRoute`.
- Delete `IrExpressionEvaluator` + `s_cache`.
- Delete `DefineRefs`/`AstRefs` pools + their `.rac` serialization.
- Strip `AstNodeSerializer` to **zero AST-execution** kinds (≈ −3,000 LoC); `.rac`
  becomes **pure bytecode** — smaller, O(1)-mmap, no polymorphic-deserialization
  attack surface, `.rac` payload version can drop the AST pools.
- Demote the 80 `Interpreter/Visitors/` to a compile-time parity oracle (or delete;
  keep `--bench-ast`'s OP_NATIVE_DEFINE harness only if still wanted).
- `Program._visitors` array + `RegisterVisitors` can shrink to the few still used by
  reconstruct-on-load (Define* still runs the visitor to *register* a type — confirm
  whether that survives or is also de-AST'd).

---

## 2. VM EXECUTION SPEED

- **Computed-goto / threaded dispatch** — **[hot][L][hi]** C# `switch` on opcode is a
  jump table but bounds-checked + no fall-through threading. Tail-duplicated
  per-handler `goto next` (token-threading) removes the re-dispatch branch
  mispredict. AOT-friendly via a label-address array is not directly expressible in
  C#; approximate with `[MethodImpl(AggressiveOptimization)]` + a `do/while(true)` +
  manual hot-case ordering.
- **Hot-case ordering** — **[hot][S][lo]** order the dispatch `switch` so the
  most-frequent opcodes (LoadLocalS, Move, Add/AddII, Call, Jmp*, GetMember) are first
  / grouped to help the JIT's jump-table + the CPU BTB.
- **Superinstructions / fusion** — **[hot][M][med]** fuse frequent pairs into one
  opcode (e.g. `LoadLocalS+Add`, `LoadConst+Call`, `GetMember+Call` → already partly
  via CallMethod; `Cmp+JmpIfNot` → already `JmpNotLtII` family — extend the family;
  `LoadIntS+Store`). Measure pair frequencies from `--dump-ir` corpora first.
- **Inline-cache widening** — **[hot][M][med]** LoadGlobalIc / CastIc / MemberAccessIc
  / CallMethodIc / EnumAccessIc exist (per-PC, re-prime on load). Add: polymorphic
  (2-4 way) ICs for member/method on hot megamorphic sites; an IC for `OP_CALL`
  global-callee resolution (skip the symbol-table parent walk).
- **Branch-prediction-friendly comparisons** — **[hot][M][med]** the typed
  `JmpNot{Lt,Le,Gt,Ge,Eq,Ne}II` fused compare-branches exist for int. Add the FF
  (float) + mixed variants; ensure the loop back-edge is a single predictable branch.
- **De-virtualize `RuntimeValue`** — **[hot][L][hi]** value dispatch goes through
  virtual/`is` checks. A tagged-union fast path (kind byte switch) for the hot
  primitive ops avoids the v-table + cast. Partly done (UnboxI/BoxI, II/FF opcodes);
  extend coverage so hot arithmetic never boxes.
- **Frame/locals access** — **[hot][M][med]** `LocalsView` indexing — ensure it's a
  raw `Span<RuntimeValue>`/array with no bounds-check in release hot paths
  (`Unsafe.Add` after a single frame-size assert).
- **Reduce per-call overhead further** — **[hot][M][med]** VmHostPool pools the
  Interpreter+VmExecutor; residual ~1KB/call. Profile: arg-list rent, Context.Copy on
  scope, SymbolEntry alloc. Target sub-512B/call.

## 3. IR / BYTECODE OPTIMIZATIONS (compile-time)

- **More aggressive SCCP / constant folding** — **[med][M][med]** fold across
  const-propagated calls to pure builtins; fold `as`-casts of consts; fold
  enum-tag/payload on const variants.
- **Peephole pass expansion** — **[med][M][lo]** the AddIntoSlot/SubIntoSlot peepholes
  exist; add: dead `Move` elimination after SSA copy-coalescing, redundant
  Load/Store pairs, `NotB(NotB x)`→x, double-negation, `Jmp`-to-`Jmp` chaining,
  unreachable-after-Ret/Halt trimming.
- **Copy-coalescing / register allocation** — **[med][L][hi]** slots are allocated
  linearly (AllocTemp). A proper SSA-based coalescer + linear-scan reuse shrinks
  SlotCount → smaller frames → less cache pressure + faster zeroing.
- **LICM coverage** — **[med][M][med]** hoisting exists (LicmHoist) but generic calls
  / new opcodes are barriers (conservative). Tighten purity analysis so more loop
  invariants hoist.
- **GVN / CSE** — **[med][M][med]** GlobalValueNumbering exists (RedundantWithDominator);
  extend CSE to member-access chains + pure-call results.
- **Bytecode minification** — **[cold][M][lo]** PassCompactor exists; add: remove
  no-op PushScope/PopScope pairs around empty scopes, fold adjacent ClearScope,
  shrink Names/Consts pools (dedup already?), strip unreachable blocks pre-emit.
- **Wide-operand only when needed** — **[cold][S][lo]** ensure Wide prefix is emitted
  lazily (it is, 9 sites) and never on the common <256 path.
- **Profile-guided block layout** — **[cold][L][hi]** `Profile: InvocationCount/
  LoopBackEdgeCount/IsHot` already tracked. Use it to lay hot blocks contiguous +
  cold blocks out-of-line (fall-through to the likely successor).

## 4. CPU / ALLOCATION / RAM

- **Per-call alloc** — **[hot][M][med]** see §2; the residual Interpreter+VmExecutor
  pooling helped (−11/−15% calls/method); chase Context.Copy + arg-list + SymbolEntry.
- **Value caching / interning** — **[med][S][lo]** small-int cache (−128..255 already?),
  bool/null singletons (done), interned short strings. BigNumber is immutable +
  aliasing-safe → safe to intern common values.
- **Avoid `ValueTask` round-trips on sync paths** — **[hot][M][med]** every fallback +
  some opcodes return `ValueTask<RuntimeResult>`; the `IsCompletedSuccessfully` fast
  path exists but the struct churn remains. Audit hot handlers for sync-shape.
- **Span/stackalloc for small transient buffers** — **[med][S][lo]** arg gathering,
  interp parts, format args → `stackalloc` when count is small + known.
- **GC config** — **[cold][S][lo]** Server GC / `TieredPGO` / `DOTNET_TieredCompilation`
  knobs for the long-running interpreter; the realtime priority already set.

## 5. .rac / MINIFICATION / LOAD

- **Pure-bytecode `.rac`** — **[med][L][med]** the §1e payoff: drop AST pools, ~−3KLoC
  serializer, O(1)-mmap load, no AST reconstruction.
- **Section compression / dedup** — **[cold][M][lo]** SharedConstPool exists; extend to
  cross-function shared Names + RaFunction bodies; optional LZ on cold sections.
- **Lazy / mmap section load** — **[cold][L][med]** load function bodies on first call
  rather than eager deserialize.
- **Tree-shake** — **[cold][M][lo]** StdLibTreeShaker exists; verify dead-fn removal is
  aggressive; strip unreferenced consts/names.

## 6. BENCHMARKS + TESTING

- **Wire heavy benches into default `--bench`** — **[S][lo]** `bench_lambdas.ra`,
  `bench_constructors.ra`, `bench_properties.ra` exist but aren't in default `--bench`
  (GBs); add an opt-in `--bench-heavy`.
- **Add benches for the newly-lowered**: generic calls (`bench_generic_call.ra`),
  auto-properties access, operator overloads in a hot loop — confirm the lowering
  actually wins vs the old fallback. **[M][lo]**
- **Per-opcode microbench / dispatch profiler** — **[M][med]** instrument opcode
  frequencies on the corpus to drive §2 superinstruction/hot-ordering choices.
- **Parity-oracle CI gate** — **[S][lo]** run `--parity <kind>` for every lowered kind
  in the suite (some kinds aren't forceable — IsType; document).
- **`.rac` fuzz / malformed-archive corpus** — **[M][med]** the verifier rejects
  malformed (22 archive checks); add a fuzzer for the polymorphic AST pools (until
  they're deleted) — deserialization attack surface.
- **AOT publish in CI** — **[S][lo]** the NativeAOT link needs vswhere+vcvars; pin a CI
  job so IL2026/IL3050 regressions from new code are caught.

## 7. KNOWN CORRECTNESS DEBT / FLAGGED (spawn_task) BUGS

- **Operator param name forced to `other`** — naming it anything else → RA0402
  (BoundOperatorValue hardcodes `Set("other",arg)` + resolver only exposes self/other).
- **Generic METHOD call parse fixed** (`a3d5a99`) — but verify no comparison
  regressions in the wild (the `<types>(` lookahead).
- **Lazy-range `for` var read inside any interpolation/fmt yields `null`** (LongLocal
  not boxed for the interp/fmt part) — noted in the plan (L4); tests dodge via `let`.
- **`finally` no-catch error-swallow** fixed (`2d60fc0`); the O3 parity divergence is
  intentional (golden=native).
- **Empty ctor / empty block `{ }`** is a parse error (Ra `{}`-is-a-set ambiguity) —
  use `{ pass }`. Possibly worth a clearer diagnostic.
- General: KNOWN_BUGS.md referenced in memory — reconcile with this list.

---

## SUGGESTED ORDER (highest ROI first)
1. **Computed/lazy/observer properties + events** (§1a deep) — unblocks the biggest
   remaining NativeDefine class; establishes accessor-body IR (reused by indexers).
2. **Indexers + extension-block members** (§1a) — reuse the accessor-IR.
3. **Soft-edge hardening** (§1b) — small, kills "silent fallback on edge".
4. **Class inheritance/ctors + remaining def gates** (§1a) — common in real code.
5. **Control-flow leftovers** (§1c) — goto-forward, break/continue-through-finally.
6. **The 2 catch-alls + THE DELETION** (§1d/§1e) — the payoff (pure-bytecode `.rac`,
   −3KLoC, no AST exec).
7. **Then perf** (§2–§4) — superinstructions, IC widening, threaded dispatch,
   copy-coalescing — now measurable against a 100%-lowered baseline.
</content>

# RA_DELETE_NATIVEDEFINE_TODO.md — endgame to 100% native IR+VM

Goal: **0.0% AST-node-walking at runtime, delete `OP_NATIVE_DEFINE` (0x90)** + the
whole AST-execution substrate (the tree-walking visitors as a runtime engine,
`IrExpressionEvaluator`, the `DefineRefs`/`AstRefs` AST pools, ~3 KLoC of
AST-execution serialization in `AstNodeSerializer`) → a **pure-bytecode `.rac`**.

Baseline at branch start: ~**89%** of the test corpus fully de-AST'd (0 NativeDefine);
**~85 NativeDefine ops in ~34 files**; **16** `Emit(OP_NATIVE_DEFINE)` sites in
`IrCompiler.cs`. Suite green throughout (graceful AST-walking fallback keeps the
language 100% functional at every step).

---

## PROGRESS LOG + ACCURATE REMAINING (recursive audit, branch HEAD 659e5ee)

Cleared this branch (14 commits, all green, parity MATCH, .rac round-trips, full
suite 281/281 + 3067 assertions at every step): record-class inheritance + abstract
records, lazy properties, **if/try-as-expression** (biggest, ~22 top-level ND),
node-level annotations (V12), match-pattern subset, unary-plus/chained-assign/
negative-literal defaults, **OP_IN (0xF7)** in/not-in, **OP_LIST_EXTEND (0xF8)**
list-spread (+ wired the dormant ListPush), non-const struct/class field defaults
(V13, construction thunk), interface/trait properties+events (V14), postfix/prefix
++/-- (+ C-style for, transitively), **OP_DELETE_LOCAL (0xFA)** del. Plus latent
bugs fixed: DCE erasing dead boxed rotates/shifts (silenced throws), struct-
serializer field-dedup (dropped thunks on load), 2 RacBytecodeVerifier false-
positives (DeclSlot=-1 sentinel, CatchSlot vs LocalCount).

**METHODOLOGY CORRECTION (important):** `--dump-ir` prints ONLY the top-level
script frame; ND inside fn/method BODIES is invisible to it. A recursive all-frames
audit (walk FuncDefRefs + DefineRefs + Children, count opcode 0x90) is the true
measure. Top-level grep says 7 ND / 4 files; **recursive truth = 47 ND / 21 files.**
The cleared constructs above lower wherever they appear (scope-independent IrCompiler
changes) — but other constructs' nested-body instances were never counted.

**REMAINING: 47 ND in 21 files, by category (sorted, recursive count):**
1. `match-unused-bind` (13, 11 files) — an arm binds a payload/binder var UNUSED in
   that arm body (`case Ok(n) -> "x"`, n dead): variant/struct/list/bare/`is T as n`.
   `case Ok(n) -> n` (used) lowers. Biggest single lever.
2. `match-stmt-destructure` (6, 6 files) — statement-position `match` (value discarded)
   with a binding/destructure arm (`match x { case Ok(n) -> s = n }`). Literal/wildcard
   stmt-match lowers.
3. `retry-statement` (6, 1 file) — `retry for N times [delay M] { … } [else { … }]`
   not IR-lowered at all.
4. `match-multiparam-adt` (~4, 3) — match on a 2+-type-param ADT (`Result<int,string>`,
   `Two<A,B>`) with destructure arms. Single-param (`Option<T>`) lowers.
5. `if-let / while-let binding` (~4, 1) — `if let PAT = e {…}` / `while let` with any
   binding pattern (distinct from match-expr lowering).
6. `or-pattern-with-binds` (2, 2) — `case L(x) | R(x) -> x`. Pure-literal or-patterns lower.
7. `nonconst-ext-field-default` (2, 1) — `extend C { var d = self.base*2 / = [1,2,3] }`.
8. `lazy-ext-field` (2, 1) — `extend C { pub lazy var d = … }`.
9. `nested-try-finally` (2, 2) — outer try WITH finally whose body has an inner try
   ALSO with finally. Inner-finally-only inside try/catch lowers.
10. `nested-match-in-arm` (1) — `match` directly as an arm body (flatten removes it).
11. `null-pattern` (1) — `case null ->` in a nullable/union match.
12. `ret-in-finally` (1) — `ret` originating INSIDE a `finally` (ret-in-try lowers).
13. `catch-variant-pattern` (1) — `catch (NotFound(path))` destructure (= the flagged
    pre-existing Try parity bug).
14. `ref-param-call` (1) — call site passing `&x` into `fn(p: ref)` (borrow+deref lower;
    only the ref-arg call site falls back — needs ref-arg call infra).

~33 of 47 are pattern-matching completeness gaps → a focused match-lowering pass is
the dominant lever. Then retry, try/finally edges, ext-field defaults, ref-param.
Clearing all 14 empties every reachable `Emit(OP_NATIVE_DEFINE)` site → enables the
deletion.

**DELETION SCOPE (re-scoped from phase 6 after investigation):** deleting
OP_NATIVE_DEFINE = remove the VmExecutor case (~1500) + the ~60 IrCompiler refs
(EmitFallback / CompileStatementWithFallback / HasNativeDefineRoute /
IsFallbackRoutable / rollback) + the Opcode.cs def. **KEEP** IrExpressionEvaluator
(shared by 10 files — annotations, contracts, calls — NOT NativeDefine-exclusive),
**KEEP** DefineRefs (shared with parked-node opcodes OP_WITH/OP_CALL_GENERIC/
OP_ASM_INVOKE/OP_IS_TYPE), **KEEP** the visitors (reconstruct-on-load DefineType runs
them). Gated on recursive-ND = 0 (the fallback wrapper becomes a hard-error once the
opcode is gone, so EVERY construct must lower first).

Each row: **[impact]** (ND ops it clears) · **[effort]** S/M/L/XL · **[risk]** lo/med/hi.
Workflow per row (proven): descriptor/opcode → four-pillar model (SSA/SCCP/IrRewriter/
LICM/verifier) → reconstruct → `.rac` serialize (self-versioned pool) → build →
probe ND=0 → `--parity <Kind>` MATCH → `.rac` round-trip → suite (exit 0) → canary →
commit. Delegate mechanical rows to subagents; verify each.

`OP_NATIVE_DEFINE` is deletable ONLY when phases 1–4 leave **zero** emit sites.

---

## Phase 1 — one-shot-definition feature gates (the `EmitFallback` / `OP_DEFINE_TYPE` fallbacks)

The type-def builders (`TryBuild{Struct,Class,Record,Trait,Interface,Extension,
Annotation,Enum,Delegate}Def`) still `return false` on these. Each = capture flat +
reconstruct + `.rac` pool (the established pattern).

1. **Annotated type defs** (`HasAnnotations` gate on every builder) — **[~10][L][hi]**
   `@ann` on a class/struct/record/enum/interface/trait/extension/annotation. The
   deepest of phase 1: annotation application registers metadata via
   `AnnotationProcessor` + the interceptor/validator/contract pipelines. Capture the
   annotation instances (name + const-folded args; non-const args → fallback) into a
   flat `AnnotationRef[]` on each `*Def`; reconstruct the `@ann` nodes; the visitor's
   existing annotation pass applies them. (Clears test_builtin_meta ≈7 + `@derive`
   records.) Sub-step: start with `@derive` on records (already a node FLAG, not an
   ann — cheap) then general `@ann`.
2. **Non-const defaults** (field / property / param) — **[~8][L][med]**
   Currently only const-foldable defaults lower. A non-const default
   (`var x = compute()`, `prop p: int = self.n*2`, `fn f(x = g())`) needs a compiled
   **init-thunk** RaFunction (self-bound where applicable), run at construction / call
   instead of the visitor walking the AST. Mirror the computed-property accessor IR
   (resolver-frame the default expr → GetOrCompile → run via VM). Touches
   StructFieldDef / PropertyDef / the method param-default capture + the field-init /
   PrepareExecutionContextForCall paths.
3. **Lazy properties** — **[~4][M][med]**
   `lazy prop p: T = <expr>` evaluates the default on first getter touch + memoizes
   (`LazyInitialized` set). Compile the default as a self-bound thunk; the lazy path
   (`PropertyAccessOps`) runs it via the VM. Reuses #2's thunk infra. (Clears
   test_properties lazy.)
4. **Static classes** — **[~3][M][med]**
   `pub static class C` — static-member-only semantics, no instances. Capture
   `ClassDef.IsStatic` + reconstruct; verify the visitor's static-class checks.
5. **Record-class inheritance** — **[~3][M][med]**
   `record class D : Base` (reference-flavour record + inheritance). Capture
   RecordDef.BaseType (mirror ClassDef inheritance). (Clears test_records.)
6. **Trait / interface properties + events** — **[~3][M][lo]**
   Same PropertyDef/EventDef widening already done for class/struct/record, applied to
   TryBuildTraitDef / TryBuildInterfaceDef.
7. **Enum non-const variant values, delegate where-constraints, annotation-def
   meta-annotations + non-const annotation param defaults** — **[~3][M][med]**
   The remaining per-kind gates (`TypeDefs.cs` comments list them).

## Phase 2 — soft-edge fallbacks (lowered constructs that bail on an edge)

These already lower; they `Emit(NativeDefine)` only on an operand-width / pool /
temp overflow. Each = Wide-encode the operand or spill. **[~1–2 each][S–M][lo]**.
Sites in `IrCompiler.cs`: destructuring decl (3598) + emit (3630), ForAwait (3705),
switch-stmt (3875) + match-stmt (3908), spawn (8465), match-expr (8795), yield (3833).
Also: **spread args** in calls `f(...xs)` (CallGeneric/Call reject spread) + **spread
in list literals** (test_spread ≈4) — needs a runtime-expand opcode or an
expand-into-band path.

## Phase 3 — control-flow leftovers

- **goto-forward / undefined-label** (3731) + **label non-lowerable body** (3778) —
  **[~1][M][med]** forward goto needs forward-jump fixups (only backward lowers today).
- **break / continue / yield THROUGH a finally** + **nested try-with-finally** —
  **[~2][L][hi]** extends the OP_SET_PENDING_FLOW / OP_FINALLY_END state machine
  (return-through-finally already done) to break/continue (need the loop's jump
  target) + yield (may be destined for a match/switch arm) + inner→outer finally
  chaining.

## Phase 4 — `in` / `is` residual + investigate stragglers

- **test_in_is (≈4)** — pin which `in` / `is` form falls back (membership `x in coll`,
  pattern `is`, range `in`?) and lower it. **[~4][M][med]**
- Re-run the full audit; any remaining file with ND>0 gets pinned + a row here.

## Phase 5 — the 2 generic catch-alls (close LAST)

- `CompileAsExpression` un-emitted paths (1356 ret-expr fallback, 5439, 7512) and
  the universal `EmitFallback` (2250). Unreachable only when phases 1–4 lower
  everything routable. Once unreachable, delete them.

## Phase 6 — THE DELETION (the payoff)

After phases 1–5 leave zero `Emit(OP_NATIVE_DEFINE)` and the audit shows 0 ND
corpus-wide:
1. Delete the `OP_NATIVE_DEFINE` case in `VmExecutor` (the ~56-case node-dispatch
   switch) + `EmitFallback` / `HasNativeDefineRoute` / `CompileStatementWithFallback`
   rollback in `IrCompiler`.
2. Delete `IrExpressionEvaluator` + its `s_cache` (the on-demand AST→IR compile).
3. Delete the `DefineRefs` / `AstRefs` polymorphic AST pools (per-`RaFunction`) — but
   FIRST confirm nothing still parks AST there (OP_DEFINE_TYPE reconstruct-on-load
   uses TypeDefs, NOT DefineRefs; OP_WITH / OP_CALL_GENERIC / OP_ASM_INVOKE / OP_FMT
   still park nodes in DefineRefs → those parked nodes must either move to flat
   descriptors or DefineRefs stays a *non-AST-execution* pool. Audit
   `st.DefineRefs.Add` / `st.AstRefs.Add` callers before deleting).
4. Strip `AstNodeSerializer` of the AST-execution kinds (~3 KLoC) — keep only what the
   remaining parked nodes (if any) need; drop the 12-pool AST serialization;
   `.rac` payload version bump → **pure bytecode** (smaller, O(1)-mmap, no
   polymorphic-deserialization attack surface).
5. Demote the ~80 `Interpreter/Visitors/` to a compile-time **parity oracle only**
   (the `--parity` harness + `--bench-ast`) or delete them; shrink
   `Program._visitors` / `RegisterVisitors` to whatever reconstruct-on-load still
   invokes (DefineX still runs the visitor to *register* a type — decide if that
   survives or the registration is also de-AST'd).
6. Final: full audit ND=0, suite green, `.rac` round-trip on the new pure-bytecode
   format, NativeAOT publish clean, `--parity all` (where forceable) MATCH.

## Note on parked-node opcodes (do NOT regress)

`OP_WITH`, `OP_CALL_GENERIC`, `OP_ASM_INVOKE(/I)`, `OP_ANNOTATION_APPLY`, `OP_IS_TYPE`
park an `AstNode` in `DefineRefs`/`AstRefs` and re-enter logic at runtime — these are
NOT `OP_NATIVE_DEFINE` but they DO keep some AST alive in `.rac`. Reaching a *truly*
pure-bytecode `.rac` (phase 6.3/6.4) means converting these to flat descriptors too
(e.g. With → field-name list; CallGeneric → arg-name + type-arg list; AnnotationApply
→ flat ann descriptor). Track separately; deleting OP_NATIVE_DEFINE (the primary
goal) does NOT require this, but the "zero serialized AST" stretch goal does.

## Known pre-existing bugs to fix alongside (flagged via chips)

- Operator overload param must be named `other` (resolver hardcodes it).
- Struct-method param defaults ignored at call site (`BoundStructMethodValue`).
- Named args on method calls (`obj.m(name: v)`) — method-group counts only positional
  arity.

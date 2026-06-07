# RA_DELETE_NATIVEDEFINE_TODO.md — endgame to 100% native IR+VM

Goal: **0.0% AST-node-walking at runtime, delete `OP_NATIVE_DEFINE` (0x90)** + the
whole AST-execution substrate (the tree-walking visitors as a runtime engine,
`IrExpressionEvaluator`, the `DefineRefs`/`AstRefs` AST pools, ~3 KLoC of
AST-execution serialization in `AstNodeSerializer`) → a **pure-bytecode `.rac`**.

Baseline at branch start: ~**89%** of the test corpus fully de-AST'd (0 NativeDefine);
**~85 NativeDefine ops in ~34 files**; **16** `Emit(OP_NATIVE_DEFINE)` sites in
`IrCompiler.cs`. Suite green throughout (graceful AST-walking fallback keeps the
language 100% functional at every step).

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

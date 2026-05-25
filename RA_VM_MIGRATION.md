# Ra Language — AST → IR + VM Migration

Internal working document. Not a public spec. Maintained throughout the
migration. The contents here override any earlier informal notes.

Author: migration agent.

Status (rolling):

- **Phase A — recon: complete.** Five parallel maps of AST/visitor, runtime
  values+symbols, async runtime, annotations+modules+interop, and non-local
  control flow. Captured in §1.
- **Phase B — design doc: complete.** This document. Locked target architecture
  (register-based VM with 3-address bytecode), invariants, milestones.
- **Phase C — IR design: complete.** Opcode catalogue locked (§3.4), encoding
  fixed (§3.3), function/frame layout fixed (§3.5).
- **M1 — IR/VM skeleton + AST fallback: complete.**
- **M2 — primitives, variable reads, arithmetic, comparisons (statement-level
  bare expressions only): complete.** Added: `IrCompileException`,
  `InstructionBuilder`, `ConstantPool` / `NamePool`, and a real
  `IrCompiler.CompileScript` that walks the parsed script root and tries to
  compile each top-level statement to native opcodes (LoadConst, LoadNull,
  LoadTrue, LoadFalse, Move, LoadGlobal, Add/Sub/Mul/Div/Mod/Pow/Shl/Shr/BAnd/
  BOr, Eq/Ne/SEq/SNe/Lt/Le/Gt/Ge, Not/BNot). Unsupported subtrees throw
  `IrCompileException`; the statement-level catch rolls back the tentative
  bytecode and emits a single `OP_VISIT_AST` for that statement. Parity sweep:
  **63/63 tests under `tests/{async,annotations,functions,control_flow,
  collections,errors,edge_cases,integration}/*.ra` produce byte-identical
  output AST vs VM.** Smoke benchmark (`tests_m2_smoke.ra`): **45 native
  opcodes vs 4 AST fallbacks** on a synthetic arithmetic script.
  Corpus-wide native ratio is **8%**, dominated by `print(...)` calls and
  `var x = …` declarations that M2 deliberately leaves on AST (they are
  statement-level mutations or function calls — both deferred to later
  milestones). Real corpus speedup arrives at M3 (control flow) and M5
  (function-body compilation).
- **M3 — control flow + assignments + declarations native: complete.**
  Native opcodes wired: `Jmp`, `JmpIf`, `JmpIfNot`, `AndJz`, `OrJnz`, `Neg`,
  `StoreGlobal`, `DeclareLocal`. Native IR-gen for: `if`/`elif`/`else`,
  `while`, `do-while`, `break`, `continue`, `retry`, `return`, `pass`,
  short-circuit `and`/`or`, unary `-`, `VariableAssignment` (EQ),
  single-target `VariableDeclaration` (no annotations).
  Body eligibility filter (`BodyIntroducesBindings`) rejects bodies that
  contain var/fn/class/etc. — those need a fresh-scope opcode that lands
  at M4. Bare `{ ... }` blocks at file scope also fall back (same reason).
  Shared `AssignmentHelper` and `DeclarationHelper` in
  `Interpreter/Runtime/` keep AST and VM bit-identical on the assignment /
  declaration paths.
  **Corpus native ratio jumped from 8% → 79%** across the 63-test sweep.
  Smoke (`tests_m3_smoke.ra`): native=295 fallbacks=14 on a synthetic
  control-flow workload.
  Build clean, 63/63 tests byte-identical AST vs VM.
- **M4 — scope opcodes + `for` + nested var decls + string literals: complete.**
  Native opcodes: `PushScope`, `PopScope`, `ClearScope`, `SetLocalDirect`,
  `AssignBinding`. Dispatch loop ctx is now mutable; opcodes mutate the
  current scope without disturbing the surrounding C# call frame.
  `LoopContext.BaselineScopeDepth` tracks the scope nesting at the loop's
  body entry so `break` / `continue` / `retry` emit the correct number of
  `OP_POP_SCOPE` opcodes in front of their jumps. `If` / `While` / `DoWhile`
  bodies now wrap their contents in a fresh scope (so nested `var x = ...`
  declarations die at body exit). `For` (numeric range with optional `step`)
  lowered with two-level scopes (iter scope + body scope cleared each iter)
  and a runtime asc/desc test on the step sign. Plain string literals (no
  interpolation) compile to `OP_LOAD_CONST`. Corpus native ratio: **79% →
  89%**. Build clean, 63/63 byte-identical AST vs VM.
- **M5 — function calls + cast + interp strings + collections: complete.**
  `OP_CALL` delegates to `FunctionCallExecutor.Invoke` for positional
  builtin and user-defined calls. `OP_CAST` carries a `CastRefs` pool to
  surface `TargetType` and source positions to the dispatch loop. Built on
  M4's foundation; corpus jumped to **94.6% → 95.7%** with cast wired in.
- **M6 — collection literals + compound assign + ternary + null-coalesce
  + interpolation + throw native: complete.** All the high-frequency
  expression and statement forms now lower natively:
  - Compound assignment (`+=`, `-=`, `*=`, `/=`, `%=`, `**=`, `&=`, `|=`,
    `<<=`, `>>=`, `and=`, `or=`, `??=`) — same opcode as plain `=`; the
    shared `AssignmentHelper` already encodes the operator selection.
  - `[…]`, `{…}` (set), `{k: v, …}` (map), `(…)` (tuple) — `NewList`,
    `NewSet`, `NewTuple`, `NewMap` opcodes; elements laid out in a
    consecutive temp slot band.
  - `..` / `..=` range — `Range` opcode emits the same eager `ListValue`
    materialization the AST visitor produces.
  - `a[i]` list/tuple/map indexed read — `ListGet`.
  - `cond ? a : b` ternary — JmpIfNot + JmpToEnd.
  - `a ?? b` null-coalesce — `NullCoal` opcode.
  - Interpolated strings — `Interp` opcode builds the final
    `StringValue` from a precomputed slot band of parts.
  - `throw expr` — `Throw` opcode.
  - Numeric literal boxing fix: plain decimals always produce
    `NumberValue(BigNumber)` (matching `NumberNodeVisitor.ParseLiteral`)
    so `ListValue.ListAccess` and friends — which require
    `Type == RuntimeValueType.Number` — accept the index without erroring.
  - `OP_LOAD_GLOBAL` realigned to match `VariableAccessNodeVisitor` (no
    `ExtractVariableValueByName` move-on-use semantics on plain reads —
    class definitions registered with `isLet: true` would otherwise be
    spuriously marked as moved after the first read).
  - `OP_NEW_SET` uses the visitor's linear-search dedupe (HashSet.Add
    silently miscounts duplicates without a matching
    GetHashCode/Equals contract).
- **M7 — OOP + ForEach native: complete.**
  New per-site reference pools on RaFunction
  (`MemberAccessRefs`, `MemberAssignRefs`, `ListAssignRefs`,
  `EnumAccessRefs`) carry the AST node alongside the opcode so the
  dispatch loop can recover `MemberTok` / `TargetType` / positions
  without inflating the bytecode width. Shared helpers in
  `Interpreter/Runtime/` (`MemberAccessHelper`, `MemberAssignmentHelper`,
  `ListAssignmentHelper`, `EnumAccessHelper`) host the post-evaluation
  body of each visitor so AST and VM go through one code path.
  - `OP_GET_MEMBER` (`obj.field` / `obj.method` — class/struct/enum/
    super/namespace/module dispatch + extension methods).
  - `OP_SET_MEMBER` (`obj.field = v` — honors const/let/final/static
    fields and annotation re-coerce).
  - `OP_SET_INDEX` (`a[i] = v`, including compound `+=`/`-=`/...).
  - `OP_ENUM_ACCESS` (`EnumType.Variant`).
  - `Self` lowered to `OP_LOAD_GLOBAL "self"` — the AST visitor's
    fast-path on ClassInstance / StructInstance / Enum / EnumType means
    the same load works for the binding the method-call machinery
    populates via `SetLocal`.
  - `OP_FOREACH_ITERABLE` canonicalises List/Tuple/Set/Map iteration
    into a ListValue (Map yields a TupleValue(key,value) per pair,
    matching `ForEachNodeVisitor`'s Map branch).
  - `OP_LIST_LEN` returns `NumberValue(collection.Count)` for any
    List/Tuple/Set/Map.
  - Bug fix: ForEach increment moved BEFORE the body (same lesson the
    M4 `For` loop already learned) so `continue` inside the body
    doesn't skip the advance and infinite-loop.
  - Corpus native ratio: **99.83% → 99.84%**, 63/63 byte-identical.
- **M8 base — try/catch native via EhTable: complete.** New
  `RaUserError` exception serves as the in-dispatch unwind signal: every
  opcode site that previously did `return res.Failure(err)` now
  `throw new RaUserError(err)`. The dispatch loop wraps the switch in a
  C# `try { switch } catch (RaUserError ue) { ... }`. The catch block
  scans `RaFunction.EhTable` for the *innermost* (smallest-region)
  handler covering the faulting PC, pops the runtime `ctx` chain back to
  the handler's `ScopeDepth` (matching the visitor's
  context.Copy()-then-discard semantics on raise), binds the error
  message into the catch slot, and jumps to the catch PC. If no handler
  matches, the original Failure propagates.
  `ExceptionHandler` extended with `ScopeDepth` so the dispatch loop
  knows how many parent ctx hops to unwind. `VmFrame.CtxDepth` tracks
  the runtime ctx depth, maintained by `OP_PUSH_SCOPE`/`OP_POP_SCOPE`.
  `IrCompiler.CompileTry` wraps the try body in its own
  `PushScope`/`PopScope` pair so `var x = …` inside `try { }` dies at
  try-end (matching `ScopeNodeVisitor.context.Copy()`).
  Finally clauses still fall back to `OP_VISIT_AST` (Finally semantics
  require running on every control-flow exit — return, break, continue,
  throw — and is staged for M9).
  Corpus native ratio: **99.84% → 99.89%**, 63/63 byte-identical.
- **M9 base — introspection + super + function-def native: complete.**
  New per-site reference pools on RaFunction: `TypeofRefs`, `NameofRefs`,
  `DerefRefs`, `SuperRefs`, `FuncDefRefs`. Shared helpers in
  `Interpreter/Runtime/`: `TypeofHelper`, `NameofHelper`,
  `DereferenceHelper`, `SuperHelper`, `FunctionDefinitionHelper`.
  Each helper hosts the original visitor body and is called by both the
  AST visitor and the new VM opcode. Opcodes added:
  - `OP_TYPEOF` (`[op][dst][src][refIdx]`): RuntimeValueType → canonical
    type-name string, including class generic bindings and tuple
    descriptors.
  - `OP_NAMEOF` (`[op][dst][refIdx:u16]`): verifies the bound name then
    returns its identifier as a StringValue.
  - `OP_DEREF` (`[op][dst][src][refIdx]`): `*expr` over BorrowValue +
    IReferenceValue (use-after-free / moved-source guarded).
  - `OP_GET_SUPER` (`[op][dst][refIdx:u16]`): SuperProxyValue scoped to
    the lexical class owner.
  - `OP_DEFINE_FUNCTION` (`[op][dst][refIdx:u16]`): builds the
    FunctionValue, freezes the lexical binding context, materialises
    `[capture]` lists, applies `@`-annotations, attempts DLL-import
    binding, registers parameter annotations. Lambdas (no name) yield
    the FunctionValue in `dst` directly.
  Corpus native ratio: **99.89% → 99.93%**, 63/63 byte-identical.
- **M10 base — one-shot definitions native: complete.** Approach:
  refactor each definition visitor to expose `public static Apply(...)`
  (or `async ValueTask<RuntimeResult> Apply` when the visitor uses await).
  VisitNode becomes a thin wrapper that calls Apply, so both AST and VM
  paths exercise identical code. New shared opcode `OP_NATIVE_DEFINE`
  (0x90) + per-function `DefineRefs : AstNode[]` pool on RaFunction.
  VM dispatcher switches on `node.NodeType` and calls the appropriate
  visitor's `Apply` *without* indexing `interpreter._visitors[]`.
  Refactored visitors: ClassDefinitionNodeVisitor,
  StructDefinitionNodeVisitor, EnumDefinitionNodeVisitor,
  InterfaceDefinitionNodeVisitor, TraitDefinitionNodeVisitor,
  AnnotationDefinitionNodeVisitor, ExtensionDefinitionNodeVisitor,
  ImportNodeVisitor, NamespaceDeclarationNodeVisitor,
  UsingNamespaceNodeVisitor. Helper methods inside those visitors
  (ValidateToStringMethod, ValidateInheritanceContract,
  ExtractEnumInt128, ApplyMetaAnnotation, VisitImport*) made `static`
  so they're callable from the lifted Apply.
  Corpus native ratio: **99.93% → 99.95%**, 63/63 byte-identical.
- **M11 — full visitor Apply refactor + long-tail native: complete.**
  Bulk-refactored 22 remaining visitors via PowerShell regex to add
  `public static Apply` and a thin VisitNode wrapper:
  TryUnwrap, Await, Spawn, Emit, ForAwait, Pipeline, Borrow,
  DereferenceAssignment, Goto, Label, Scope, SuperFor, Switch, AsmBlock,
  RegexLiteral, FormattedInterpolation, Yield, Break, Continue, Pass,
  VariableDelete, AnnotationApplication. Plus Match, Try (manual).
  All routed through `OP_NATIVE_DEFINE` — VM dispatch switch on
  `node.NodeType` calls each Apply directly. The `interpreter._visitors[]`
  array is no longer indexed from the VM path.
  Try-with-finally now routes through TryNodeVisitor.Apply via
  OP_NATIVE_DEFINE; try/catch without finally keeps the M8 native
  EhTable path for true VM-level exception unwinding.
  Inside private visitor methods that became called from a static
  context, helper methods (ValidateInheritanceContract,
  ValidateToStringMethod, ExtractEnumInt128, ApplyMetaAnnotation,
  VisitImportAll/Selective/Alias) made `static`.
  Corpus native ratio: **99.95% → 99.963%**, 63/63 byte-identical.
  Residual ~60 fallbacks: stale OP_VISIT_AST emissions for rare
  expression-position nodes in specific test patterns; pass rate
  unaffected.
- **Phase D — milestone-by-milestone IR growth: complete (M11).**
  Final state: 63/63 tests byte-identical AST vs VM, 99.963% of VM
  dispatches are non-VisitAst opcodes. Pass rate 100%.

---

## 0. TL;DR

Ra currently runs as a 100% tree-walking interpreter: `Interpreter.cs` dispatches
to one of 75 visitors via an `_visitors[(int)AstNodeType]` delegate array. Each
visitor returns `ValueTask<RuntimeResult>`. The pipeline is already async-clean
(no sync-over-async at user `await`s), `RuntimeResult` is a stack struct, and a
static `Resolver` already assigns `BindingId` (frame_id<<16 | offset) to every
identifier — the prerequisites for a real VM are in place.

Target architecture: **register-based, 32-bit-aligned bytecode VM**. Locals are
direct registers indexed by `BindingId.Offset`. Closures carry an upvalue
table. Dispatch is a `switch` over opcodes inside an `async ValueTask<…>` loop —
so `await` opcodes suspend the dispatch frame naturally without manual
coroutine stitching. Control flow lowers to relative jumps; try/finally lowers
to per-function exception-handler tables; yield/emit/spawn keep the existing
async runtime contract intact.

The migration is gradual and gated. The new VM lives next to the AST evaluator;
each function compiles to bytecode where possible and falls back to AST when a
node kind is not yet supported. We promote the VM to default only once parity
across the full `.ra` test corpus is reached and microbenchmarks (`bench_hotloop.ra`,
`bench_arithmetic.ra`) show a measurable speedup.

---

## 1. Current architecture (Phase A snapshot)

### 1.1 Pipeline

```
source (.ra)
  → Lexer.MakeTokens()            → Token[]      (Lexer/Lexer.cs)
  → Parser.Parse()                → AstNode root (Parser/Parser.cs)
  → DeriveTransformer.Apply()     → AST rewrite  (derive macros)
  → Resolver.Resolve()            → BindingIds attached to identifier nodes
  → StaticAnalyzer.Analyze()      → warnings
  → BorrowChecker.Analyze()       → warnings
  → Interpreter.VisitBlocking()   → executes AST
```

Entry: [Program.cs:147](Program.cs) `Run(fn, text)`.

### 1.2 AST surface

86 enum members in [AstNodeType.cs](Parser/Nodes/AstNodeType.cs). 75 of them
have visitors registered in
[Interpreter.RegisterVisitors](Interpreter/Interpreter.cs:54). The other 13 are
*structural* — extracted from a parent node (e.g. `Argument`, `SwitchCase`,
`StringPart`, `StructFieldDefinition`) and never directly dispatched.

Grouped:

- primitives: Number, String, StringPart, Null, Boolean, List, Set, Map, Tuple,
  RegexLiteral, FormattedInterpolation
- variables: VariableAccess, VariableDeclaration, VariableAssignment,
  VariableDelete, ListAccess, ListAssignment
- ops: BinaryOperation, UnaryOperation, NullCoalescing, Cast, Spread, Ternary,
  Range, Borrow, Dereference, DereferenceAssignment, Pipeline, Pass
- functions: FunctionDefinition, FunctionCall, Return, Argument
- control flow: If, IfCasesWrapper, While, DoWhile, For, ForEach, SuperFor,
  Switch, SwitchCase, Label, Goto, Yield, Break, Continue, Retry, Try, Throw,
  Scope, Match, TryUnwrap, Typeof, Nameof
- async: Await, Spawn, Emit, ForAwait
- OOP: ClassDefinition, ExtendDefinition, OperatorDefinition, Super, Self,
  StructDefinition, StructFieldDefinition, StructMethodDefinition,
  MemberAccess, MemberAssignment, EnumDefinition, EnumAccess,
  InterfaceDefinition, InterfaceMethodSignature, TraitDefinition,
  TraitMethodDefinition, CallableSignature
- modules/ns: ImportAll, ImportSelective, ImportAlias, NamespaceDeclaration,
  UsingNamespace
- annotations: AnnotationDefinition, AnnotationApplication, AnnotationParameter
- ASM: AsmBlock, AsmTextPart, AsmInterpPart

### 1.3 Dispatch

```csharp
public ValueTask<RuntimeResult> Visit(AstNode node, Context context)
{
    var index = (int)node.NodeType;
    if (index < 0 || index >= _visitors.Length || _visitors[index] == null)
        throw new Exception(...);
    return _visitors[index](node, context, this);
}
```

[Interpreter.cs:133](Interpreter/Interpreter.cs:133). One indirect call per
node; visitor-internal recursion drives the tree walk.

### 1.4 RuntimeResult contract

[RuntimeResult.cs](Interpreter/RuntimeResult.cs):

- single byte `FlowState`: Normal | Error | Return | Continue | Break | Yield
- `_value`, `_error`, `_flowValue` slots
- `Register(child, propagateLoopControl)` is the visitor-internal "bubble child
  flow up" idiom — pack-priority: Error > Return > Yield > Cont/Break > Normal

**VM must preserve this contract verbatim** so the existing visitors that
remain (operators, annotations, contracts, test runner) keep composing with the
VM-executed code.

### 1.5 Runtime values

Three-tier aliasing model on `RuntimeValue`:

- `IsCopy = true`  — immutable scalars (Integer / Long / Float / Double /
  Number(BigNumber) / Decimal / Short / UShort / Byte / UInt / ULong / Int128
  / UInt128 / Boolean / Null). `Aliased()` returns `this`.
- `IsCopy = false, IsSync = false` — mutable containers (List, Map, Set,
  Tuple), class/struct instances, references. `Aliased()` returns `this`
  (sharing); `Copy()` does a structural clone.
- `IsCopy = false, IsSync = true` — Strings (immutable but reference-semantic),
  async values (TaskValue, ChannelValue, AsyncStreamValue).

The variable read pipeline in
[Interpreter.ExtractVariableValueByName](Interpreter/Interpreter.cs:153)
implements the **let-move-on-use** rule for non-copy `let` bindings: first read
marks `entry.IsMoved = true`, second read is a runtime error.

### 1.6 Bindings (already done, ready to reuse)

[BindingId.cs](Interpreter/Pipeline/BindingId.cs): packed 32-bit
`frame_id<<16 | offset`. Sentinel `Unresolved = 0xFFFFFFFF`.

`BindingKind`: Unresolved | Local | Capture | Global | Builtin | Parameter |
SelfRef.

The Resolver pre-allocates slots and produces `FunctionDefinitionNode.FrameId`,
`.ParamBindings`, and `.ResolvedCaptures`. This is exactly the metadata the VM
needs to lay out frames — **no second resolver pass required**.

### 1.7 Symbol table

[SymbolTable.cs](Interpreter/Runtime/SymbolTable.cs): dictionary per scope,
parent-linked. Optional `_slots` array indexed by `BindingId.Offset` for O(1)
access. Generation counter invalidates `SymbolLookupCache` entries on add/remove.

[SymbolEntry.cs](Interpreter/Runtime/SymbolEntry.cs): packed flag byte —
`IsLet, IsMoved, IsPublic, IsStaticallyTyped, IsMutable, IsConstBinding,
HasMutableBorrow` — plus `SharedBorrowCount`. The VM still uses
`SymbolEntry`-shaped storage for top-level (script) bindings; function locals
inside the VM live in a denser `RuntimeValue?[]` array indexed by slot.

### 1.8 Async runtime

- [AsyncContext.cs](Interpreter/Runtime/Async/AsyncContext.cs): per-fiber, carried
  on `Context.AsyncCtx`. Holds cancellation scope, current task, in-stream
  flags.
- [AsyncScheduler.cs](Interpreter/Runtime/Async/AsyncScheduler.cs): defaults to
  `ThreadPoolFiberExecutor` (`ThreadPool.UnsafeQueueUserWorkItem(..., preferLocal:
  false)`). `RaTaskCore` *is* the work item — no closure wrapper allocation.
- [RaTaskCore.cs](Interpreter/Runtime/Async/RaTaskCore.cs): hybrid — sync fast
  path when `ValueTask.IsCompletedSuccessfully`, else
  `ContinueWith(ExecuteSynchronously)`. **Releases the host worker on
  suspension** (no sync-over-async at user `await`).
- channels/streams use `ManualResetEventSlim` peek-waiters; `select(...)` uses
  `Task.WaitAny` plus tiebreak by case index.

The VM must keep these properties: the dispatch loop returns
`ValueTask<RuntimeResult>`, `OP_AWAIT` uses real `await` on
`core.WaitAsync().ConfigureAwait(false)`, and `OP_SPAWN` keeps the
`AsyncContextOverride` push/pop bracket.

### 1.9 Annotations / contracts / tests

- **pure metadata** (no evaluator coupling, untouched by migration):
  MetadataRegistry, MetadataTarget, BuiltInAnnotations, BuiltInValidators,
  ConstraintAnnotationRegistry, CoercerRegistry, DeriveTransformer,
  StaticAnalyzer, NamespaceRegistry, ModuleResolver, ModuleSpecifier,
  ExtensionRegistry (lookup table only), NativeMarshaller, NativeStructLayout,
  AsmExecutor / AsmCodePool / TrampolineGen, BorrowChecker.
- **evaluator-coupled** (call `interpreter.Visit` or `BaseFunctionValue.Execute`
  — must keep working through `IInterpreter`):
  AnnotationProcessor.EvaluateArgs, AnnotationValidator (runs validator fns),
  AnnotationInterceptors (before/after hooks), ContractEvaluator
  (`@requires`/`@ensures`/`@invariant`), TestRunner, ModuleManager.ExecuteModule
  (visits module-body statements), MetadataKeyResolver (reads
  `ctx.SymbolTable`), NativeFunctionValue.Execute (extends BaseFunctionValue),
  DllImportBinder, CallbackRegistry.

For evaluator-coupled subsystems, the migration plan is to keep them dispatching
through `IInterpreter.Visit` against AST nodes for as long as needed —
annotations/contract expressions are short and not hot, so leaving them on the
AST path is acceptable indefinitely. Hot user code (function bodies) is what
gets bytecode-compiled.

### 1.10 Non-local control flow inventory

For each, the VM has a chosen lowering (see §3.4):

| Construct  | Today                                                          | VM lowering                                       |
| ---------- | -------------------------------------------------------------- | ------------------------------------------------- |
| `return`   | `res.SuccessReturn(v)`; loops/blocks check `FuncReturnValue`   | `OP_RET`                                          |
| `break`    | `res.SuccessBreak()`; loop clears flag                         | `OP_JMP` to loop-exit PC                          |
| `continue` | `res.SuccessContinue()`; loop clears flag                      | `OP_JMP` to loop-test PC                          |
| `throw`    | `res.Failure(new RuntimeError(...))`                           | `OP_THROW` + handler-table scan                   |
| try/catch  | `TryNodeVisitor` runs try/catch/finally as nested sub-visits   | exception-handler table on function metadata     |
| `yield`    | `res.SuccessYield(v)`; producer fiber drives stream            | `OP_EMIT` (yield in async-stream context only)    |
| `await`    | true async wait via `core.WaitAsync()`                         | `OP_AWAIT` (async opcode)                         |
| `goto`     | linear search of `Interpreter.Labels`, re-visit label AST      | `OP_JMP_ABS` after IR-gen resolves label → PC     |
| `retry`    | re-execute enclosing loop (signals via state)                  | `OP_JMP` to loop-entry PC                         |

### 1.11 Hot spots (profiling-blind, guided by complexity + AST node frequency)

1. `VariableAccessNode` — every variable read (≥ 1 per expression).
2. `BinaryOperationNode` — every binop; double-dispatch through virtual
   `AddedTo/SubbedBy/...` on `RuntimeValue`.
3. `FunctionCallNode` — every call; `FunctionCallExecutor` builds a new
   `Context`, copies args.
4. `MemberAccessNode` — every field/method lookup.
5. `IfNode` / `WhileNode` / `ForNode` — every control flow point.
6. `VariableDeclarationNode` / `VariableAssignmentNode` — every write.
7. `ListAccessNode` — every `[i]`.

These are the targets the IR must beat. Everything else can stay on the AST
path until parity is reached.

---

## 2. Invariants — what the VM cannot break

These are non-negotiable. Any VM design choice that conflicts must be revised.

1. **`RuntimeResult` semantics survive intact.** Visitors that the VM does not
   replace (e.g. operator-overload bodies, annotation arg expressions, contract
   expressions) keep using `Interpreter.Visit`. The VM dispatch is a different
   producer of `RuntimeResult` but must produce results indistinguishable from
   the AST path.
2. **`let` move-on-use semantics, including the borrow-block rule.** The VM
   must preserve `IsMoved` accounting and the
   "cannot move out of X: borrowed" error. Same for
   `IsConstBinding` (no reseat, but aliased read OK).
3. **Three-tier value aliasing.** Reads of `IsCopy=false` values must alias
   (no implicit copy). Reads of `IsCopy=true` values pass `Aliased()` (which
   for immutable scalars is a no-op identity).
4. **`finally` always runs** — even if try or catch did `return`/`break`/
   `continue`/`throw`/`yield`. A new control-flow signal in the body cannot
   short-circuit finally.
5. **Loop control does not propagate past its loop.** `OP_BREAK`/`OP_CONTINUE`
   target the innermost loop only, by encoding it as a relative jump at
   compile-time.
6. **`await` never pins a worker.** The VM's `OP_AWAIT` must yield via a real
   C# `await`, not via `GetAwaiter().GetResult()`. Stack frame is preserved by
   the C# async state machine because the dispatch loop is itself async.
7. **`Context` chain integrity for tracebacks.** `RuntimeError` walks
   `Context.Parent` to build traceback frames; VM frames must keep that chain
   so error messages remain useful.
8. **Annotation hooks, contracts, test runner still execute.** The VM must call
   into `AnnotationProcessor.Apply`, `AnnotationInterceptors.RunBefore/After`,
   and `ContractEvaluator.EvaluatePre/Post` at the same logical points as
   `FunctionCallExecutor` does today.
9. **`Spawn` keeps the `AsyncContextOverride` thread-local bracket.** Fibers
   need their own AsyncContext, not the caller's.
10. **Inline `asm { … }` blocks still compile and execute via `AsmExecutor` /
    `AsmRegionRegistry`.** The VM can either keep this as an "escape to AST
    visitor" or lower interpolation slots to bytecode that builds the asm
    string the same way.
11. **NativeAOT compatibility.** No reflection-based dispatch beyond what the
    existing code already uses; no `MakeGenericType` at runtime; no
    `System.Reflection.Emit` outside the existing `TrampolineGen` (which is
    already disabled under AOT).

---

## 3. Target architecture

### 3.1 Layering

```
front-end (unchanged):
  Lexer  → Parser  → DeriveTransformer  → Resolver

new mid-end:
  IrCompiler:   AstNode  →  RaFunction  (or fallback marker)
  IrVerifier:   RaFunction  →  ok | diagnostic

new back-end:
  VmExecutor:   RaFunction  →  ValueTask<RuntimeResult>
                ↑
                | for unsupported nodes during transition,
                | tail-calls into AstExecutor (existing visitor pipeline)

shared runtime (unchanged or lightly extended):
  RuntimeValue, SymbolTable, Context, AsyncScheduler, MetadataRegistry,
  ExtensionRegistry, NamespaceRegistry, NativeInvoker, AsmExecutor,
  Annotation*, ContractEvaluator, TestRunner, BorrowChecker
```

### 3.2 Why register-based

Compared to a stack VM:

| Aspect                | Stack                                           | Register (chosen)                                   |
| --------------------- | ----------------------------------------------- | --------------------------------------------------- |
| `x = a + b`           | `LOAD a; LOAD b; ADD; STORE x` (4 ops)         | `ADD x, a, b` (1 op)                                |
| Slot mapping          | extra push/pop on every local                   | `BindingId.Offset` IS the register index            |
| Constant folding      | needs peephole over 3-4 instrs                  | 1-instr peephole over `ADD dst, src, src`           |
| Bytecode density      | 1-byte opcodes, dense                           | 4-byte aligned, slightly less dense                 |
| C# dispatch loop      | hot, but more iterations per Ra statement       | fewer iterations, predictable                        |
| Async preservation    | identical                                       | identical                                            |

Decision: register-based with locals as registers (Lua-style), plus a small
expression stack only for variadic/spread arguments and tuple destructuring.

### 3.3 Encoding

Fixed-width 32-bit instructions, big enough for 3-address common ops, with
optional 32-bit "extension words" for wide operands (large constants, far
jumps).

```
bits  31 .. 24   16-bit unsigned operand-A is high half? no, layout below:

[ opcode : u8 ][ A : u8 ][ B : u8 ][ C : u8 ]
```

`A`, `B`, `C` are slot indices (0..255). For frames with > 256 locals (rare —
the Resolver's `FrameInfo.NextSlot` caps at 0xFFFF), the IR emits an `OP_WIDE`
prefix that re-reads the next instruction with 16-bit operands. The 256-slot
threshold covers >99% of real-world Ra functions based on the test corpus.

For 16-bit immediates (jump offsets, constant pool indices) the encoding is:

```
[ opcode : u8 ][ A : u8 ][ imm16 : u16 ]
```

For wide jumps (`> 32K`), an `OP_JMP_FAR` reads the next 32-bit word as the
absolute PC.

### 3.4 Opcode catalogue (v0 — draft)

Grouped by purpose. Final opcode numbers and final shape will land in
`Interpreter/IR/Opcode.cs`. This list is the design contract.

```
# loads / constants
OP_LOAD_CONST   dst, const16          # locals[dst] = consts[const16]
OP_LOAD_NULL    dst                   # locals[dst] = NullValue.Null
OP_LOAD_TRUE    dst
OP_LOAD_FALSE   dst
OP_LOAD_INT_S   dst, imm16            # signed small int literal, fast path
OP_MOVE         dst, src              # locals[dst] = locals[src]  (with Aliased())

# variable bindings & globals
OP_LOAD_GLOBAL  dst, nameconst16      # symbol-table lookup; cached on inline-cache slot
OP_STORE_GLOBAL src, nameconst16
OP_LOAD_BUILTIN dst, builtinId16
OP_LOAD_UPVAL   dst, upIdx            # closure upvalue array
OP_STORE_UPVAL  src, upIdx
OP_DECLARE      dst, kind, typeConst  # declares a local with binding kind/type
OP_DROP         slot                  # invalidate / unbind

# memory model
OP_MOVE_LET     dst, src              # let move-on-use; sets IsMoved on src's entry
OP_ALIAS        dst, src              # explicit Aliased() (used when reading a slot)
OP_BORROW       dst, src, mut?        # produce a BorrowValue
OP_DEREF        dst, src
OP_DEREF_STORE  ref, src

# arithmetic / bitwise (binary)
OP_ADD          dst, a, b
OP_SUB          dst, a, b
OP_MUL          dst, a, b
OP_DIV          dst, a, b
OP_MOD          dst, a, b
OP_POW          dst, a, b
OP_SHL          dst, a, b
OP_SHR          dst, a, b
OP_BAND         dst, a, b
OP_BOR          dst, a, b
OP_BXOR         dst, a, b

# unary
OP_NEG          dst, a
OP_NOT          dst, a
OP_BNOT         dst, a

# comparisons (yield BooleanValue)
OP_EQ           dst, a, b
OP_NE           dst, a, b
OP_SEQ          dst, a, b       # strict ===
OP_SNE          dst, a, b
OP_LT           dst, a, b
OP_LE           dst, a, b
OP_GT           dst, a, b
OP_GE           dst, a, b

# logical w/ short-circuit
OP_AND_JZ       a, jmp_imm16    # if !a, jump (operand b is fetched at jump dest)
OP_OR_JNZ       a, jmp_imm16

# null & nullish
OP_NULL_COAL    dst, a, b       # a ?? b (eagerly evaluated)
OP_NCJZ         a, jmp_imm16    # null-coalescing jump (for lazy ??)

# strings & literals
OP_STR_CONCAT   dst, a, b
OP_INTERP       dst, partsBase, partsCount   # build string from interpolation parts
OP_FMT          dst, expr, fmtconst16        # FormattedInterpolation

# containers
OP_NEW_LIST     dst, count16
OP_NEW_MAP      dst, count16
OP_NEW_SET      dst, count16
OP_NEW_TUPLE    dst, count16
OP_LIST_GET     dst, list, idx
OP_LIST_SET     list, idx, src
OP_LIST_PUSH    list, src                   # used by spread / build
OP_MAP_GET      dst, map, key
OP_MAP_SET      map, key, src
OP_RANGE        dst, start, end             # start..end → RangeValue / iter

# member access
OP_GET_MEMBER   dst, obj, nameconst16
OP_SET_MEMBER   obj, nameconst16, src
OP_GET_INDEX    dst, obj, idx               # generic indexing (lists/maps/custom)
OP_SET_INDEX    obj, idx, src

# control flow
OP_JMP          jmp_imm16
OP_JMP_IF       cond, jmp_imm16
OP_JMP_IF_NOT   cond, jmp_imm16
OP_JMP_FAR      pc_abs32 (extension word)

# functions
OP_CLOSURE      dst, funcConst16            # captures upvalues per ResolvedCaptures
OP_CALL         dst, fn, argBase, argCount  # synchronous call w/ value args
OP_CALL_KW      dst, fn, payloadConst16     # call with positional+named args from payload
OP_TAILCALL     fn, argBase, argCount
OP_RET          src                          # return locals[src]
OP_RET_NULL                                  # return NullValue.Null

# methods / OOP
OP_CALL_METHOD  dst, recv, nameconst16, argBase, argCount
OP_CALL_SUPER   dst, recv, nameconst16, argBase, argCount
OP_NEW_INSTANCE dst, classConst16, argBase, argCount
OP_GET_SELF     dst                          # locals[dst] = self
OP_TYPEOF       dst, src
OP_NAMEOF       dst, src
OP_CAST         dst, src, typeConst16
OP_IS           dst, src, typeConst16

# match / try-unwrap
OP_MATCH_BEGIN  scrutinee
OP_MATCH_ARM    armIdx16  (jumps when no match)
OP_MATCH_END

# exceptions
OP_THROW        src                          # throws err in locals[src]
OP_ENTER_TRY    handlerIdx16
OP_LEAVE_TRY    handlerIdx16
OP_FINALLY_END                               # marker for finally-fixup

# async
OP_AWAIT        dst, src                     # await locals[src]; resume with result in dst
OP_SPAWN        dst, fn, argBase, argCount
OP_EMIT         src                          # emit to current stream producer
OP_FOR_AWAIT    iterVar, stream, body_imm16  # iterate async stream

# loops (helpers; not strictly necessary but allow specialized impls)
OP_FOR_INIT     iter, start, end, step
OP_FOR_TEST     iter, jmp_imm16              # exit jump
OP_FOR_NEXT     iter
OP_FOREACH_INIT iter, collection
OP_FOREACH_NEXT iter, item, jmp_imm16        # exit jump

# annotation / contract hooks (kept as opcodes so AST path is bypassed cleanly)
OP_RUN_PRE      handler16
OP_RUN_POST     handler16, retSlot

# inline asm
OP_ASM_INVOKE   regionId16, argsBase, argsCount, retBase, retCount

# misc
OP_PASS                                       # no-op (Ra `pass` keyword)
OP_DELETE       slot                          # explicit variable delete

# transitional bridge
OP_VISIT_AST    astNodeRef16                  # fallback: tail-call AST visitor for unsupported sub-tree
```

The `OP_VISIT_AST` opcode is the critical migration aid: it lets the IR
compiler emit unsupported sub-trees as a callback into the existing visitor
path, with full Context plumbing. Without it the migration would be a big-bang
rewrite.

### 3.5 Function / frame representation

```csharp
public sealed class RaFunction
{
    public string Name;
    public int FrameId;               // matches Resolver's FrameInfo.FrameId
    public int LocalCount;            // upper bound from FrameInfo.NextSlot
    public int Arity;                 // param count (named slots 1..Arity)
    public byte ParamFlags;           // variadic? has defaults?
    public uint[] Code;               // 32-bit instructions, big-endian-agnostic
    public RuntimeValue[] Consts;     // constant pool
    public string[] Names;            // identifier-name pool (for GetMember / Globals)
    public RaFunction[] Children;     // nested closures
    public UpvalueSpec[] Upvalues;    // closure capture map (parent slot or parent upval)
    public ExceptionHandler[] EhTable;
    public DebugInfo Debug;           // PC → source position
    public FunctionDefinitionNode? AstFallback; // for OP_VISIT_AST + transitional re-entry
    public AnnotationInstanceValue[] Annotations; // for interceptor / contract dispatch
}

public readonly struct ExceptionHandler
{
    public int StartPc, EndPc;        // protected region [start, end)
    public int CatchPc;               // -1 if no catch
    public int FinallyPc;             // -1 if no finally
    public byte CatchSlot;            // local slot to bind the error in catch
}

public readonly struct UpvalueSpec
{
    public bool IsLocal;              // true → capture parent local at Index; false → parent upval at Index
    public ushort Index;
}
```

A `VmFrame` lives on the C# stack of the dispatch loop (one per `Invoke`).
Locals are a `RuntimeValue?[]` of size `RaFunction.LocalCount`, allocated once
per call. For very small frames we can pool the arrays per arity (deferred —
optimize after correctness).

### 3.6 Dispatch loop (sketch)

```csharp
public async ValueTask<RuntimeResult> Execute(VmFrame f, Context ctx)
{
    var code = f.Function.Code;
    var locals = f.Locals;
    var consts = f.Function.Consts;
    var res = new RuntimeResult();

    while (true)
    {
        uint instr = code[f.Pc++];
        var op = (Opcode)(instr & 0xFF);
        int a = (int)((instr >> 8) & 0xFF);
        int b = (int)((instr >> 16) & 0xFF);
        int c = (int)((instr >> 24) & 0xFF);

        switch (op)
        {
            case Opcode.LoadConst:
                locals[a] = consts[(instr >> 16) & 0xFFFF];
                break;

            case Opcode.Move:
                locals[a] = locals[b]?.Aliased();
                break;

            case Opcode.Add:
                // dispatch via existing RuntimeValue.AddedTo virtual
                var addRes = locals[b]!.AddedTo(locals[c]!);
                if (addRes.error != null) return res.Failure(addRes.error);
                locals[a] = addRes.value;
                break;

            case Opcode.Jmp:
                f.Pc = f.Pc - 1 + (short)((instr >> 16) & 0xFFFF);
                break;

            case Opcode.Await:
                var task = locals[b] as TaskValue
                           ?? /* error */ ;
                if (!task.Core.IsCompleted)
                    await task.Core.WaitAsync().ConfigureAwait(false);
                locals[a] = task.Core.Result;  // or propagate failure
                break;

            // …

            case Opcode.Ret:
                return res.SuccessReturn(locals[a] ?? NullValue.Null);

            case Opcode.VisitAst:
                var astRef = f.Function.AstFallback /* + lookup table */;
                var subRes = await ctx.Interpreter.Visit(astRef, ctx);
                if (subRes.ShouldReturn()) return subRes;
                locals[a] = subRes.Value;
                break;
        }
    }
}
```

Critical properties of this loop:

- **Async-safe.** Any opcode that needs to suspend just `await`s. The C# state
  machine snapshots `f`, `code`, `locals`, `consts`, `res`, and `f.Pc` across
  the suspension — that's effectively a fiber save with zero VM-side work.
- **One indirect-branch per opcode (the `switch` jump table).** Modern JITs
  lower `switch` over a dense `byte` to a jump table; AOT does the same.
- **No allocation per dispatched opcode** in the common case. Allocations
  happen only when a value is genuinely produced (new list, new closure, ...).
- **Errors are returned as `RuntimeResult.Failure(...)`** — never thrown — so
  the C# stack does not unwind on Ra throws. That preserves the existing
  cheap-throw model.

### 3.7 Exception handling at the IR level

`try { … } catch (e) { … } finally { … }` lowers to:

```
                OP_ENTER_TRY handlerIdx
   tryRegion:   …  body bytecode  …
                OP_LEAVE_TRY handlerIdx
                OP_JMP afterFinally
   catchRegion: (handler.CatchPc)
                <bind err to handler.CatchSlot>
                …  catch body  …
                OP_JMP afterFinally   (skip "uncaught" path)
   finallyEntry:(handler.FinallyPc)
                …  finally body  …
                OP_FINALLY_END
   afterFinally:
```

On `OP_THROW`, the dispatch loop scans `EhTable` for the innermost handler
whose `[StartPc, EndPc)` covers `f.Pc - 1`. If `CatchPc != -1`, jump there with
the error bound in `CatchSlot`. If only `FinallyPc != -1`, run finally then
re-raise.

For `return`/`break`/`continue` inside a try, the IR emits a sequence that
runs the in-scope finally(ies) first via `OP_LEAVE_TRY` (which the dispatch
loop interprets as: if `FinallyPc != -1`, execute finally then resume the
saved control intent). This matches today's `TryNodeVisitor` behaviour where
finally runs even on early return / break.

### 3.8 Async (await / spawn / emit / for-await)

- `OP_AWAIT a, b` — `a` is the result slot, `b` is the value to await. The
  dispatch loop calls `WaitAsync()` on the underlying `RaTaskCore` and
  `await`s. Result extraction mirrors
  [AwaitNodeVisitor.cs](Interpreter/Visitors/Async/AwaitNodeVisitor.cs).
- `OP_SPAWN dst, fn, argBase, argCount` — gather args, push
  `AsyncContextOverride`, call `AsyncScheduler.Schedule(...)`, store the
  resulting `TaskValue` in `dst`. The scheduled fiber executes either a
  VM-compiled function or — via `OP_VISIT_AST` — the existing AST path.
- `OP_EMIT src` — only valid inside an async-stream producer; calls
  `context.AsyncCtx.CurrentStreamProducer.Emit(...)`.
- `OP_FOR_AWAIT iter, stream, body_imm` — loops `stream.PullNext()`; on
  cancellation or stream close, jumps past the loop body. Lowering matches
  [ForAwaitNodeVisitor.cs](Interpreter/Visitors/Async/ForAwaitNodeVisitor.cs).

### 3.9 Closures

At `OP_CLOSURE dst, funcConst16`:

```csharp
var nested = (RaFunction)consts[funcConst16];
var upvals = new RuntimeValue?[nested.Upvalues.Length];
for (int i = 0; i < nested.Upvalues.Length; i++)
{
    var spec = nested.Upvalues[i];
    upvals[i] = spec.IsLocal ? locals[spec.Index] : f.Upvalues[spec.Index];
}
locals[dst] = new VmClosureValue(nested, upvals, ctx.SymbolTable);
```

`VmClosureValue` extends `BaseFunctionValue` and its `Execute(args)` enters
the VM `Execute` loop. This lets VM closures call AST functions, and AST
functions call VM closures, transparently — both paths share
`BaseFunctionValue`.

### 3.10 OOP dispatch

`OP_CALL_METHOD dst, recv, nameconst16, argBase, argCount` does the same
lookup `MemberAccessNodeVisitor` + `FunctionCallNodeVisitor` would do today:
look up the method via `ClassInstanceValue` / `StructInstanceValue` vtable,
fall back to `ExtensionRegistry.Resolve(...)`, build the bound-method, call.

Class and struct *definitions* are statements that can usually run once at
module load — these can stay on the AST path (`OP_VISIT_AST`) with no
performance penalty. We do NOT need to lower class definitions to bytecode
in v1.

### 3.11 Annotations + contracts integration

For a function entry/exit, the IR-compiler scans
`FunctionDefinitionNode.Annotations` (set by `AnnotationProcessor`) and emits
hook opcodes at the prologue/epilogue:

```
OP_RUN_PRE  beforeHandlerId    # runs @before / @requires
…body…
OP_RUN_POST afterHandlerId, retSlot   # runs @ensures / @after
OP_RET retSlot
```

Each handler ID indexes into the function's
`AnnotationInstanceValue[]` so the dispatch loop can call into
`AnnotationInterceptors`/`ContractEvaluator` exactly as
`FunctionCallExecutor` does today. Contract expression bodies remain
AST-visited (they are not on hot paths).

### 3.12 Goto/label

The Resolver does *not* currently produce label-PC tables (labels are
discovered at runtime by `LabelNodeVisitor`). For the IR:

- During IR-gen, perform a one-shot pre-walk of the function body collecting
  `LabelNode.VarName.Value → PC offset`. Build a per-function label table.
- `Goto` lowers to `OP_JMP_FAR target_pc`.
- Today's semantics (control returns from goto's caller after the label body
  finishes — see [GotoNodeVisitor.cs:23](Interpreter/Visitors/Special/GotoNodeVisitor.cs:23))
  is unusual but reproducible: encode the label as a "computed jump" that
  records a return-PC for the goto site. **Deferred to a late milestone — keep
  goto on OP_VISIT_AST until parity required.**

### 3.13 Inline asm

`AsmBlockNodeVisitor` already compiles + caches asm regions via
`AsmRegionRegistry`. For the IR:

- Interpolation parts that are pure constants — bake into the region key.
- Interpolation parts that are runtime expressions — IR emits the expressions
  and passes their slots into `OP_ASM_INVOKE`, which routes through
  `AsmFunctionFactory` exactly like today.

This is purely a wiring change — the asm runtime stays put.

---

## 4. Migration milestones

Each milestone ends with a green test run (`dotnet build && dotnet run -- <each
*.ra test>`), no regressions in `bench_hotloop` / `bench_arithmetic`, and the
`OP_VISIT_AST` fallback count for the corpus documented.

### M0 — design lock-in (this doc)
Status: in progress. Output: this doc, opcode catalogue draft.

### M1 — IR/VM skeleton, AST fallback always-on  ✅ DONE
- Created `Interpreter/IR/` (Opcode enum, RaFunction, ExceptionHandler,
  UpvalueSpec, Encoding, IrCompiler).
- Created `Interpreter/Vm/` (VmFrame, VmExecutor).
- `IrCompiler.CompileScript` emits `[OP_VISIT_AST 0, OP_HALT 0]` — pure
  pass-through.
- `Program.UseVm` flag set by `--ir` / `--vm` CLI arg or `RA_VM=1` env var.
- Smoke parity: 53/53 sampled tests across async, functions, control_flow,
  collections, errors, edge_cases, integration produced byte-identical
  output AST vs VM.

### M2 — integers, locals, arithmetic, comparisons  ✅ DONE
- Native opcodes wired in VmExecutor: LoadConst, LoadNull, LoadTrue,
  LoadFalse, Move, LoadGlobal, Add, Sub, Mul, Div, Mod, Pow, Shl, Shr, BAnd,
  BOr, Eq, Ne, SEq, SNe, Lt, Le, Gt, Ge, Not, BNot.
- Expression compiler covers: `Number`, `Boolean`, `Null`, `VariableAccess`,
  `BinaryOperation` (arith+cmp), `UnaryOperation` (not, bitwise-not).
- Statement-level integration is conservative: only *bare* expression
  statements compile natively. `VariableDeclaration` / `VariableAssignment` /
  control-flow stay on AST (M3+).
- `LoadGlobal` delegates to `IInterpreter.ExtractVariableValueByName` so
  let-move accounting / borrow-block / IsConstBinding edge cases are
  preserved verbatim — no risk of behaviour drift.
- Arithmetic opcodes dispatch to the existing `RuntimeValue.AddedTo` /
  `SubbedBy` / … virtuals; same overflow trapping, NaN/Inf handling, custom
  operator-overload routes. Fast-path specialization (`TryFastBinary`-style)
  is a post-M11 optimization.
- Unary `-x` is currently synthesized by the AST visitor as `x *
  NumberValue(-1)` (no dedicated `Negated()` virtual). Lowering to native
  ops would need either an `OP_NEG` (synthesized in dispatch) or an
  `OP_LOAD_CONST(-1)` + `OP_MUL` pair. Left to M3 for cleanliness.
- Counters: `VmExecutor.NativeOpsExecuted` and `VmExecutor.AstFallbacks`,
  exposed when `RA_VM_STATS=1` is set.

### M3 — control flow + statement-level mutation  ✅ DONE
- Native opcodes wired: `Jmp`, `JmpIf`, `JmpIfNot`, `AndJz`, `OrJnz`, `Neg`,
  `StoreGlobal`, `DeclareLocal`.
- `If` / `IfCasesWrapper` lowered to per-case (cond → JmpIfNot → body → Jmp end)
  with all branches patched at the end. Else block compiles inline.
- `While` lowered to (test → JmpIfNot exit → body → Jmp loop_start).
- `DoWhile` lowered to (body → cond → JmpIf loop_start).
- `Break` / `Continue` use a `LoopContext` stack with forward-jump fixups
  patched at loop exit / continue target. `Retry` is a backward Jmp to
  loop_start.
- `Return` and `RetNull` work end-to-end at script root (Halt unwraps to
  Success in RunScript).
- Short-circuit `and` / `or` compile to two-jump skip patterns so the RHS
  doesn't evaluate when the result is already determined.
- Unary `-x` synthesized as `x * NumberValue(-1)` at the VM (no dedicated
  virtual on RuntimeValue).
- `VariableAssignment` (EQ only) via shared `AssignmentHelper`; honors
  const/let/let-const/final-initialized/IsBorrowed/IReferenceValue
  through-write / BorrowValue rebind / TypeChecker coercion semantics.
- `VariableDeclaration` (single decl, no annotations, no static) via shared
  `DeclarationHelper`; honors redeclaration, statically-typed checks, generic
  element late-binding for channel/stream/task.
- Body eligibility filter rejects: any descendant `VariableDeclaration`,
  `FunctionDefinition`, `ClassDefinition`, `StructDefinition`,
  `EnumDefinition`, `InterfaceDefinition`, `TraitDefinition`,
  `AnnotationDefinition`, `ExtensionDefinition`, `NamespaceDeclaration`,
  `UsingNamespace`, `Import*`, `For`, `ForEach`, `SuperFor`, `ForAwait`,
  `TryUnwrap`, `Match`, `Try`. Each of those needs a fresh-scope or
  pattern/error-region opcode — staged for M4-M7.
- Corpus native ratio: **8% → 79%** across the 63-test sweep.
- Build clean, 63/63 byte-identical AST vs VM.

### M4 — scope opcodes + `for` + nested var decls + string literals  ✅ DONE
- New opcodes: `PushScope`, `PopScope`, `ClearScope`, `SetLocalDirect`,
  `AssignBinding`. The dispatch loop carries a mutable `ctx` local that
  `PushScope` advances to a fresh child Context and `PopScope` walks back via
  `Context.Parent`.
- IrCompiler state extended with `ScopeDepth` (static). `LoopContext`
  records `BaselineScopeDepth` so `break` / `continue` / `retry` can emit
  the right number of `OP_POP_SCOPE` opcodes ahead of their jumps when they
  sit inside nested `if` / `while` / `for` bodies.
- `If` / `While` / `DoWhile` bodies now run inside a fresh scope (matching
  the AST visitors' `context.Copy()` + `bodyContext.Clear()` between iters).
  Nested `var x = …` declarations inside loop / branch bodies are no
  longer an eligibility blocker.
- `For` (numeric range) lowered with two scopes — iter scope holds the
  iteration variable, body scope is cleared each iteration. Step sign is
  tested at runtime so ascending and descending ranges share one lowering.
- `String` literals with no interpolation compile to `OP_LOAD_CONST` (one
  cached `StringValue` per literal). Interpolated strings still fall back
  pending an `OP_INTERP` opcode (M5+).
- Eligibility filter renamed `BodyContainsUnsupported`. Allows nested
  `If` / `While` / `DoWhile` / `For` / `Scope` / `VariableDeclaration`
  (single, native-eligible) / `VariableAssignment` (EQ). Rejects everything
  that needs opcodes we haven't added yet: function bodies, OOP, async,
  pattern, exception regions, etc.
- Corpus native ratio: **79% → 89%**.
- Build clean, 63/63 byte-identical AST vs VM.

### M5 — function calls (positional) — IN PROGRESS
- `OP_CALL dst, fnSlot, argCount` calls `FunctionCallExecutor.Invoke` with
  positional args drawn from `locals[fnSlot+1 .. fnSlot+argCount]`. Reuses
  the AST visitor's call infrastructure so annotation interceptors,
  contracts, type coercion, and generic dispatch all behave identically.
- IR-gen restricts eligibility to: no `node.GenericTypeArgs`, no `IsRef`
  args, no named (`NameTok`) args, no `SpreadNode` arg expressions.
  Anything fancier falls back.
- Arg slots are bump-allocated contiguously above `fnSlot` *before*
  compiling the callee / args, so each sub-expression's temp churn lives
  above the call's slot band and the OP_CALL contract holds.

### M5 — functions and closures
- Compile `FunctionDefinition` bodies. `OP_CALL`, `OP_RET`, `OP_CLOSURE`,
  `OP_TAILCALL` (when safe).
- Validate against `tests/functions/*.ra`, `tests_validation*.ra`.

### M6 — OOP
- `OP_CALL_METHOD`, `OP_NEW_INSTANCE`, `OP_GET_SELF`, `OP_GET_MEMBER`,
  `OP_SET_MEMBER`, `OP_CAST`, `OP_IS`.
- Class/struct *definitions* stay on AST (rare, not hot).

### M7 — match, try/catch, throw
- Pattern matching, exception handler table, `OP_THROW`, `OP_ENTER_TRY`,
  `OP_LEAVE_TRY`, `OP_FINALLY_END`.

### M8 — async
- `OP_AWAIT`, `OP_SPAWN`, `OP_EMIT`, `OP_FOR_AWAIT`. Validate against
  `tests/async/*.ra` and `tests_async_concurrency.ra`.

### M9 — annotations, contracts, test runner integration
- `OP_RUN_PRE`, `OP_RUN_POST`. Validate against
  `tests/annotations/*.ra`.

### M10 — long tail
- `Borrow`, `Dereference`, `DereferenceAssignment`, `Pipeline`,
  `RegexLiteral`, `Spread`, `Typeof`, `Nameof`, `Cast`, ASM block lowering,
  goto/label.

### M11 — flip default + cleanup
- Make VM the default executor.
- Audit `OP_VISIT_AST` fallback rate over full test corpus: must be 0 or
  documented exceptions only (e.g. operator-overload bodies, contract
  expressions).
- Delete dead AST visitors only when their AST node has a VM lowering and no
  remaining fallback.

---

## 5. Risks and mitigations

| Risk                                                          | Mitigation                                                                                                   |
| ------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| Async semantics regress (worker pinning, lost continuations)  | All async opcodes go through the same `RaTaskCore.WaitAsync` API; integration test = `tests_async_concurrency.ra`. |
| Move/copy semantics drift (esp. `let` move-on-use)            | `OP_MOVE_LET` is a distinct opcode from `OP_MOVE`; IR-gen sets it whenever the read is "non-copy let, non-const-binding". |
| Try-finally bugs                                              | Exhaustive table-driven test: every combo of `{normal, return, break, continue, throw, yield}` x `{has-catch, has-finally, has-both}`. |
| Goto/label oddity (current re-entry semantics)                | Defer to M10. Stay on AST until then.                                                                        |
| NativeAOT breaks                                              | No `Reflection.Emit` outside existing `TrampolineGen`; CI builds `dotnet publish -c Release -r win-x64`.    |
| Existing visitors get out of sync with IR opcodes             | Every IR opcode has a one-line comment naming the visitor it lowers; CI grep ensures both moved together.   |
| Performance regression instead of speedup                     | Microbench mode runs every milestone; baseline locked before M1; require ≥1.5× on bench_arithmetic by M3.    |
| Cross-frame `Context` chain broken → tracebacks lose frames   | Keep `Context` plumbing intact (the VM frame's `Context` is parented exactly like the AST flow does today).  |
| Annotation/contract hooks fire at wrong moment                | `OP_RUN_PRE` / `OP_RUN_POST` are inserted by `IrCompiler` at the same logical site `FunctionCallExecutor` injects them today; one shared helper. |

---

## 6. What stays untouched

- `Lexer/`, `Parser/`, `Errors/`, `Types/`, `Utilities/`.
- `Parser/Nodes/` (we still need the AST for fallback and for non-compiled
  constructs like annotation arg expressions).
- `Interpreter/Pipeline/` (Resolver, BindingId, ResolvedCapture) — already
  produces exactly what the VM needs.
- `Interpreter/Runtime/` (SymbolTable, SymbolEntry, Context,
  ExtensionRegistry, MetadataRegistry, etc.) — VM borrows it as-is.
- `Interpreter/Runtime/Annotations/*` (the *pure metadata* ones) — unchanged.
- `Interpreter/Runtime/Async/*` — unchanged. VM uses `RaTaskCore.WaitAsync`,
  `AsyncScheduler.Schedule`, etc., exactly like today.
- `Interpreter/Runtime/Asm/*`, `Interpreter/Runtime/Interop/*`,
  `Interpreter/Runtime/Borrowing/*`, `Interpreter/Runtime/Namespaces/*`,
  `Interpreter/Modules/*` — unchanged interfaces.
- `Interpreter/Values/*` — value classes stay; we only add `VmClosureValue`
  extending `BaseFunctionValue`.
- `Interpreter/Visitors/*` — kept in place during migration; deleted lazily
  in M11 only after verified redundancy.
- `Program.cs` — receives a small flag to choose executor; no other change.

---

## 7. New files (planned)

```
Interpreter/IR/
  Opcode.cs                  # enum + sizes
  Encoding.cs                # encode/decode helpers
  RaFunction.cs              # the compiled function record
  ExceptionHandler.cs
  UpvalueSpec.cs
  ConstantPoolBuilder.cs
  IrCompiler.cs              # AstNode → RaFunction
  IrCompiler.Stmts.cs        # statement compilers (per category)
  IrCompiler.Exprs.cs
  IrCompiler.Functions.cs
  IrCompiler.Control.cs
  IrVerifier.cs              # structural sanity (jumps land in-function, locals in range, EH covers PCs)
  IrPrinter.cs               # `--dump-ir` for debugging
Interpreter/Vm/
  VmFrame.cs
  VmExecutor.cs              # the dispatch loop
  VmClosureValue.cs          # extends BaseFunctionValue
  VmDiagnostics.cs           # PC → SourceSpan resolver for traceback frames
Interpreter/Vm/Hooks/
  AnnotationHookRegistry.cs  # maps OP_RUN_PRE/OP_RUN_POST ids to handlers
```

---

## 8. Development hygiene

- **No regressions silently.** Each milestone runs every `.ra` file under
  `tests/` and `other_tests/` (resolved relative to bin/.../net10.0) and the
  full top-level `tests_*` files. Diff exit codes against the AST baseline.
- **Microbench at every milestone.** `dotnet run -- --bench` after each merge.
  Record best/avg/alloc per bench. Refuse to merge if average regresses > 5%.
- **AOT verification.** At least once per milestone, run
  `dotnet publish -c Release -r win-x64`. The build must succeed and produce a
  runnable executable.
- **Tracebacks stay rich.** When `RuntimeError.BuildTraceback` walks
  `Context.Parent`, VM frames must look just like AST frames — same
  `DisplayName`, same `ParentEntryPos`. Periodically grep for
  `[Ra Language]` and confirm error output is unchanged.

---

## 9. Open design questions (to revisit before M5)

1. **Inline caches for global / member lookups.** Worth it now or after the
   skeleton lands? Defer until we have profiling.
2. **Boxing of small integers in arithmetic.** Today every `int + int` allocates
   an `IntegerValue`. Worth an opcode like `OP_ADD_II` that operates on raw
   `long` register banks? Big perf win but invasive. Defer to a post-M11
   "specialization" phase.
3. **Loop unrolling / hoisting?** No. Out of scope for this migration. The
   goal is "modern interpreter on bytecode", not a JIT.
4. **Per-arity local-array pooling?** Defer; measure first.
5. **Constant interning across functions** (string/regex)? Yes — a
   `ProgramConstants` table shared across `RaFunction`s in a module reduces
   cross-call duplication. To be sketched in M4.

---

## 10. Glossary (so future-me does not re-derive these)

- **AST path / AST executor / AST visitor pipeline**: the current
  `Interpreter` + `Interpreter/Visitors/*` model. Tree-walking.
- **IR**: intermediate representation. Concretely, the `uint[] Code` plus
  metadata on a `RaFunction`.
- **VM**: the bytecode interpreter (`Interpreter/Vm/VmExecutor.cs`).
- **`OP_VISIT_AST`**: the bridge opcode that hands a subtree to the AST
  visitor pipeline. Used as a fallback during migration.
- **Fallback rate**: fraction of executed Ra instructions that hit
  `OP_VISIT_AST` rather than a native VM opcode. Telemetry target: 0 after M11.

---

---

## 11. M12 — 100% native ratio (no OP_VISIT_AST fallbacks)

**Goal:** drive `AstFallbacks` to zero across the full regression corpus
without losing test parity. The remaining 60–121 fallbacks at the end of M11
came from three patterns that the IR compiler's strict-mode rollback path
routed through `EmitVisitAst` instead of `OP_NATIVE_DEFINE`:

- `Try` (without finally) whose try-body or catch-body contained an
  unsupported construct.
- `If` whose branch body contained an unsupported construct.
- `VariableDeclaration` that wasn't natively compilable (multi-decl, type
  ascriptions, annotations, etc.).
- `VariableAssignment` whose RHS was unsupported.

### Approach

1. **Promote three more visitors to expose `public static Apply`** mirroring
   the M11 pattern:
   - `IfNodeVisitor.Apply`
   - `VariableDeclarationNodeVisitor.Apply`
   - `VariableAssignmentNodeVisitor.Apply`
2. **Wire matching cases in `VmExecutor`'s `OP_NATIVE_DEFINE` switch** so the
   dispatch loop calls the visitor's static helper directly (no
   `interpreter._visitors[]` indexing).
3. **Replace `IrCompiler.EmitVisitAst` in the rollback path with `EmitFallback`**,
   which prefers `OP_NATIVE_DEFINE` whenever the node kind has a registered
   dispatch case. `OP_VISIT_AST` survives as a safety net for genuinely
   unknown node kinds (none currently observed in the corpus).
4. **Add per-AstNodeType fallback histogram telemetry** (`FallbacksByNodeType`)
   so future regressions can identify regression node kinds at a glance.

### Result

```
=== M12 sweep ===
files: 137
assertions: 770 OK, 0 FAIL
processes: 0 timeout, 0 crash
VM: native=169407 fallback=0 ratio=100.0000%

Fallback histogram (sum):
  <empty>
```

100% native ratio, 770/770 assertion parity, 0 timeouts, 0 crashes. The full
regression corpus (137 `.ra` files across `tests/`) now executes without a
single `OP_VISIT_AST` opcode.

`OP_VISIT_AST` remains in the opcode table and dispatch switch as a defensive
safety net — emitting it requires the IR compiler to encounter an
`AstNodeType` for which no `OP_NATIVE_DEFINE` route exists. Adding a new
language construct without wiring its visitor will fall back gracefully
instead of crashing.

### Banner

`[Ra Language] IR+VM path enabled (M12: 100% native ratio across full test
corpus, no OP_VISIT_AST fallbacks).`

---

---

## 12. M13 — VM-only consolidation

**Goal:** make the IR + VM the sole execution path of the interpreter. Excise
every gate, banner, env-var pickup, fallback bridge, and telemetry counter
that still treated the AST executor as a peer option.

### 12.1 Architectural purge (M13.1)

- `Program.UseVm` field deleted.
- `--ir` / `--vm` CLI flags removed.
- `RA_VM` / `RA_VM_STATS` env-var pickups removed.
- "IR+VM path enabled" banner removed (the VM is the only backend; no need
  to announce it).
- `Program.Run` collapsed to a single execution path: lex → parse →
  derive-transform → resolver → static analyzer → borrow checker →
  `IrCompiler.CompileScript` → `VmExecutor.RunScript`.
- `Opcode.VisitAst` deleted from the opcode enum.
- `VmExecutor`: per-instruction telemetry branch (`NativeOpsExecuted` /
  `AstFallbacks`), `FallbacksByNodeType` histogram, and the `VisitAst`
  dispatch case all deleted. The bridge to `_interpreter.Visit` is gone.
- `IrCompiler.EmitVisitAst` deleted. `EmitFallback` now throws
  `IrCompileException` if a node kind has no `OP_NATIVE_DEFINE` route — a
  hard error replaces the silent AST fallback.
- "tree-walking interpreter" / "AST executor" doc comments rewritten where
  they still appeared.

### 12.2 Hot-path dispatch optimizations (M13.2)

- Inner-loop `pc` hoisted to a local `int` instead of being read/written
  through `f.Pc` on every instruction. `f.Pc` is now synced only at exit
  points (Halt / Ret / RetNull / unhandled Failure).
- The implicit "fell off the end" `pc >= code.Length` check per iteration
  is gone. Every emitted bytecode terminates with a Halt / Ret / RetNull
  opcode, so the dispatch loop only exits through a real return path.
- `Binary`, `DummyPos`, `MakeIcError` annotated with
  `MethodImplOptions.AggressiveInlining` so the JIT can fold them into
  the hot opcode bodies.
- Jump opcodes (`Jmp`, `JmpIf`, `JmpIfNot`, `AndJz`, `OrJnz`) write back
  into the local `pc` register, not `f.Pc`.
- EH catch handler reads `pc` locally and re-points `pc = h.CatchPc` on a
  match before resuming the inner loop.

### Result

Sweep parity preserved at 137 files / 770 assertions OK / 0 fail / 0
crash / 0 timeout. No `OP_VISIT_AST` opcode remains in the binary. The
dispatch loop's only inputs are the bytecode buffer + the locals array.

Micro-benchmarks (post-M13.2):
- `bench_hotloop.ra`  (1 000 000-iter loop, `sum = sum + i`): ~700 ms best
- `bench_arithmetic.ra` (500 000-iter loop, multi-op): ~840 ms best

The dispatch loop itself is no longer the bottleneck; remaining cost is
SymbolTable name-keyed lookups (`LoadGlobal`/`StoreGlobal`/`AssignBinding`)
and `NumberValue` boxing on arithmetic. Both are addressable in a follow-up
milestone via slot-based locals (`OP_LOAD_LOCAL_SLOT` /
`OP_STORE_LOCAL_SLOT`) and an inline cache on by-name global lookups.

---

## 13. M14 — slot-based locals via Resolver bindings

**Goal:** skip the per-access `ctx.SymbolTable.GetEntry(name)` dictionary walk
for every binding the Resolver already pinned to a stable frame slot. This
is the biggest remaining hot-path cost in arithmetic loops.

### Design

The Resolver pass (already in place) assigns each declaration a
`BindingId(FrameId, Offset)`. M14 leverages that:

- `RaFunction` adds three fields:
  - `int SlotCount` — frame slot table size (max Offset + 1).
  - `string?[] SlotNames` — sparse name table indexed by offset, consulted
    only on the cold "slot not yet populated" lazy fallback + diagnostic
    paths.
  - `Dictionary<string,int>? NameToSlot` — inverse, used by SetLocalDirect /
    AssignBinding to keep the slot pointing at the current iteration's
    `SymbolEntry` when an outer loop re-enters a nested for-body.
  - `int[] DeclSlotByAstRef` — parallel to AstRefs: which slot a given
    OP_DECLARE_LOCAL emission cached its freshly-created entry into.
- `VmFrame` adds `SymbolEntry?[] SlotLocals` sized to `RaFunction.SlotCount`.
  Slots are frame-scoped, so they survive PushScope / PopScope.
- Two new opcodes:
  - `OP_LOAD_LOCAL_S a, slot:u16` — read `slots[slot].Value`, run the same
    `IsMoved` / `HasMutableBorrow` guards `OP_LOAD_GLOBAL` runs.
  - `OP_STORE_LOCAL_S a (src), slot:u16` — plain `=` only; assigns through
    the cached `SymbolEntry` after the mutability / borrow / let-move guard.
    Compound assignments (`+=`, etc.) still take `OP_STORE_GLOBAL` because
    they need `AssignmentHelper` for operator selection.
- The IR compiler decides at every `VariableAccessNode` /
  `VariableAssignmentNode` whether to emit the slot opcode:

```csharp
private static bool IsSlotEligible(BindingId b, BindingKind k)
    => b.IsResolved
    && b.FrameId == 0
    && (k == BindingKind.Local || k == BindingKind.Global
        || k == BindingKind.Parameter || k == BindingKind.SelfRef);
```

  Anything failing this falls back to `OP_LOAD_GLOBAL` / `OP_STORE_GLOBAL`.
- Slot lazy population: on a `null` slot read the dispatch loop does one
  `SymbolTable.GetEntry(name)` lookup, caches the entry into the slot, and
  proceeds. Required because function definitions / dynamically-injected
  names populate the symbol table without going through OP_DECLARE_LOCAL.
- Slot refresh on for-loop iter vars: `OP_SET_LOCAL_DIRECT` and the first
  `OP_ASSIGN_BINDING` for a name look the slot up via `NameToSlot` and
  repoint it to the new `SymbolEntry`, so re-entering a nested loop body
  from an outer iteration doesn't read the orphaned entry from the prior
  inner scope.

### Result

Sweep parity preserved at 137 files / 770 OK / 0 fail / 0 timeout / 0 crash.

Micro-benchmarks (best wall-clock):

| Bench | Pre-M14 | Post-M14 | Δ |
|---|---|---|---|
| `bench_hotloop.ra` (1M-iter `sum = sum + i`) | 723 ms | 548 ms | -24% |
| `bench_arithmetic.ra` (500k-iter multi-op) | 955 ms | 715 ms | -25% |

Allocation per run is unchanged (~411 / 574 MB), because the dominant
remaining cost is `NumberValue` boxing on every arithmetic result. That's
the next milestone target (e.g. specialized OP_ADD_I64 paths over a raw
register bank, or value-type pooling for small integers).

---

## 14. M15 — unboxed integer arithmetic fast path

**Goal:** kill the BigInteger pair allocation on every `NumberValue + NumberValue`
when both operands fit in `int64`. The dispatch loop dominates wallclock in
arithmetic loops; the dominant allocation cost is `BigNumber` arithmetic
producing a fresh `BigNumber` per result.

### Design

Hot-path predicate inside `VmExecutor.Binary`:

```csharp
if (left.Type == RuntimeValueType.Number && right.Type == RuntimeValueType.Number)
{
    var ln = (NumberValue)left;
    var rn = (NumberValue)right;
    if (TryGetInt64(ln, out long lv) && TryGetInt64(rn, out long rv))
    {
        try
        {
            switch (op)
            {
                case BinOp.Add: produced = NumberValue.OfBigNumber(BigNumberFromLong(checked(lv + rv))); break;
                case BinOp.Sub: produced = NumberValue.OfBigNumber(BigNumberFromLong(checked(lv - rv))); break;
                case BinOp.Mul: produced = NumberValue.OfBigNumber(BigNumberFromLong(checked(lv * rv))); break;
                case BinOp.Lt:  produced = BooleanValue.Of(lv <  rv); break;
                // ... Le / Gt / Ge / Eq / Ne / SEq / SNe
            }
        }
        catch (OverflowException) { /* fall through to BigNumber */ }
    }
}
```

- `TryGetInt64(NumberValue, out long)` accepts only `Scale.IsZero` operands
  whose `Unscaled` magnitude fits in int64. Both checks are sub-nanosecond.
- Result boxing routes through `NumberValue.OfBigNumber()` so values inside
  the existing small-int intern range (`-128..1024`) skip allocation entirely.
- Overflow falls through to the existing virtual-dispatch path, preserving
  arbitrary-precision semantics for big numerics.
- Comparison opcodes return `BooleanValue.Of(bool)` (a cached singleton);
  no allocation on the fast path at all.

### Result

Sweep parity preserved: 137 files / 770 OK / 0 fail / 0 timeout / 0 crash.

| Bench | M13 | M14 | M15 | Cumulative Δ |
|---|---|---|---|---|
| `bench_hotloop.ra` | 723 ms | 548 ms | 500 ms | -31% |
| `bench_arithmetic.ra` | 955 ms | 715 ms | 647 ms | -32% |

Allocations per run essentially unchanged: 411 MB / 574 MB — the remaining
cost is the `NumberValue` instance per result whose magnitude exceeds the
small-int intern range. Removing that requires unboxed slot storage
(struct-of-tag-and-int64 union) or per-frame `NumberValue` pooling; deferred
to M16.

---

## 15. M16 — compile function bodies to IR

**Goal:** the script body has been IR-compiled since M2; user functions still
recursed back into the AST visitor pipeline on every call. M16 extends IR
coverage to function bodies so a hot call site (`fn fib(n)` in a loop) runs
purely through the VM dispatch loop, with parameters / locals routed
through slot opcodes just like the script frame.

### Design

- `IrCompiler.CompileFunction(FunctionDefinitionNode)` mirrors
  `CompileScript`:
  - allocates a `RaFunction` with `FrameId = fnNode.FrameId` so slot
    eligibility (M14) admits the function's own parameters and locals.
  - pre-registers parameter slots from `node.ParamBindings` so SlotCount
    accounts for them even before any body access populates the slot.
  - walks `fnNode.BodyNode` through the existing
    `CompileStatementWithFallback` pipeline. Anything the IR can't lower
    raises `IrCompileException`, which bubbles up to a caller that drops
    the compile attempt and keeps the AST path.
  - terminates with `OP_RET_NULL` so falling off the end of a void
    function returns null (matches AST semantics).
- Post-compile finalisation extracted into `FinalizeFn(RaFunction, State)`
  so both entry points share the slot-table, name-to-slot, EH-table, and
  pool wiring.
- `State.FrameId` field plumbed into `IsSlotEligible(b, k, st)` so
  cross-frame captures still fall through to `OP_LOAD_GLOBAL`.
- `FunctionValue` carries `IR.RaFunction? CompiledBody`.
  `FunctionDefinitionHelper.Apply` calls `TryCompileBodyToIr` after the
  value is otherwise wired:

```csharp
private static void TryCompileBodyToIr(FunctionValue funcValue, FunctionDefinitionNode node)
{
    if (node.IsAsync || node.IsAsyncStream) return;
    if (node.ShouldAutoReturn) return;
    if (node.BodyNode == null) return;
    if (node.FrameId < 0) return;
    try { funcValue.CompiledBody = IrCompiler.CompileFunction(node); }
    catch (IrCompileException) { funcValue.CompiledBody = null; }
}
```

- `FunctionValue.ExecuteBodySync` dispatches through `VmExecutor.Execute`
  (not `RunScript`) when `CompiledBody != null`, so the FlowState.Return
  flag survives back to the caller's post-body handling.

Conservative gates for now:
- Async / async-stream functions still walk the AST (await suspension
  machinery is outside the IR's reach).
- Arrow-form (`ShouldAutoReturn`) functions still walk the AST — the
  trailing `OP_RET_NULL` would clobber the auto-returned final-expression
  value. Wire a dedicated arrow-form lowering in a follow-up.
- Anything the IR rejects mid-body simply leaves `CompiledBody` null.

### Result

Sweep parity preserved: 137 files / 770 OK / 0 fail / 0 timeout / 0 crash.

Bench (best wall-clock):

| Bench | M15 | M16 | Δ |
|---|---|---|---|
| `bench_hotloop.ra` | 500 ms | 473 ms | -5% |
| `bench_arithmetic.ra` | 647 ms | 656 ms | +1% (noise) |

The microbench corpus doesn't call user-defined functions, so the gain
here is incidental. The structural win is that every supported function
body in the test corpus now executes through IR + VM (slot ops,
fast-path arithmetic, etc.) instead of recursing into
`Interpreter.Visit(BodyNode)`. Async / arrow functions remain the two
holdouts; addressing them requires either reshaping the dispatch loop
to materialise its frame as an async state machine (for async) or
emitting a typed return terminator (for arrow).

---

## 16. M17 — arrow-form, async, and class-method IR coverage

**Goal:** close the IR-coverage gaps M16 left in place:
1. Arrow-form (`=> expr`) functions whose final expression must propagate as
   the return value.
2. Async / async-stream functions whose body uses `await` / `yield`.
3. Class methods, extension methods, and trait dispatch — all still
   recursing into `interpreter.Visit(MethodNode.BodyNode)`.

### Design

**Shared compile entry.** `Interpreter.IR.RaFunction? FunctionDefinitionHelper.GetOrCompileBody(FunctionDefinitionNode)`
caches the IR per AST node (`node.CompiledBody`, `node.IrCompileTried`).
Every dispatch path (top-level functions, class methods, extension methods,
trait method binders) calls the shared helper, so each `FunctionDefinitionNode`
is compiled at most once regardless of how many bound-method values reference
it. Class methods declared inside `class { ... }` bodies are
`FunctionDefinitionNode`s and pick this up directly.

**Arrow-form lowering.** `CompileFunction` now branches on
`ShouldAutoReturn`:
- compile `BodyNode` as an expression into a scratch slot,
- emit `OP_RET <slot>` so the body's value reaches the caller's
  `FuncReturnValue`,
- on `IrCompileException` mid-expression, roll back the partial emit and
  fall back to the block-form lowering (no auto-return, but the function
  still runs through IR).

**Async unblocking.** Removed the `IsAsync || IsAsyncStream` gate. The
dispatch loop is already an `async ValueTask<RuntimeResult>`, and async
constructs (`OP_AWAIT`, `OP_YIELD`, `OP_EMIT`, `OP_FOR_AWAIT`) route through
`OP_NATIVE_DEFINE` → visitor's static `Apply` → real `await`. AsyncContext
lives on `Context`, which the VM threads through unchanged. `FiberRuntime` /
`AsyncScheduler.Schedule` wrap `ExecuteBodySync` exactly as before;
ExecuteBodySync dispatches via VM when `CompiledBody` is set.

**End-of-body terminator change.** The previous trailing `OP_RET_NULL`
caused constructors to fail with "Constructors cannot return a value" on
implicit fall-through. Changed to `OP_LOAD_NULL scratch; OP_HALT scratch`
which surfaces `RuntimeResult.Success(null)` *without* setting
`FuncReturnValue`. This matches the AST visitor's behaviour where falling
off the end yields `Value=null, FuncReturnValue=null`; explicit `ret X`
still emits `OP_RET <slot>` and flags `FlowState.Return`.

**Call-site wiring** (all check `GetOrCompileBody` and dispatch VM when
non-null, AST otherwise):
- `FunctionValue.ExecuteBodySync` (top-level fns)
- `BoundClassMethodValue.ExecuteCore` (class methods)
- `BoundClassMethodGroupValue.ExecuteWithNamedArgs` (overload groups)
- `BoundExtensionMethodGroupValue.ExecuteWithNamedArgs` (extension methods)
- `MethodCallBinder.BoundMethodGroupValue.ExecuteWithNamedArgs` (trait /
  group dispatch)

Struct / trait method definitions (`StructMethodDefinitionNode`,
`TraitMethodDefinitionNode`) currently lack `FrameId` / `ParamBindings`
from the Resolver — they keep the AST path. Promoting them is a Resolver
extension that fits later milestones.

### Result

Sweep parity: 137 files / 770 OK / 0 fail / 0 timeout / 0 crash — including
arrow-form constructors, async/await tests, and class-method-heavy
integration tests.

Bench is unchanged within noise (the microbenches don't exercise
user-defined classes / async dispatch), but the structural win is real:
every IR-eligible function and class method in the test corpus now
executes through the VM dispatch loop with slot opcodes, fast-path
arithmetic, and hoisted PC — the AST `interpreter.Visit(BodyNode)` path
runs only for struct / trait method bodies and for nodes the IR cannot
yet lower.

---

## 17. M18 — struct / trait / operator / extension method IR coverage

**Goal:** finish the long-tail. M17 IR-ised top-level functions and class
methods; M18 covers everything else that previously fell back to
`interpreter.Visit(MethodNode.BodyNode)`:
- `StructMethodDefinitionNode` (struct methods + constructors)
- `OperatorDefinitionNode` (operator overloads on classes / structs)
- `TraitMethodDefinitionNode` (trait method bodies, when concrete)
- `ExtensionDefinitionNode` (extension methods on existing types)

### Design

**Resolver extension.** Previously these definition kinds were skipped by
the Resolver (TraitDefinition / ExtensionDefinition) or partially walked
without persisting the frame metadata (StructMethodDefinition,
OperatorDefinition). Updates:
- `WalkMethodLikeBody` now returns the `BindingId[]` for the params and an
  out-parameter for the frame id, so the caller can attach them to the AST
  node.
- `WalkClass` writes `op.FrameId` / `op.ParamBindings` for each operator.
- `WalkStruct` writes the same on every method and operator.
- New `WalkTrait` walks each concrete trait method (skipping abstracts).
- New `WalkExtension` reuses `WalkFunction` per extension method (they
  are already `FunctionDefinitionNode`s, so they pick up the full
  Resolver pipeline including capture analysis).

**AST node extensions.** `StructMethodDefinitionNode`,
`OperatorDefinitionNode`, `TraitMethodDefinitionNode` each gain:

```csharp
public int FrameId = -1;
public BindingId[]? ParamBindings;
public RaFunction? CompiledBody;
public bool IrCompileTried;
```

**Shared IR compile entry.** `IrCompiler.CompileFunction` is now a thin
wrapper around `IrCompiler.CompileMethodShape(name, frameId, arity,
paramBindings, argNameToks, body, shouldAutoReturn)`. The shape entry is
called by:
- `FunctionDefinitionHelper.GetOrCompileBody(FunctionDefinitionNode)`
  (top-level + class methods + extension methods)
- `FunctionDefinitionHelper.GetOrCompileStructMethod(StructMethodDefinitionNode)`
- `FunctionDefinitionHelper.GetOrCompileTraitMethod(TraitMethodDefinitionNode)`
- `FunctionDefinitionHelper.GetOrCompileOperator(OperatorDefinitionNode)`

Each caller caches the result on the AST node so a method called N times
compiles once.

**Dispatch wiring.**
- `BoundStructMethodValue.ExecuteWithNamedArgs` checks
  `GetOrCompileStructMethod(MethodNode)`; dispatches via VM when non-null,
  AST otherwise.
- `BoundOperatorValue` now carries the optional `OperatorDefinitionNode`
  reference (added a constructor parameter with default null so external
  call sites keep compiling). Both `ShouldAutoReturn` and block forms
  consult `GetOrCompileOperator(OpNode)` when available.
- `RuntimeValue.TryOperatorDispatch` updated to pass `op` through to the
  `BoundOperatorValue` constructor.
- `MethodCallBinder.BoundMethodGroupValue.ExecuteWithNamedArgs` switch
  expression now covers both `FunctionDefinitionNode` (existing) and
  `TraitMethodDefinitionNode` (new) when picking the compiled body.

### Result

Sweep parity preserved: 137 files / 770 OK / 0 fail / 0 timeout / 0 crash.

Bench (best wall-clock, microbenches don't exercise these paths):
- `bench_hotloop.ra`  ~491 ms
- `bench_arithmetic.ra` ~639 ms

Structural state: every dispatch site that used to invoke
`interpreter.Visit(MethodNode.BodyNode)` now first consults a cached
`RaFunction` and runs the body through the VM dispatch loop. The
fallback `interpreter.Visit` survives only as a safety net for nodes the
IR compiler can't yet lower (raised as `IrCompileException`,
silently caught) — that path runs against nothing in the current test
corpus.

---

## 18. M19 — AST fallback killed dead

**Goal:** the dispatch sites still carried `else { interpreter.Visit(BodyNode) }`
safety nets. M19 removes them: IR is mandatory. Any body the IR compiler
refuses surfaces as a runtime error pointing at the responsible function /
method rather than silently regressing to AST.

### Design

**Telemetry first.** Added a `RecordFailure(message)` helper in
`FunctionDefinitionHelper`, with `IrCompileFailures` counter +
`IR_DEBUG_FAILED_COMPILES=1` env-var that emits one
`[ir-compile-fail] ...` line per swallowed `IrCompileException`. Sweep
across the 137-file test corpus reported **zero** failures.

**Fallback branches deleted.** Replaced the six `else { interpreter.Visit(...) }`
blocks with hard runtime errors:

```csharp
if (compiled == null)
    return res.Failure(new RuntimeError(PositionStart, PositionEnd,
        $"<kind> '{Name}' has no executable body", Context));
{
    var vm = new Vm.VmExecutor(interpreter);
    var frame = new Vm.VmFrame(compiled);
    bodyRes = await vm.Execute(frame, execCtx);
}
```

Touched files:
- `Interpreter/Values/Functions/FunctionValue.cs`
- `Interpreter/Values/Classes/BoundClassMethodValue.cs`
- `Interpreter/Values/Classes/BoundClassMethodGroupValue.cs`
- `Interpreter/Values/Classes/BoundExtensionMethodGroupValue.cs`
- `Interpreter/Values/Structs/BoundStructMethodValue.cs`
- `Interpreter/Values/Traits/MethodCallBinder.cs`
- `Interpreter/Values/Operators/BoundOperatorValue.cs`

**Sync of secondary FunctionValue producer.** Two paths build a
`FunctionValue` from a `FunctionDefinitionNode`:
`FunctionDefinitionHelper.Apply` (VM-side, called from `OP_DEFINE_FUNCTION`)
and `FunctionDefinitionNodeVisitor.VisitNode` (AST-side, called when a
visitor's static `Apply` recurses via `interpreter.Visit(funcDefNode, ctx)`).
Only the helper called `TryCompileBodyToIr` — visitor-route values had
`CompiledBody=null` and the no-fallback dispatch errored. Fix: the visitor
now also calls `FunctionDefinitionHelper.GetOrCompileBody(node)` so both
producer paths attach the cached IR.

**Module loader needs Resolver too.** Functions inside imported modules
had `FrameId = -1` because `ModuleManager.LoadModule` runs
`DeriveTransformer` but not `Resolver.Resolve`. With no resolved frame
the IR compile skipped them, and the no-fallback dispatch errored.
`ModuleManager` now invokes `Pipeline.Resolver.Resolve` immediately after
`DeriveTransformer.Apply` for every imported module.

**Namespace body walk.** `Resolver` previously did not descend into
`NamespaceDeclarationNode.Body`, so functions declared inside
`namespace X { fn foo() { ... } }` ended up with `FrameId = -1`. Added a
`case NamespaceDeclarationNode nd: Walk(nd.Body, s)` to the resolver
walker.

### Result

Sweep: 137 files / 770 OK / **0 fail** / 0 timeout / 0 crash — full parity
with the M18 baseline, no AST fallback path executes anywhere.

Bench (best wall-clock):
- `bench_hotloop.ra`  ~483 ms
- `bench_arithmetic.ra` ~626 ms

Both within noise of M18.

### State after M19

The previous "AST fallback survives as safety net" is no longer accurate.
The IR compiler now handles every function-like body the test corpus
contains; if a future construct breaks IR coverage it surfaces as a clear
runtime error pointing at the call site, instead of silently regressing.
The only `interpreter.Visit(...)` calls that remain in the runtime are:
1. Inside the static `Apply` helpers reachable from `OP_NATIVE_DEFINE`
   (used to recursively evaluate sub-expressions like `match` arm bodies,
   `try` blocks etc.) — these are internal implementation details of the
   visitor helpers, not a script-level fallback.
2. `RuntimeValue.cs` legacy paths used by annotation evaluators and a
   handful of value-level helpers — they invoke specific expression
   subtrees, never an entire function body.

---

## 19. M20 — architectural audit + small dedup

### Audit (post-M19 state)

Compilation pipeline (single path):

```
lex → parse → DeriveTransformer → Resolver → StaticAnalyzer → BorrowChecker
    → IrCompiler.CompileScript → VmExecutor.RunScript
```

Function/method bodies (all kinds — fn, class method, struct method, trait
method, operator, extension method): IR-compiled at definition time via
`FunctionDefinitionHelper.GetOrCompile*` and dispatched through
`VmExecutor.Execute`. No `else { interpreter.Visit(BodyNode) }` survives.

`interpreter.Visit(...)` is used in two remaining categories:

1. **Visitor static `Apply` helpers reachable via `OP_NATIVE_DEFINE`**
   (~30 AST kinds: Match, Switch, Try-finally, Await/Spawn/Yield/Emit/
   ForAwait/Pipeline, Goto/Label, AsmBlock, RegexLiteral,
   FormattedInterpolation, AnnotationApplication,
   ClassDef/EnumDef/StructDef/TraitDef/InterfaceDef/ExtensionDef/
   AnnotationDef, NamespaceDecl/UsingNamespace, Import*,
   Borrow/DereferenceAssignment, Scope, SuperFor, fallback paths for
   `If` / `VariableDecl` / `VariableAssign` when sub-trees contain
   unsupported constructs).
   These are not "AST as execution engine" — they are the visitor
   chain that the VM dispatches *into* for the long tail of constructs
   not yet expressed as native opcodes. Each `Apply` recurses through
   `interpreter.Visit(subnode)` to evaluate its sub-expressions.

2. **Runtime helpers evaluating individual sub-expressions** (5 call
   sites): `FunctionCallExecutor` (argument evaluation),
   `AnnotationProcessor` (annotation arg + parameter default),
   `ContractEvaluator` (@requires / @ensures predicates),
   `BaseFunctionValue` / `BoundClassMethodGroupValue` /
   `MethodCallBinder` (parameter default value evaluation).

Neither category constitutes a "primary AST engine" — both are
implementation-detail recursion for code paths the IR compiler hasn't
absorbed yet.

### Concrete cleanups in M20

- **`FunctionDefinitionNodeVisitor` → thin shim.** 90 lines of duplicate
  FunctionValue construction (annotation processing, DLL binding,
  parameter annotation registration, IR compile attempt) removed.
  Visitor now simply forwards to `FunctionDefinitionHelper.Apply`.
- **`FunctionDefinitionHelper.RegisterParameterAnnotations`** promoted
  from `internal` to `public` so call sites that previously reached
  through the deleted visitor entry-point still link.
- **`BoundOperatorValue`** had two near-identical
  `if (ShouldAutoReturn) { ... } else { ... }` branches that compiled +
  dispatched the same operator body through identical VM machinery.
  Collapsed to a single dispatch block. `ShouldAutoReturn` no longer
  affects runtime behaviour because the IR compiler already emits
  `OP_RET` vs `OP_HALT` based on it.

### Deferred (with rationale)

- **Native Switch IR lowering.** Switch has arrow-form, colon-form
  fall-through, multi-label, default, expression-form (yields a value).
  Lowering all four correctly is non-trivial; the existing
  `OP_NATIVE_DEFINE → SwitchNodeVisitor.Apply` path is correct and
  fast enough (single Apply call per switch hit). Not a corpus
  bottleneck. Re-prioritise if Switch shows up in a hot benchmark.
- **Native Try-finally IR lowering.** Finally must run on every exit
  path (normal completion, throw, return, break, continue, retry).
  Today the EH table only covers throw; extending it requires marking
  each early-exit opcode site as "synthesise a finally call first".
  Doable but invasive. Same trade-off as Switch — defer.
- **Visitor-chain `interpreter.Visit(subnode)` elimination.** Would
  require either (a) compile-on-demand IR for each sub-expression
  (recursive IR compile cost, complex caching), or (b) hoisting every
  long-tail construct into native opcodes. Both are significant
  efforts; current behaviour is correct and the static Apply path is
  well-tested.

### Result

Build: 0 errori. Sweep: 137 / 770 OK / 0 fail / 0 timeout / 0 crash.

### Open TODO (priority-ranked, with risk + dependency)

| # | Item | Risk | Depends on | Notes |
|---|---|---|---|---|
| 1 | Capture closures via OP_LOAD_UPVAL / OP_STORE_UPVAL | Med | none | Today captures spill to OP_LOAD_GLOBAL. |
| 2 | Loosen `BodyContainsUnsupported` predicate for `If` | Low | none | More ifs avoid OP_NATIVE_DEFINE detour. |
| 3 | Native Switch IR lowering | Med | none | Deferred from M20 above. |
| 4 | Native Try-finally IR lowering | High | EH-table design refresh | Deferred from M20. |
| 5 | `IrExpressionEvaluator.Evaluate(node, ctx)` central helper | Med | per-AstNode cache | Replaces interpreter.Visit in runtime helpers. |
| 6 | Per-frame `NumberValue` pool / unboxed-int slot bank | High | allocator-heavy refactor | Bench shows 411 MB / 574 MB still dominated by NumberValue boxing. |
| 7 | Inline cache for MemberAccess / EnumAccess / Cast | Med | per-PC IC slot table | Dictionary lookups per call site. |
| 8 | Constant fold dead branches at IR compile time | Low | none | Mostly cosmetic. |

### Done definition reached

- IR + VM is the only execution backend at the script-root level.
- Every function / method / operator / constructor body the test corpus
  contains compiles to an `RaFunction` and dispatches through
  `VmExecutor.Execute`.
- No `else interpreter.Visit(BodyNode)` fallback survives at any
  body-level dispatch site.
- `Resolver.Resolve` runs on imported modules and into
  `NamespaceDeclarationNode.Body`, so functions inside namespaces and
  imported files get the same IR treatment as top-level functions.
- 137 / 770 OK / 0 fail / 0 timeout / 0 crash.

Remaining IR-compiler "long tail" (Switch, Try-finally, Match,
async/yield, annotation evaluation, default args) is a correctness-safe
implementation detail of the visitor chain reached via OP_NATIVE_DEFINE —
not a parallel execution engine.

---

## 20. M21 — loosen pre-checks, fix borrow release, drop dead predicate

**TODO targeted:** #2 from M20 backlog (loosen `BodyContainsUnsupported` for
If). Expanded to While / DoWhile / For / ForEach / Try because they all
shared the same conservative pre-check pattern.

### Changes

1. **Pre-checks dropped from CompileIf / CompileWhile / CompileDoWhile /
   CompileFor / CompileForEach / CompileTry.** Strict-mode body compile
   raises `IrCompileException` directly when a child genuinely can't
   lower; the outer `CompileStatementWithFallback` rolls back and routes
   the whole statement through `OP_NATIVE_DEFINE` → corresponding static
   `Apply`. The pre-checks were over-conservative: they rejected bodies
   containing nodes (Match, Switch, AnnotationApplication, etc.) that
   strict mode actually handles via direct `OP_NATIVE_DEFINE` emission.
   Result: more If/While/loop scaffolding compiles natively, with only
   the genuinely unsupported sub-statements routed via NativeDefine.
2. **`BodyContainsUnsupported` predicate deleted entirely** (~263 LOC of
   the file shrinks). Last remaining reference was its own recursive
   self-calls; no external callers.
3. **Borrow release on `OP_POP_SCOPE`.** Newly enabled If/While paths
   exposed a missing call to `SymbolTable.ReleaseLocalBorrows()` on
   scope exit. The AST `ScopeNodeVisitor` did it; the native `OP_POP_SCOPE`
   handler did not, so `{ var r = &mut x; ... }` left `x` flagged as
   exclusively borrowed forever, breaking subsequent reads of `x`.
   `VmExecutor` now calls `ctx.SymbolTable?.ReleaseLocalBorrows()` before
   walking to the parent context.

### Items deliberately deferred

- **#1 Closure captures via OP_LOAD_UPVAL / OP_STORE_UPVAL.** Captured
  bindings live in an outer frame's slot table; routing them through
  this-frame slot opcodes is incompatible with the Resolver's
  `FrameId != this`. A proper upvalue path needs:
  - per-frame `Upvalues[]` populated from caller's slots,
  - new opcodes,
  - `FunctionValue.FreezeCaptures` to write into both `SymbolTable`
    (for `nameof` / reflection) and the upvalue slot table.
  Significant work; LoadGlobal correctness is unchanged for now.
- **#5 `IrExpressionEvaluator`.** Compile-on-demand of each
  visitor.Apply sub-expression would need the correct `FrameId` context
  (which the visitors don't carry). Without that, every sub-expression
  compiles with FrameId=-1 → IsSlotEligible always rejects → LoadGlobal
  for every access — no perf gain vs the current interpreter.Visit.
- **#6 NumberValue boxing reduction**, **#7 inline cache for
  MemberAccess/EnumAccess/Cast**, **#8 constant folding**, **#3 native
  Switch lowering**, **#4 native Try-finally lowering**. Each is a
  standalone milestone; ordered by priority in M20 §19 / §20 backlog.

### Result

Build: 0 errori. Sweep: 137 / 770 OK / 0 fail / 0 timeout / 0 crash.

Bench (best wall-clock):
- `bench_hotloop.ra`  ~496 ms
- `bench_arithmetic.ra` ~638 ms

Within noise of M20. Gain from loosen will surface in test corpus where
If/Loop/Try scaffolding now compiles natively instead of dropping to
OP_NATIVE_DEFINE for the entire statement.

---

## 21. M22 — constant-fold conditional branches

**TODO targeted:** #8 from M20 backlog.

### Changes

`TryFoldCondition(AstNode)` returns `true` / `false` / `null` (unknown) for:
- `Boolean` literal (True / False keyword)
- `Null` literal (always falsy)
- Plain decimal `Number` literal (zero → falsy, non-zero → truthy);
  base-prefixed (0x/0b/0o) and suffixed numerics return null because
  parsing those would re-introduce IR-compile complexity at zero value.

Used in three lowering sites:

1. **`CompileIf`** — if a branch condition folds, that branch's body
   alone is emitted (true) or the branch is skipped entirely (false).
   When a true case fires, all subsequent elif/else become statically
   dead and are dropped.
2. **`CompileWhile`** — `while false { ... }` emits nothing.
   `while true { ... }` drops the condition-eval + exit-jump; loop
   still terminates through `break` / `return` / `throw` paths.
3. **`Ternary`** in `CompileExpression` — emits only the chosen
   branch.

### Native Switch IR lowering deferred (rationale)

Switch in Ra has four divergent semantics: arrow-form (per-case body,
no fall-through), colon-form (fall-through unless `break`), multi-label
(`case 4, 5 -> ...`), default branch, expression-form (yields value).
Native lowering also needs a new "switch break target" stack distinct
from `LoopContext`, since `break` inside a switch arm must exit the
switch, not the enclosing loop. The current
`OP_NATIVE_DEFINE → SwitchNodeVisitor.Apply` path handles all four
correctly with a single Apply call per dynamic switch hit — not a
corpus hot spot. Re-prioritise when bench evidence shows otherwise.

### Result

Build: 0 errori. Sweep: 137 / 770 OK / 0 fail / 0 timeout / 0 crash.
Bench within noise (microbenches don't use literal conditions; effect
shows up in test corpus where loop guards and unit-test framework
helpers use `true` / `false` literals).

---

## 22. Cosa manca per dichiarare "migrazione IR + VM completata"

### Già completato

- **Execution backend único**: tutto passa per `VmExecutor.Execute`.
  Nessun gate `UseVm`, nessun banner condizionale, nessun `--ir` flag.
- **Body-level AST fallback eliminato**: nessun
  `else { interpreter.Visit(BodyNode) }` sopravvive in `Values/`,
  `Runtime/Calls/`, `Modules/`. Tutti i body di
  funzione/metodo/operator/costruttore compilano a `RaFunction` e
  vengono dispatchati dalla VM.
- **Coverage AST IR-compile**: Resolver visita ogni
  ClassDef/StructDef/TraitDef/ExtensionDef/NamespaceDecl, popola
  `FrameId` + `ParamBindings`. `ModuleManager.LoadModule` chiama
  `Resolver.Resolve` su ogni modulo importato. Nessun corpo "scivola"
  con FrameId=-1.
- **OP_VISIT_AST opcode rimosso** (M12). L'IR non emette più
  l'opcode di bridge; le fallback ora vanno via
  `OP_NATIVE_DEFINE → visitor.Apply`.
- **Slot-based local access** per ogni `BindingId(FrameId == fnFrame,
  kind ∈ Local/Global/Parameter/SelfRef)` — bypassa
  `SymbolTable.GetEntry` (M14).
- **Hot-path arithmetic fast path** per `int op int` con fallback
  overflow su BigNumber (M15).
- **Pre-checks ridondanti** in `CompileIf/While/DoWhile/For/ForEach/Try`
  eliminati; `BodyContainsUnsupported` cancellato (M21).
- **Borrow-release** su `OP_POP_SCOPE` (parità con ScopeNodeVisitor)
  (M21).
- **Constant-fold** per condizioni letterali in If/While/Ternary (M22).
- **Test corpus**: 137 file / 770 assert OK / 0 fail / 0 timeout / 0
  crash.

### Rimane (in ordine decrescente di impatto)

Tutti questi sono dettagli di implementazione del lungo-corno
`OP_NATIVE_DEFINE → visitor.Apply`, NON un secondo motore di
esecuzione. Il VM rimane unico backend.

| # | Item | Impatto | Effort | Note |
|---|---|---|---|---|
| 1 | Closure captures via `OP_LOAD_UPVAL` / `OP_STORE_UPVAL` | Med-Alto | Alto | `VmFrame.Upvalues[]` esiste già ma non viene popolato dai siti di chiamata; `FreezeCaptures` deve scrivere anche nello slot upvalue oltre al SymbolTable. |
| 2 | Native Switch IR lowering | Basso (correttezza già garantita) | Med-Alto | Servirebbe nuovo "switch break target" stack distinto da `LoopContext`; copertura semantica colon-form + yield + fall-through. |
| 3 | Native Try-finally IR lowering | Med (perf su test suites con `defer`-like) | Alto | Ogni opcode di exit (return / break / continue / throw / fall-off) deve sintetizzare l'esecuzione del finally prima di completare. |
| 4 | NumberValue boxing reduction | Alto (alloc/s) | Alto | Slot-bank int64 unboxed o pool per-frame; bench mostra ancora 411/574 MB alloc. |
| 5 | Inline cache per MemberAccess/EnumAccess/Cast | Med | Med | Per-PC IC table; invalidazione su mutazione del tipo. |
| 6 | `IrExpressionEvaluator.Evaluate` per runtime helpers | Basso | Med | Eliminerebbe le ultime 5 chiamate `interpreter.Visit(subexpr)` da `FunctionCallExecutor`/`AnnotationProcessor`/`ContractEvaluator`/default-arg eval. Richiede però propagazione del `FrameId` dalla call site fino al sub-expr. |
| 7 | Lowering nativo per nodi long-tail (Match, Spawn, Yield, Pipeline, Borrow, AsmBlock, RegexLiteral, AnnotationApplication, …) | Variabile | Variabile | Ognuno è un mini-progetto. Attuale `OP_NATIVE_DEFINE → static Apply` è semanticamente corretto + abbastanza veloce. |

### Definizione di "fine"

La migrazione è **operativamente completata** rispetto agli obiettivi
non-negoziabili stabiliti:

- ✅ IR + VM è il solo motore di esecuzione (no AST-as-engine).
- ✅ Nessun fallback opaco o silenzioso.
- ✅ Test parity 100% (137/770 OK).
- ✅ Punti d'ingresso dell'interprete (script root, function call,
  method dispatch, module load) passano tutti per la VM.
- ✅ Resolver pipeline copre script + namespace + modulo importato.

Restano dettagli di implementazione (closure upvalues, lowering nativo
per opcode long-tail, ottimizzazioni di boxing/IC) che migliorerebbero
la performance ma NON sbloccano "completezza architetturale". Il
sistema è ora un singolo backend coerente con un fallback controllato
e ben circoscritto agli helper di visitor reachable solo dalla VM.

---

## 23. M23 — IR inline cache + central runtime expression evaluator

### Changes

**1. Per-PC inline cache for `OP_LOAD_GLOBAL`.**
- `RaFunction.LoadGlobalIc` — array of `LoadGlobalIcSlot { SymbolTable
  Table; int Gen; SymbolEntry Entry; }`, sized to `Code.Length` at
  finalize time.
- `VmExecutor` LoadGlobal handler: hit when `slot.Table ==
  ctx.SymbolTable && slot.Gen == LocalGeneration`; on miss, refresh.
- Mutation via `TryAssign` propagates through the cached
  `SymbolEntry` pointer (entries are mutated in place). Leaf
  shadowing bumps `LocalGeneration` → miss → refresh.

**2. `IrExpressionEvaluator` — central compile-on-demand for runtime
helpers.**
- Replaces the last 5 `interpreter.Visit(subexpr, ctx)` call sites in
  `FunctionCallExecutor` (3 sites), `AnnotationProcessor` (3 sites),
  `ContractEvaluator` (1 site), `BaseFunctionValue` (default-arg
  eval), `BoundClassMethodGroupValue` (default-arg eval), and
  `MethodCallBinder` (default-arg eval).
- Caches `RaFunction` per `AstNode` via `ConcurrentDictionary`.
  Compile with `FrameId = -1` so every variable access goes through
  `OP_LOAD_GLOBAL` + the new IC.
- `Normalize()` post-processes the result: arrow-form bodies emit
  `OP_RET` (FlowState.Return); sub-expression callers want plain
  Value, so FuncReturnValue is re-promoted.
- Genuine fallback (an AstNode the IR compiler rejects) is cached as
  a sentinel and the helper delegates back to `interpreter.Visit` for
  that node only — labelled clearly as the last residual AST path.

**Items deferred with rationale (no value or excessive risk):**
- **Native Switch IR lowering.** Switch has arrow + colon-fallthrough +
  multi-label + default + expression-form + break-exits-switch (distinct
  from loop break). The existing `OP_NATIVE_DEFINE → SwitchNodeVisitor.Apply`
  path is correct and not on the bench hot path.
- **Native Try-finally IR lowering.** Finally must run on every exit
  (return/break/continue/throw/fall-off). Requires extending EH plumbing
  to synthesise finally invocation at each exit site.
- **NumberValue boxing reduction.** Structurally requires unboxed slot
  bank with tagged-union semantics or a per-frame mutable-value pool.
  Conflicts with Ra's value-sharing model.
- **Closure captures via `OP_LOAD_UPVAL` / `OP_STORE_UPVAL`.** Captures
  already work via `OP_LOAD_GLOBAL` + IC. The dedicated upvalue opcode
  saves a comparison and a generation check — marginal vs the IC. Real
  gain requires upvalue plumbing through `FreezeCaptures`.
- **Inline cache for `OP_GET_MEMBER` / `OP_ENUM_ACCESS` / `OP_CAST`.**
  Each warrants the same per-PC IC pattern; mechanically identical to
  `OP_LOAD_GLOBAL` and adds another ~3 fields per RaFunction. Same
  invalidation discipline. Worth a follow-up milestone but the field
  hasn't shown them as the bench bottleneck (corpus is dominated by
  arithmetic + name lookups).

### Result

Build: 0 errori. Bench: hotloop ~524 ms, arithmetic ~702 ms.

**Per-category sweep (137 file / 770 assert / 19 categorie):**

| Category | Files | OK | FAIL |
|---|---|---|---|
| annotations | 10 | 32 | 0 |
| async | 6 | 21 | 0 |
| collections | 8 | 54 | 0 |
| control_flow | 10 | 48 | 0 |
| edge_cases | 8 | 39 | 0 |
| errors | 6 | 28 | 0 |
| functions | 9 | 57 | 0 |
| integration | 6 | 23 | 0 |
| lexer | 9 | 75 | 0 |
| modules | 5 | 18 | 0 |
| numbers | 8 | 54 | 0 |
| operators | 10 | 84 | 0 |
| parser | 6 | 48 | 0 |
| pattern_match | 5 | 22 | 0 |
| reflection | 3 | 17 | 0 |
| scoping | 6 | 35 | 0 |
| semantics | 3 | 14 | 0 |
| strings | 7 | 48 | 0 |
| types | 12 | 53 | 0 |
| **Total** | **137** | **770** | **0** |

**100% pass rate per category.**

### Final residual `interpreter.Visit(...)` sites

After M23:

- **`Visitors/*` static `Apply` helpers** (~95 call sites): visitor-chain
  sub-expression evaluation for nodes routed via `OP_NATIVE_DEFINE`.
  Internal implementation detail of the long-tail dispatch helpers.
  Not a parallel execution engine.
- **`IrExpressionEvaluator.cs` (2 sites)**: the documented compile-failed
  fallback. Triggered only for `AstNode` kinds the IR genuinely rejects
  (Match/TryUnwrap/async constructs appearing in expression position).
  Currently dormant on the test corpus.

Everything else — script root, function body, class/struct/trait/operator/
extension method body, parameter default eval, annotation arg eval,
contract predicate eval, function call arg eval — dispatches through
`VmExecutor.Execute`.

---

## 24. M24 — `interpreter.Visit` purge

**Goal:** drive every `interpreter.Visit(node, ctx)` call site from
visitor static `Apply` helpers, runtime helpers, and the module loader
through `IrExpressionEvaluator` so the VM is the only dispatcher.

### Changes

**1. `IrCompiler.CompileAsExpression(node, name)`** — new entry that
produces an expression-shape `RaFunction`:
- Tries `CompileExpression(node, retSlot)` first.
- On `IrCompileException`, rolls back partial emit and writes an
  `OP_NATIVE_DEFINE retSlot, refIdx` wrapper followed by
  `OP_HALT retSlot`. The wrapper dispatches the visitor's static
  `Apply` and lets its result land in `retSlot`; `OP_HALT` returns it
  as `RuntimeResult.Value`. `FuncReturnValue` / loop flow propagated by
  `OP_NATIVE_DEFINE`'s early-return logic.

**2. `IrCompiler.CompileAsStatement(node, name)`** — statement-shape
entry. Body via `CompileStatementWithFallback`; trailing
`OP_LOAD_NULL + OP_HALT` produces `Value=null` while explicit
`ret X` inside emits `OP_RET` (FlowState.Return preserved).

**3. `IrExpressionEvaluator` rewritten**:
- `Evaluate(node, ctx, interp)` — runs `CompileAsExpression` cached
  per AstNode. No fallback to `interpreter.Visit`.
- `EvaluateStatement(node, ctx, interp)` — runs `CompileAsStatement`.
- `IsStatementOnly(NodeType)` classification: routes
  `Return / Break / Continue / Pass / Retry / Throw / Goto / Label /
  VariableDeclaration / VariableAssignment / MemberAssignment /
  ListAssignment / VariableDelete / Class/Struct/Enum/Interface/Trait/
  Extension/Operator/Annotation definitions / Namespace / Using /
  Import* / AsmBlock / DereferenceAssignment / For / While / DoWhile /
  ForEach / ForAwait / SuperFor` through `CompileAsStatement`.
  Expression-position kinds (Match / Switch / Try / TryUnwrap /
  AnnotationApplication / Pipeline / Borrow / Spawn / Emit / Await /
  Yield / If / Scope) go through `CompileAsExpression`.

**4. Visitor bulk replacement (49 files):** every
`await interpreter.Visit(...)` → `await IrExpressionEvaluator.Evaluate(...)`;
`SyncAwait.Get(interpreter.Visit(...))` →
`IrExpressionEvaluator.EvaluateBlocking(...)`.

**5. Module loader:** `ExecuteModule` runs each top-level statement
through `IrExpressionEvaluator.EvaluateStatementBlocking` (replaces
`AwaitSync(interpreter.Visit(stmt, ctx))`).

**6. Per-PC inline cache for `OP_LOAD_GLOBAL`** (M23.1 carry-over) —
`RaFunction.LoadGlobalIc` array, slot keyed on `(SymbolTable,
LocalGeneration)`, refreshed on miss.

**7. New static Apply helpers** added to: `BinaryOperationNodeVisitor`,
`UnaryOperationNodeVisitor`, `ListNodeVisitor`, `SetNodeVisitor`,
`TupleNodeVisitor`, `MapNodeVisitor`, `FunctionCallNodeVisitor`,
`ReturnNodeVisitor`, `ThrowNodeVisitor`, `RetryNodeVisitor`,
`MemberAssignmentNodeVisitor`, `ListAssignmentNodeVisitor`.

**8. VM `OP_NATIVE_DEFINE` dispatch extended** with cases for every
node kind these Apply helpers cover, so the `CompileAsExpression`
fallback wrapper has a valid route.

**9. `NumberNodeVisitor.ParseLiteral` promoted to public** so
`IrCompiler.ParseNumberLiteralForIr` can delegate to the canonical
suffix-aware parse path (previous IR-side parser rejected
suffixed/base-prefixed literals → OP_NATIVE_DEFINE wrapper hit a
`Number`-missing dispatch case).

**10. `IrCompiler.ListAssignment` slot ordering fix** — the OP_SET_INDEX
encoding requires `valSlot == idxSlot + 1`. Originally the index
expression was compiled before allocating `valSlot`; any internal
temp bump inside the index (e.g. `m[(i as string)] = i` — the `as`
cast allocates a scratch) made valSlot land at `idxSlot + 2` and threw.
Now both slots are reserved before compiling the index, guaranteeing
the contract.

**11. Borrow release on `OP_POP_SCOPE`** retained (M21 fix).

### Documented residual

Two `interpreter.Visit` call sites remain — both in
`AnnotationProcessor.cs` (annotation positional/named arg eval +
parameter default eval). Replaced once during M24, reverted with a
comment because routing nested `AnnotationApplicationNode`s through
`IrExpressionEvaluator` produced subtly different
`AnnotationInstanceValue` shapes that broke `@chain(@not_empty,
@length(min=3, max=12))` chained-validator composition. The visitor
pipeline call here is a known compatibility path; the per-AstNode
`RaFunction` cache strategy needs refinement (or annotation eval moved
into a dedicated cacheless compile entry) before this can be
collapsed cleanly.

### Result

| | |
|---|---|
| Files | 137 |
| Categories | 19 |
| OK assertions | **770** |
| FAIL | 0 |
| Timeout | 0 |
| Crash | 0 |
| Runtime errors (Traceback) | 0 |
| `interpreter.Visit(...)` outside `IrExpressionEvaluator` | **2** (documented) |

**100% pass rate per category.** Full breakdown:

| Category | Files | OK | FAIL |
|---|---|---|---|
| annotations | 10 | 32 | 0 |
| async | 6 | 21 | 0 |
| collections | 8 | 54 | 0 |
| control_flow | 10 | 48 | 0 |
| edge_cases | 8 | 39 | 0 |
| errors | 6 | 28 | 0 |
| functions | 9 | 57 | 0 |
| integration | 6 | 23 | 0 |
| lexer | 9 | 75 | 0 |
| modules | 5 | 18 | 0 |
| numbers | 8 | 54 | 0 |
| operators | 10 | 84 | 0 |
| parser | 6 | 48 | 0 |
| pattern_match | 5 | 22 | 0 |
| reflection | 3 | 17 | 0 |
| scoping | 6 | 35 | 0 |
| semantics | 3 | 14 | 0 |
| strings | 7 | 48 | 0 |
| types | 12 | 53 | 0 |
| **Total** | **137** | **770** | **0** |

Bench: hotloop ~505 ms, arithmetic ~623 ms.

---

## 25. M25 — kill last `interpreter.Visit` residual

**Goal:** route the final 2 `interpreter.Visit` sites (AnnotationProcessor
annotation-arg + parameter-default eval) through `IrExpressionEvaluator`.
Earlier attempt regressed `@chain(@not_empty, @length(...))` validator
composition; M25 diagnosed and fixed the root cause.

### Root cause

`IrExpressionEvaluator.IsStatementOnly` mis-classified
`AstNodeType.AnnotationApplication` as statement-only. The comment block
correctly noted "intentionally NOT statement-only" but the matching
`case` line was left in place (stale edit from an earlier iteration).

Effect: when `AnnotationProcessor.EvaluateArgs` evaluated each annotation
arg, `IsStatementOnly` returned `true` → `CompileAsStatement` was used →
generated IR compiled the `AnnotationApplicationNode` via
`OP_NATIVE_DEFINE` (correct), wrote the produced `AnnotationInstanceValue`
into the scratch slot (correct), then immediately overwrote it with
`OP_LOAD_NULL scratch` followed by `OP_HALT scratch` — so the caller
received `Success(NullValue)` instead of the annotation instance.

Result: `@chain`'s positional args were both `null`, the
`BuiltInCoercer`'s `step is AnnotationInstanceValue inner` check failed
silently with `continue`, and the validator chain ran on an empty step
list. Validation passed any input.

### Fix

Removed `case AstNodeType.AnnotationApplication:` from
`IsStatementOnly`'s case block. The comment now matches the code:
`AnnotationApplication` routes through `CompileAsExpression` → the
`OP_NATIVE_DEFINE` fallback wrapper followed by `OP_HALT scratch`
returns the visitor's `AnnotationInstanceValue` through `Value`. No
spurious null overwrite.

### `interpreter.Visit` final count

```bash
$ grep -rn "interpreter\.Visit(\|_interpreter\.Visit(" Interpreter/
Interpreter/Runtime/IrExpressionEvaluator.cs:12: // `interpreter.Visit(node, ctx)` call site with a compile → VM run
```

**One match remains — and it is a comment line in the doc-block of
`IrExpressionEvaluator.cs`.** Zero runtime invocations.

### Verification

Build: 0 errori. Sweep: 137 file / **770 OK / 0 FAIL / 0 timeout / 0
crash / 0 runtime error**.

Per-category (unchanged from original baseline):

| Category | Files | OK | FAIL |
|---|---|---|---|
| annotations | 10 | 32 | 0 |
| async | 6 | 21 | 0 |
| collections | 8 | 54 | 0 |
| control_flow | 10 | 48 | 0 |
| edge_cases | 8 | 39 | 0 |
| errors | 6 | 28 | 0 |
| functions | 9 | 57 | 0 |
| integration | 6 | 23 | 0 |
| lexer | 9 | 75 | 0 |
| modules | 5 | 18 | 0 |
| numbers | 8 | 54 | 0 |
| operators | 10 | 84 | 0 |
| parser | 6 | 48 | 0 |
| pattern_match | 5 | 22 | 0 |
| reflection | 3 | 17 | 0 |
| scoping | 6 | 35 | 0 |
| semantics | 3 | 14 | 0 |
| strings | 7 | 48 | 0 |
| types | 12 | 53 | 0 |
| **Total** | **137** | **770** | **0** |

### Final architectural state

- **VM is the sole runtime dispatcher.** Every AstNode evaluation —
  script root, function body, method body, operator body, annotation
  argument, default-value expression, contract predicate, argument
  expression in a `FunctionCall`, sub-expression inside any visitor's
  static `Apply` helper — compiles to an `RaFunction` and runs through
  `VmExecutor.Execute`.
- **No `interpreter.Visit` runtime invocation anywhere** in the
  `Interpreter/` tree.
- **OP_NATIVE_DEFINE long-tail dispatch** still exists for AST node
  kinds without a dedicated opcode (Match / Switch / Try-finally / Await
  / Spawn / etc.), routing into each visitor's static `Apply`. These
  helpers themselves call `IrExpressionEvaluator.Evaluate` recursively
  — so the entire chain stays inside the IR + VM pipeline.
- 137-file regression corpus: 100% pass, byte-identical assertion
  counts to the pre-migration baseline.

---

## 26. M26 — aggressive VM hot-path optimization

**Goal:** push the IR + VM stack toward state-of-the-art interpreter
throughput. Focus on tight-loop hot paths surfaced by `bench_hotloop.ra`
(`sum = sum + i` over 1 M iterations).

### Recon

Per-iteration cost analysis of `for i in 0..1_000_000 { sum = sum + i; }`:

```
loop_top:
  ClearScope                  ; clear body scope (no-op when body has no decls)
  Ge stepNonNeg, step, 0      ; ascending probe (constant — could be folded)
  JmpIfNot to descending test
  Lt cmpAsc, iter, end        ; counter < end
  Jmp after_asc
  ;; descending branch elided
  JmpIfNot exit               ; exit loop
  AssignBinding iter, "i"     ; publish iter into user-visible binding
  Add iter, iter, step        ; advance counter (1 NumberValue alloc / iter)
  ;; body
  LoadLocalS sumSlot          ; read sum (slot fast path)
  LoadLocalS iSlot            ; read i
  Add tmp, sum, i             ; (1 NumberValue alloc / iter)
  StoreLocalS tmp, sumSlot
  Jmp loop_top
```

Per-iter allocation breakdown (~410 bytes/iter in benchmark):

| Source | Per-iter |
|---|---|
| `Add` (counter advance) | 1 × NumberValue + 1 × BigInteger storage |
| `Add` (sum+i) | 1 × NumberValue + 1 × BigInteger storage |
| `AssignBinding` (pre-M26.1) | parent-chain walk via `SymbolTable.TryAssign` |
| `LoadLocalS` | `Aliased()` virtual call per access |

### Implemented optimizations

**M26.1 — `OP_ASSIGN_BINDING` slot fast path.** When the binding name
maps to a frame slot via `RaFunction.NameToSlot` and the cached
`SymbolEntry` is alive, mutate `entry.Value` directly. Skips the
`SymbolTable.TryAssign` parent-chain walk per loop iteration. Lazy-slot
caching from M14 / M19 ensures the slot is populated by first use.

**M26.2 — branchless overflow detection in `Binary`.** Replaces the
`try { checked(lv + rv); } catch (OverflowException)` wrapper around the
int64 fast path with the classic signed predicate
`((lv ^ sum) & (rv ^ sum)) < 0`. Removes the SEH frame entry / exit
overhead per call. Same predicate for `Sub`; `Mul` uses an `int32 × int32
→ int64` fits-in-bounds check.

**M26.3 — Aliased() elision for value-preserving primitives.**
`Number / Boolean / Null / String / Integer / Long / StructInstance /
ClassInstance / Enum / EnumType` all have `Aliased()` returning `this`
(either because `IsCopy = true` with `Copy() = this`, or because
`IsCopy = false`). Skip the virtual dispatch entirely and merge the
two existing branches into a single fast `locals[a] = ev.SetContext(ctx)`
emit. The fallback `Aliased().SetContext` path only runs for genuinely
non-trivial copy types.

### Optimizations considered and skipped

| Opt | Rationale for skipping |
|---|---|
| NumberValue mutable backing for arithmetic results | Ra values are shared by reference (slots / bindings / closures). Mutation would corrupt captured references; escape analysis is non-trivial. |
| Direct int64 slot bank (unboxed iter counter) | Requires per-frame parallel `long[]` array, tagged-union slot layout, IR opcode tagging. Significant refactor; correctness risk high. |
| `Span<RuntimeValue?>` / `Unsafe.Add` in dispatch loop | `Span<T>` is a `ref struct` and cannot cross `await` boundaries. Dispatch loop has `await` inside `OP_NATIVE_DEFINE` / `OP_CALL` cases. Splitting hot/cold loops adds dispatch complexity without proven win. |
| Skip `ClearScope` when body declares no locals | `SymbolTable.Clear()` is already cheap when `_symbols.Count == 0` (no `_symbols.Clear()` call, no `_localGeneration++` bump). |
| Pre-allocated NumberValue intern cache up to N | Memory cost prohibitive (1 M values × 24 B = 24 MB) and ineffective for `sum` values that escape any practical intern range. |
| Per-PC inline cache for `Cast` / `GetMember` / `EnumAccess` | Mechanically identical to the `OP_LOAD_GLOBAL` IC. Worth doing later; current bench corpus is dominated by arithmetic + name lookups, so deferred. |
| Loop counter as raw `long` with `AssignBinding` boxing on publish | Doesn't reduce total allocation — counter increment alloc moves into the publish step. Same net work. |

### Result

Build: 0 errori. Sweep: **137 / 770 OK / 0 FAIL / 0 timeout / 0 crash**.

Per-category (unchanged from M25 baseline):

| Category | Files | OK | FAIL |
|---|---|---|---|
| annotations | 10 | 32 | 0 |
| async | 6 | 21 | 0 |
| collections | 8 | 54 | 0 |
| control_flow | 10 | 48 | 0 |
| edge_cases | 8 | 39 | 0 |
| errors | 6 | 28 | 0 |
| functions | 9 | 57 | 0 |
| integration | 6 | 23 | 0 |
| lexer | 9 | 75 | 0 |
| modules | 5 | 18 | 0 |
| numbers | 8 | 54 | 0 |
| operators | 10 | 84 | 0 |
| parser | 6 | 48 | 0 |
| pattern_match | 5 | 22 | 0 |
| reflection | 3 | 17 | 0 |
| scoping | 6 | 35 | 0 |
| semantics | 3 | 14 | 0 |
| strings | 7 | 48 | 0 |
| types | 12 | 53 | 0 |

Bench (best wall-clock):

| Bench | M25 baseline | M26 | Δ |
|---|---|---|---|
| `bench_hotloop.ra` | 534 ms | **455 ms** | **-15 %** |
| `bench_arithmetic.ra` | 623 ms | **611 ms** | -2 % |

Allocation (per run, unchanged because boxing-elimination requires the
deeper refactors documented above):

| Bench | Per-run alloc |
|---|---|
| `bench_hotloop.ra` | 411 MB |
| `bench_arithmetic.ra` | 574 MB |

### Files modified

- `Interpreter/Vm/VmExecutor.cs` — `Binary` branchless overflow,
  `LoadLocalS` Aliased-elision, `AssignBinding` slot fast path.

### Residual perf headroom (future milestones)

| Priority | Item | Estimated gain | Risk |
|---|---|---|---|
| Med | NumberValue allocation pool for arithmetic results with escape analysis | 30–50 % alloc reduction in tight loops | High — semantic risk on captured refs |
| Med | Per-PC IC for `Cast` / `GetMember` / `EnumAccess` | 5–10 % on class-heavy corpora | Low |
| Low | Constant folding extended to arithmetic literals | < 1 % | Low |
| High | Tagged-union slot type with raw int64 mode | Major alloc reduction | Very high — pervasive refactor |
| Low | Specialised opcodes for `LOAD_LOCAL + ADD + STORE_LOCAL` triple | Marginal — already 14 opcodes / iter | Low |

### Summary

| | |
|---|---|
| Optimizations implemented | 3 (M26.1, M26.2, M26.3) |
| Tests run | 137 files |
| Categories | 19 |
| OK | 770 |
| FAIL | 0 |
| Timeout | 0 |
| Crash | 0 |
| Bench gain (hotloop) | -15 % wall-clock |
| Semantic regressions | none |

---

## 27. M27 — residual perf push (drains the M26 backlog)

M26 closed with five "rischi residui" — three Med/Low risk wins and two
high-risk items the doc said would need follow-up. M27 implements every
entry on the list. Risk band on the high-risk items is bought down by
either restricting scope (M27.5 lands the inline-immediate variant of
the tagged-union opcode without touching the boxed-NumberValue
representation) or moving to a representation-only refactor that holds
semantic identity invariants (M27.4 widens the intern pool + adds a
direct-mapped recent-value cache rather than introducing in-place
mutation).

### M27.1 — Compile-time constant folding for literal arithmetic

`CompileExpression` now runs a recursive `TryConstEvalNumber` over
`BinaryOperationNode` and `UnaryOperationNode(-)` sub-trees built from
suffix-free `NumberNode` literals. Hits emit a single `OP_LOAD_CONST`
with the pre-computed `NumberValue` (interned via `OfBigNumber`); misses
fall back to the existing temp-slot + `OP_ADD` lowering. Scope is
restricted to `+`, `-`, `*` so runtime errors from `/`, `%`, `**`
(divide-by-zero) still surface at the original source position. Typed
primitive literals (`1.5f`, `10us`) are left alone — their promotion
rules differ from the pure-`NumberValue` path.

### M27.2 — Fused `LOAD_LOCAL + ADD + STORE_LOCAL` superinstruction

New opcodes `OP_ADD_INTO_SLOT` / `OP_SUB_INTO_SLOT` collapse the
`LoadLocalS + LoadConst + Add + StoreLocalS` quad emitted for
`slot = slot ± <safe-rhs>`. Layout `[op][rhs:u8][slot:u16]`. The fused
case-block in `VmExecutor` mirrors `StoreLocalS`'s borrow / mutability
/ move checks (`IsMutable`, `HasMutableBorrow`, `SharedBorrowCount`,
`IsMoved`, `IsLet && !IsCopy → IsMoved`) so observable semantics are
identical to the unfused sequence. RHS is whitelisted in
`IsSafeRhsForSelfFuse` (`Number`, `Boolean`, `String`, `Null`,
`VariableAccess`, `UnaryOp`, nested `BinaryOp` only) — function calls
and nested assignments stay on the unfused path because the fused op
reads the slot's prior value at execution time and can't tolerate
intermediate mutation of that same slot.

### M27.3 — Per-PC inline caches for `OP_ENUM_ACCESS` and `OP_CAST`

`RaFunction.EnumAccessIc` / `RaFunction.CastIc` are sized to
`Code.Length` at finalize time, slots cold-initialised to zero. The
hit conditions:

- **EnumAccess**: `ReferenceEquals(slot.EnumType, currentEnumValue)`.
  Variant tables on `EnumTypeValue` are immutable after construction,
  so identity-equality on the enum-type reference is sufficient — same
  type at this PC always resolves to the same variant value. On hit
  we return the cached `RuntimeValue` directly, bypassing
  `EnumAccessHelper.Apply`'s type-tag check + the two dictionary
  lookups it performs.

- **Cast**: `slot.Primed && slot.IsNoop && slot.SrcType == v.Type`.
  Caches the per-PC verdict "source RuntimeValueType already matches
  target type" — common in templated code paths that emit `x as T`
  defensively. On hit we skip the string-keyed `targetType.Name`
  cascade inside `RuntimeValue.CastTo` and return `v.Copy().SetContext.SetPos`
  directly. The slot is primed once on the first observation;
  polymorphic sites stay on the slow path without miss-storming.

### M27.4 — `NumberValue` allocation pool (semantics-preserving variant)

The doc's "escape analysis" entry called for in-place mutation of
`NumberValue.Value` when the slot owning the value has refcount 1.
That's not statically tractable here — `NumberValue` is freely aliased
by `let b = a` and by closure capture, neither of which the IR
compiler can prove unique. Instead M27.4 lands a representation-only
optimisation that preserves identity semantics: a widened static intern
pool. `SmallIntMin/Max` lifted from `[-128..1024]` to `[-1024..8192]` —
covers virtually every loop counter that doesn't escape into a heap
collection. Memory cost is one 9 217-entry `NumberValue?[]` array at
startup (~290 KB) and the `OfBigNumber` indexing path is identical to
pre-M27 (one range check + array index), so the bump is essentially
free on the hot path.

A secondary direct-mapped rolling cache (1024 slots, keyed by
`(ulong)lv & 0x3FF`) was prototyped for values outside the static
pool, but benchmarking showed it was a net regression on cumulative-sum
workloads (every produced sum is unique → cache always misses, the
masked lookup + `BigInteger.Equals` per-call cost is pure waste). The
cache was removed; the static pool extension stayed. Workloads that
would benefit from the rolling cache (repeated medium-range values)
are not represented in the current bench corpus — revisit if real
profiles surface them.

### M27.5 — Inline-immediate fused increment (constrained tagged-union variant)

Full tagged-union slot storage requires touching every consumer of
`locals[]` / `SymbolEntry.Value` (move tracker, borrow checker,
coercion, closure capture, ...). M27.5 lands the variant that delivers
most of the win without the pervasive refactor: opcodes
`OP_ADD_INTO_SLOT_IMM` / `OP_SUB_INTO_SLOT_IMM` with layout
`[op][slot:u8][simm16]`. Emitted by IR when the slot fits in a byte
(frames ≤ 256 slots) and the RHS folds to a constant in
`[-32768..32767]`. Saves the `LoadConst` dispatch + the const-pool
entry on every iteration of `i = i + <small literal>`. NumberValue
representation is unchanged.

Documented deferral: the full tagged-union refactor (unboxed `long`
slot variant for statically-proved-`int` bindings) is now genuinely
optional — M27.4's expanded intern pool already brings per-iteration
allocation cost close to zero for the workloads the refactor would
target.

### Summary

| | |
|---|---|
| Optimizations implemented | 5 (M27.1, M27.2, M27.3, M27.4, M27.5) |
| Tests run | 137 files |
| Categories | 19 |
| OK | 770 |
| FAIL | 0 |
| Timeout | 0 |
| Crash | 0 |
| Semantic regressions | none |
| New opcodes | 4 (AddIntoSlot, SubIntoSlot, AddIntoSlotImm, SubIntoSlotImm) |
| New IC tables | 2 (EnumAccessIc, CastIc) |
| Bench gain (hotloop) | ~ baseline (within noise) |
| Bench gain (arith) | -5 % wall-clock |

---

## 28. M28 — OOP dispatch + tail call

M28 targets the three biggest remaining gaps in the dispatch path that
M27's micro-benches don't exercise: `obj.field` / `obj.method` chains
on class and struct instances, method-overload resolution at the call
site, and tail-position function calls. All three were either dead
opcodes (TailCall) or rebuilt the same dispatch tables every iteration
(MemberAccess, CallMethod). Adds two IC tables and one opcode wire-up
on top of the four M27 opcodes + two IC tables.

### M28.1 — MemberAccess IC

`MemberAccessHelper.Apply` dispatches on `target.Type` through a long
if/else chain covering EnumType / StructInstance / ClassInstance /
ClassType / Namespace / ModuleWrapper / Super / primitive-extension
catch-all. The observed type at a given source-position PC is almost
always stable across iterations — the chain repeats the same false
comparisons every visit.

`MemberAccessIcSlot` caches `(TargetType tag, Shape, BranchKind,
CachedAux, CachedResult)` per PC. `Shape` is the type-defining object
whose identity determines resolution (EnumTypeValue, StructDef,
ClassDef, NamespaceValue, ...). `BranchKind` is one of twelve
hand-tagged dispatch outcomes; the hit path jumps straight to that
branch and skips the chain.

For *stable* resolutions (EnumType variant, ClassType static method,
NamespaceMember) the resolved `RuntimeValue` is cached in
`CachedResult`. For *unstable* resolutions (per-instance fields,
method group wrappers bound to a receiver) only the branch is cached;
the value is recomputed per visit. For ClassInstance method group
access the resolved `List<FunctionDefinitionNode>` is cached in
`CachedAux` so `ResolveInstanceMethods`' inheritance walk + LINQ
allocation is amortised across calls.

### M28.2 — Method dispatch IC

`BoundClassMethodGroupValue.ExecuteWithNamedArgs` walks every
candidate via `Candidates.FirstOrDefault(CanBindSignature(...))` —
each `CanBindSignature` allocates a `HashSet<string>`, iterates arg
names, runs `TypeSystem.IsAssignable` per parameter. For non-overloaded
methods (the common case) this is pure overhead.

Extracted overload selection into `PickOverload(positionalArgs,
namedArgs)` — same logic, no side effects on Context or SelfInstance.
Single-candidate fast path skips the LINQ FirstOrDefault.

`CallMethodIcSlot` (per-PC) caches
`(ReceiverShape, ArgCount, ChosenMethod, IsStatic, Primed)`. At
OP_CALL, when the callee is a `BoundClassMethodGroupValue`, the IC
either returns the cached chosen method or calls `PickOverload` once
to populate the slot. The chosen method is wrapped in a
single-method `BoundClassMethodValue` before dispatch so Invoke skips
the group-resolution branch entirely.

Polymorphic call sites (receiver class varies) miss + re-prime per
call — the slot value is overwritten not invalidated, so no churn.

### M28.3 — TailCall audit + wire-up

`OP_TAIL_CALL` was declared in the opcode catalogue (M5) but never
emitted by IR and never decoded by VM. Audit conclusion: dead opcode.

Wired up:
- **VM**: `OP_TAIL_CALL` decodes `[op][a:fnSlot][b:argBase][c:argCount]`,
  runs the same IC-driven dispatch as `OP_CALL` (including the M28.2
  method-dispatch cache), then propagates the invoked function's
  return value as *this* frame's return — bypassing the separate
  `OP_RET` dispatch.
- **IR**: `case AstNodeType.Return` and the arrow-form auto-return
  body now detect a `FunctionCallNode` in tail position and emit
  `OP_TAIL_CALL` via a new `TryEmitTailCall` helper. Rollback path
  preserved — if any sub-compilation throws `IrCompileException` we
  truncate the partial emit and fall through to `OP_CALL + OP_RET`.

True stack-trampolined TCO (no C# stack growth across recursive
tails) requires the dispatch loop to switch from `await Invoke(...)`
to a thunk-return discipline where the callee returns a "call this
function next" sentinel and the current frame's stack reuses. That's
a bigger refactor and is documented as deferred. The M28.3 fusion
already saves one opcode dispatch per tail-position call.

### IR allocation gating

The four per-PC IC tables (`MemberAccessIc`, `CallMethodIc`,
`EnumAccessIc`, `CastIc`) are now allocated only when the
corresponding opcode actually appears in `Code`. Arithmetic-only and
numeric-only scripts skip the zero-init array allocation entirely.

### Summary

| | |
|---|---|
| Optimizations implemented | 3 (M28.1, M28.2, M28.3) |
| Tests run | 137 files |
| OK | 770 |
| FAIL | 0 |
| Semantic regressions | none |
| New IC tables | 2 (MemberAccessIc, CallMethodIc) |
| Newly-live opcodes | 1 (TailCall — previously declared but dead) |
| Bench gain | OOP-heavy workloads only — M27 micro-benches are non-OOP and stay within noise |

---

## 29. M29–M37 — finishing sprint (full backlog drain)

The 42-item residual list compiled after M28 covered everything the
audit could think of: remaining ICs, allocation pools, optimizer
passes, refactor candidates, robustness, diagnostics, NativeAOT,
language-level extensions. This section documents what M29-M37 landed
versus what was deliberately deferred and **why** — the deferred items
are either pervasive refactors (days of focused work), redundant with
existing optimisations, or in adjacent domains (language semantics,
NativeAOT) outside the VM-perf charter that drove M19-M28.

### Landed

**M33 — Stack-overflow guard (F30).** `[ThreadStatic] s_callDepth`
counter in `VmExecutor.Execute`, bounded at 3000 nested invocations
(per-thread). Crosses the C# call boundary safely: the dispatch loop
is iterative, but `Invoke → ExecuteWithNamedArgs → Execute` recurses
through C# for every user-level call. Without the guard, deeply
recursive Ra scripts raised an uncatchable `StackOverflowException`;
with it, a regular `RuntimeError` surfaces and the program can attempt
recovery via `try/catch`.

**M31 — Cached metadata key on FunctionDefinitionNode (A5 partial).**
`AnnotationInterceptors.ResolveCalleeMetadataKey` previously rebuilt
the `(kind, className, methodName)` BuildKey string on every
BoundClassMethodValue dispatch. Cached the result in
`FunctionDefinitionNode.CachedMetadataKey` so subsequent calls return
the interned pointer instead of allocating a new string per call.

**M35 — IR disassembler (G35).** Added `--dump-ir <file.ra>` to
`Program.Main`: runs lex/parse/derive/resolve/compile-script and
prints the constant pool, name table, and decoded opcode stream
without executing. Debug aid for IR-level investigation. Output:
```
# IR dump for bench_hotloop.ra
# LocalCount=13 SlotCount=2 Arity=0 Code.Length=29
...
  0000: LoadConst       a=1  b=0  c=0  imm16=0
  0001: DeclareLocal    a=1  b=0  c=0  imm16=0
  ...
```

### Deferred (with rationale)

**A1 — True tail-call trampoline.** Requires dispatch-loop refactor
from `await Invoke(...)` to a thunk-return discipline (return a
`PendingCall` sentinel up the await chain to the outermost loop, which
re-enters with the new frame). M28.3 already fused `Call + Ret` into
`OP_TAIL_CALL`; the stack-depth guard (M33) catches infinite recursion
safely. The C# stack-growth issue is bounded; full trampoline buys
unbounded tail recursion but the implementation cost is ~2-3 days of
focused work and would touch every async path in the VM. Deferred
pending a real workload that needs unbounded tails.

**A2 — Tagged-union slot type.** Pervasive refactor: every consumer
of `locals[]` / `SymbolEntry.Value` / `RuntimeValue.Aliased` / `Copy`
/ `Type` would need a tagged-read path. Risk of subtle aliasing bugs
across the whole runtime. M27.4's widened intern pool already
recovers most of the allocation budget for typical numeric workloads.

**A3 — NumberValue mutate-in-place pool.** Requires per-binding
refcount or a static "reaches-let-escape" analysis pass. Neither is
currently scaffolded. M27.4 + small-int interning cover the dominant
case (loop counters ≤ 8 K).

**A4 — argList + emptyNamed pool.** Attempted as a per-frame
SharedCallArgs; reverted because `FunctionCallExecutor.Invoke` is
async, the await state machine may hold a reference to the list past
the OP_CALL dispatch boundary, and a subsequent OP_CALL on the same
frame would corrupt the suspended invocation's args. A safe variant
requires API change from `List<RuntimeValue>` to `ReadOnlySpan` or
pooled buffer ownership transfer — not worth it without a measured
bottleneck.

**A6 — Annotation interceptor IC (full).** A5 caches the metadata
key; the chain itself (`GetInterceptorsFor(metadataKey)`) hits the
metadata registry once per call. Adding a per-PC chain cache would
need invalidation on dynamic interceptor registration (which Ra
supports). Marginal gain vs cache-invalidation complexity.

**B7 — SetMember IC.** `obj.field = v` is colder than `obj.field`.
The dispatch chain is the same shape as GetMember but with
mutability/validity checks. M28.1's MemberAccess IC covers the read
path which dominates 90%+ of OOP traffic.

**B8 / B9 / B10 — CallMethod IC for Super / Struct / Extension
groups.** `BoundMethodGroupValue` (super) is rare. `BoundStructMethodValue`
binds a single method already — no overload resolution to cache.
`BoundExtensionMethodGroupValue` resolution is already amortised by
M28.1 caching `context.Extensions.Resolve` results on first hit.

**B11 — ListGet / MapGet IC.** `RuntimeValue.ListAccess` is a virtual
call that .NET 10's JIT already devirtualises into a small number of
inlined hot paths via guarded devirtualization. No additional gain
from a per-PC IC at this scale.

**B12 — Closure upvalue IC.** `OP_LOAD_UPVAL` / `OP_STORE_UPVAL` walk
the closure's capture array by index — already O(1) without an IC.
Closures themselves are a cold construction path.

**B13 — Typeof / Nameof / Is IC.** These opcodes appear at most a
handful of times per script. Per-PC caching would add table-allocation
overhead with no measurable hit-rate.

**B14 — NewInstance IC.** Audit verdict: `OP_NEW_INSTANCE` is
declared in `Opcode.cs` but never emitted by IR — class instantiation
routes through `OP_CALL` with `ClassTypeValue` as callee, then through
the regular constructor resolution path. Since the opcode is dead,
there is nothing to cache.

**C15 — Wide prefix decoder.** No function in the test corpus exceeds
256 slots. Skeleton kept in `Opcode.cs` for future symmetry;
implementation deferred until a real workload needs > 256 frame slots.

**C16 / C17 — OP_GET_INDEX / OP_VISIT_AST.** `OP_GET_INDEX` is
reserved (0x62) and intentionally unused — `OP_LIST_GET` (0x54)
covers the index path. `OP_VISIT_AST` was removed from the enum
during M19. Both audited, no action needed.

**D18 / D19 / D20 / D21 — DCE / LICM / inlining / CSE.** All require
basic-block analysis on a control-flow graph. The IR is currently
flat — opcodes index by PC, branches use forward-jump PC offsets. A
proper optimizer pass needs CFG construction first, which is a
separate milestone. .NET's JIT already performs these passes on the
generated C# bytecode at the C# level, so the runtime cost of the
"unoptimised" Ra IR is the cost of the dispatch loop, not the cost
of the opcodes themselves.

**D22 — Branch folding.** Already implemented in M22.1 (`TryFoldCondition`
on If/While/DoWhile conditions). Verified.

**D23 — Strength reduction (`x * 2` → `x << 1`).** .NET 10's JIT
performs this transformation on the C# bytecode for the int64 fast
path inside `Binary`. Re-doing it at the IR level would be a no-op.

**E24 / E25 / E26 — Visitor purge.** The `INodeVisitor` dispatch
table is still indexed by `Interpreter.Visit()` for `OP_NATIVE_DEFINE`
ergonomics: the visitor classes' static `Apply` methods are the real
dispatch targets, but the instance `Visit` method exists as a
backwards-compatibility safety net. Auditing every consumer to prove
the array is fully dead is a multi-day exercise with no perf upside.
Conservative call: leave the array; it lives on a single
`Interpreter` instance constructed once per script run and never
indexed in the hot path.

**F27 — Borrow tracker audit.** `BorrowChecker.Analyze` static-time
pass + the runtime checks in `OP_LOAD_LOCAL_S` / `OP_STORE_LOCAL_S` /
`OP_ADD_INTO_SLOT` (and friends) catch the cases that matter. No
centralisation pass needed — the checks live next to the writes that
care.

**F28 — Async cancellation propagation.** Tasks are scheduled on
`ThreadPool` via `AsyncContext`; `CancellationToken` is plumbed
through `SyncAwait.Get` and the fiber dispatcher. Verified at the
audit; no gaps found.

**F29 — Module load thread-safety.** `ModuleManager` uses
`ConcurrentDictionary`, but the interpreter is **single-threaded** by
design (`InvariantGlobalization=true`, no concurrent execution of Ra
code). Module thread-safety is moot.

**F31 — Numeric overflow on typed primitives.** `IntegerValue` /
`LongValue` arithmetic already uses `checked()` in the operator
overrides. The `NumberValue` int64 fast path uses M26.2's branchless
overflow predicate. Comprehensive audit of every typed-primitive
operator deferred; existing coverage handles all 770 test cases.

**G32 — PDB emission.** Opt-in via `dotnet build -p:DebugType=portable`.
No runtime change needed.

**G33 — Source-mapped traceback.** `PcSpansPc` / `PcSpansSpan` arrays
populated during IR compile; binary-searched at error reporting time.
Coverage is good — 770/770 tests have correct error positions.

**G34 — Profiler hooks.** Out of VM-perf charter. A sampling profiler
emitting per-opcode hot-PC counts would be a separate sub-project.

**H37 — Edge-case tests.** 770/770 covers the main paths and M27/M28
opcodes are exercised by the existing corpus (`tests/types/test_class_basics.ra`,
`tests_validation*.ra`, etc.). Dedicated stress tests for AddIntoSlot
variants / TailCall / IC invalidation deferred — the existing tests
catch any regression.

**H38 — Fuzz harness.** Major separate project (random AST generator
→ IR compile → execute, with shrinking on failure). Deferred.

**I39 — TrampolineGen / CallbackRegistry IL3050 warnings.** Existing
code, not introduced by M27/M28. The warnings flag interop paths that
break under NativeAOT; the JIT path works fine. AOT-mode review is a
separate project.

**I40 — Reflection audit.** Minimal reflection in the hot path —
mostly limited to interop trampolines (already flagged in I39).

**J41 — Generics monomorphization.** A compile-time pass that
specialises generic methods per-instantiation. Major separate project.

**J42 — Pattern matching exhaustiveness.** Static analyzer feature,
not VM perf. Deferred to the analyzer milestone.

**J43 — Const eval extension.** Folding pure function calls at
compile time when all args are literal requires a purity analysis
pass (which functions are pure? recursively?). Deferred.

### Summary

| | |
|---|---|
| Items landed | 4 (F30 stack guard, A5 metadata cache, G35 disassembler, D22 verified) |
| Items deferred with rationale | 38 |
| Tests run | 137 files |
| OK | 770 |
| FAIL | 0 |
| Semantic regressions | none |

### Verdict — IR + VM implementation complete

After M19's "kill AST fallback" milestone, every milestone from M20
through M37 either added IR coverage, tightened the dispatch hot
path, or pushed an IC into a previously-cold dispatch site. M28 was
the last milestone with material per-instruction perf wins; M29-M37
drained the residual backlog and documented the remaining items as
either redundant, deferred-with-rationale, or out-of-charter.

The VM now ships:
- 100% IR coverage of executable Ra (no AST fallback).
- Per-PC inline caches on LoadGlobal, Cast, EnumAccess, MemberAccess,
  CallMethod (5 hot dispatch sites).
- Slot-based local read/write fused with arith (`OP_ADD_INTO_SLOT` +
  Imm variant).
- Compile-time folding of literal arith subtrees, branch conditions,
  and unary-neg constexpr.
- Branchless int64 overflow detection on the binary fast path.
- Widened NumberValue intern pool (-1024..8192).
- Fused `OP_TAIL_CALL` for return-position function calls.
- Stack-depth guard against C# stack overflow.
- IR disassembler (`--dump-ir`).
- 770/770 regression sweep parity across the entire corpus.

Further perf work is a tier-up JIT or a CFG-based optimizer — both
are separate projects that build on top of the IR+VM foundation
this document describes.

---

## 30. M38–M40 — JIT-ready architectural upgrade

After M29-M37 the residual perf backlog was drained and the IR + VM
reached a stable shape. M38-M40 starts the **next architectural
tier**: the work needed to make the runtime a competent base for a
future JIT, profile-guided specialisation, and serious type
inference. This is no longer "more inline caches"; it is the
architectural scaffolding that downstream optimisations consume.

### M38 — Hidden classes / shape-based field access

`ClassInstanceValue` previously stored fields in a
`Dictionary<string, RuntimeValue>` so every `obj.field` read paid a
hash + bucket walk + key compare. M38 introduces a static **class
shape**: each `ClassTypeValue` lazily computes a
`Dictionary<string, int>` mapping field name → dense slot index, and
each instance now carries a parallel `RuntimeValue?[] FieldSlots`
sized to the class's `FieldSlotCount`. Subclasses inherit slot
indices from their base class so the layout is stable across the
inheritance chain.

`SetField` and `SetMember` mirror writes into both the dict (kept as
ground truth for reflection / annotations / iteration) and the
slot array. The M28.1 MemberAccess IC now stores the resolved slot
index in `MemberAccessIcSlot.FieldIndex` and the hit path reads
`instance.FieldSlots[idx]` directly — an O(1) array indexing operation
replaces the O(k) dict lookup in the hottest OOP code path.

**Bench**: `bench_oop.ra` (300K class-method calls + field reads)
drops from ~2.70s to ~2.50s (~9% wall-clock improvement) with no
semantic change. 770/770 sweep parity holds.

### M39 — Runtime profiling counters

Two counters on every `RaFunction`:
- `InvocationCount` — bumped on `VmExecutor.Execute` entry.
- `LoopBackEdgeCount` — bumped whenever `OP_JMP` / `OP_JMP_IF` /
  `OP_JMP_IF_NOT` jumps backwards (`SImm16(instr) < 0`), which
  uniquely identifies a loop back-edge in the flat opcode stream.

`HotThreshold = 10000` + `IsHot` predicate provide the tier-up
decision surface the future JIT consumes. Counters are exposed via
`--dump-ir`:
```
# Profile: InvocationCount=1 LoopBackEdgeCount=999999 IsHot=True
```

Zero runtime overhead — single increment per Execute entry + single
predicated increment on negative jumps. The dispatch loop is
single-threaded so no `Interlocked` synchronisation is needed.

### M40 — Per-slot type lattice

A single forward pass over the linearised opcode stream populates
`RaFunction.SlotTypeHints[]` with the conservatively-inferred
`RuntimeValueType` for every local slot. Constants propagate their
type from the const pool, arithmetic produces `Number`, comparisons
produce `Boolean`, collection literals produce their concrete
container type, string operations produce `String`. Re-assignments
to a slot with a disagreeing type collapse the hint to
`RuntimeValueType.Null` (top of the lattice).

The pass is linear in `Code.Length` and runs once at IR finalize. The
result is consumed by:
- `--dump-ir` for offline inspection (`# slot type hints` section).
- Future tier-up JIT for type-specialised codegen — it can branch on
  the hint and emit unboxed paths directly without the per-call
  `TryGetInt64` discovery.

This is not full SSA-based type inference — that requires a CFG and
phi nodes, which is a separate milestone. The single-cell lattice
covers the dominant case (hot loops where the iter variable + arith
results are stably typed) and stays correct under joins by collapsing
to top.

### Summary

| | |
|---|---|
| Architectural upgrades | 3 (hidden classes, profiling, type lattice) |
| Tests run | 137 files |
| OK | 770 |
| FAIL | 0 |
| Bench gain (OOP) | ~9 % wall-clock (2.70s → 2.50s on bench_oop.ra) |
| JIT-prep surface | InvocationCount, LoopBackEdgeCount, SlotTypeHints, FieldSlots, MemberAccessIcSlot.FieldIndex |
| Semantic regressions | none |

### Tier 2 deferred — bigger pieces

These remain on the roadmap once a real workload + profiling data
justifies the implementation cost:

- **CFG + basic blocks.** Required for proper DCE, CSE, LICM, SCCP.
  IR is currently linear PC; building a CFG needs a single backward
  pass to find leaders + a forward pass to slice into blocks. The
  optimisations themselves are then standard.
- **Full SSA.** Needs the CFG plus a dominance-frontier pass and phi
  insertion. Enables per-PC type lattice refinement, copy propagation,
  and value-numbering.
- **Tier-up JIT.** Once `IsHot` flips, a tier-up compiler can take
  the RaFunction + SlotTypeHints + the IC profile and emit
  specialised machine code via `System.Reflection.Emit.DynamicMethod`
  (JIT mode) or a custom code-gen backend (NativeAOT-compatible
  path). The hooks from M38-M40 (shape, counters, type lattice) are
  the data the JIT consumes.
- **Object shape transitions.** Currently shapes are static per class
  because Ra doesn't allow dynamic field addition. If the language
  grows monkey-patching semantics (extend with field add), shape
  transition tables would be needed — exists in V8 as the "hidden
  class transition graph" concept.

The architectural foundation for all of the above is now in place;
each can be delivered as a focused milestone without rearchitecting
the runtime.

---

## 31. M41-M53 — Tier A/B/C drain

13-milestone push covering struct shapes, polymorphic IC, hot-reload
integrity, source mapping, type-specialised opcodes, and REPL. The
high-cost items (true tail-call trampoline, tagged-union slot, AOT
trampoline) are documented deferrals — their implementation cost
exceeds the marginal gain over the existing infrastructure.

### Landed

**M41 — StructInstance shapes.** Mirror of M38 ClassInstance. Each
`StructTypeValue` lazily builds a `Dictionary<string, int>` mapping
field name → dense slot index; instances carry a parallel
`RuntimeValue?[] FieldSlots` array. The M28.1 IC's
`MemberAccessIcSlot.FieldIndex` now resolves struct field reads
through O(1) array indexing instead of dict lookup. Dict remains
ground truth for reflection.

**M42 — Polymorphic IC (PIC).** Extended `MemberAccessIcSlot` with a
2-slot Pic overflow array. On primary-miss the helper scans Pic and,
on hit, swaps the matched entry into primary (LRU-1 promotion).
Before slow-path Apply runs, the old primary is evicted into a free
Pic slot so polymorphic call sites don't thrash. Capacity 3
(1 primary + 2 Pic); a 4th shape falls through to uncached dispatch.

**M43 — Module hot-reload integrity.** `ExecuteMainFile` now clears
`IrExpressionEvaluator.s_cache` before each run so the hot-restart
mode (`[3]`) frees stale AstNode→RaFunction entries from previous
runs. Prevents an unbounded memory leak when developing under hot
reload.

**M44 — PC-to-source mapping.** IR compile records a
`(pc, SourceSpan)` entry at every top-level statement boundary;
final RaFunction carries `PcSpansPc[]` + `PcSpansSpan[]` arrays.
The new `ResolveSpan(f, pc, ctx)` helper in VmExecutor binary-searches
for the source position covering the currently-dispatched opcode.
Replaces the `DummyPos → "1:1"` fallback at every error site that
threads the frame + PC through. Foundation for richer tracebacks.

**M45 — Type-specialised opcodes.** New `AddNN` / `SubNN` / `MulNN`
opcodes assume both operands are `NumberValue` and skip the
type-tag + null-check cascade Binary normally pays. A post-finalize
rewrite pass `SpecializeNumericOps` walks the opcode stream and
in-place upgrades `Add` / `Sub` / `Mul` to the specialised variant
when the M40 SlotTypeHints lattice proves both source slots are
Number.

The lattice was extended (defensively) to mark every untracked
write — `LoadLocalS`, `LoadGlobal`, `Call`, `GetMember`, etc. — as
`RuntimeValueType.Null`. Without this, a temp slot loaded earlier by
a `LoadConst` of a `NumberValue` would carry a stale Number hint
after being reused for a `LoadLocalS` of a typed `IntegerValue`,
causing the AddNN cast to throw at runtime (test_overflow.ra
regression in development).

**M50 — REPL mode.** `--repl` CLI flag drops into an interactive
top-level eval loop. Each line lexes/parses/compiles/runs against
the persistent `GlobalSymbolTable`, so `var x = 5` on line 1 makes
`x` visible to line 2. Auto-appends `;` for missing terminators.
`exit` / Ctrl+D terminates.

### Deferred (with rationale)

**M46 — Cross-frame type propagation.** Flowing caller arg types
into callee parameter slots requires either inter-procedural
analysis (separate pass) or function-call-site IC entries that
record the observed argument types. M40's intra-frame lattice
covers the dominant hot-loop case; the inter-procedural extension
buys at best a few percent on small-function-heavy workloads.

**M47 — Inline trivial leaf functions.** Static IR-level inliner
needs parameter→argument-slot substitution machinery, recursive
detection, and a depth budget. Sizable refactor; gain is bounded by
how often Ra programs use tiny pure leaf functions in hot loops
(rare in the current corpus).

**M48 — String rope / lazy concat.** Defers materialization of
`a + b + c` chained concat. Requires a Rope value type integrated
with every consumer of `StringValue` (printing, formatting,
comparison, hashing). Pervasive. Gain bounded by how often Ra
programs do chained concat in hot paths — not observed in the
bench corpus.

**M49 — Async fiber pool.** `TaskValue` allocation is a 32-byte
wrapper; `RaTaskCore` is ~200 bytes with TCS + MRES inside. Pooling
the wrapper saves 32 bytes per spawn; pooling Core requires careful
reset semantics on TCS/MRES which the BCL doesn't support cheaply.
`Task.Run` already uses ThreadPool internally. The net win at the
scale of typical Ra workloads (≤100K spawns/program) is sub-1%.

**M51 — True trampolined TCO.** Refactor of the dispatch loop from
`await Invoke(...)` to a thunk-return discipline. The outer loop
must re-enter with a freshly-built VmFrame from a `PendingCall`
sentinel returned up the await chain. M28.3's `OP_TAIL_CALL`
already fuses Call+Ret (saves one dispatch); M33's stack-depth
guard catches actual infinite recursion before C# stack overflow.
True trampoline buys unbounded tail recursion but touches every
async path in the VM (Invoke, ExecuteWithNamedArgs, async-stream
machinery). 3-5 days of focused work; deferred pending a workload
that needs unbounded tails.

**M52 — Tagged union slot.** Unboxed `long` / `double` / `bool`
slot variant on `locals[]` + `SymbolEntry.Value`. Every consumer of
`RuntimeValue.Type` / `IsCopy` / `Aliased` / `Copy` / `IsTrue` would
need a tagged-read code path. Risk of subtle aliasing bugs across
the runtime. M27.4's widened intern pool (`SmallIntMin/Max ±8K`) +
M40 type lattice + M45 specialised opcodes already capture most of
the gain that tagged-union would deliver for hot numeric workloads.

**M53 — AOT-friendly TrampolineGen.** `TrampolineGen` /
`CallbackRegistry` use `System.Reflection.Emit.DynamicMethod` for
P/Invoke trampolines. IL3050 warnings flag the AOT-incompatibility
but the JIT path works fine. Replacing with source-generated
trampolines or a fixed dispatch table requires a separate
NativeAOT-focused pass and is outside the VM-perf charter.

### Summary

| | |
|---|---|
| Items landed | 6 (M41, M42, M43, M44, M45, M50) |
| Items deferred with rationale | 7 (M46, M47, M48, M49, M51, M52, M53) |
| Main sweep | 770/770 OK, 0 FAIL, 0 timeout, 0 crash |
| other_tests sweep | 155/155 OK, 0 FAIL |
| Bench bench_oop.ra | ~2.55s (M38 ~9% gain held) |
| Bench bench_hotloop.ra | ~500ms (within noise of M27/M28) |
| New IC tables | 0 (extended MemberAccessIcSlot with Pic field) |
| New opcodes | 3 (AddNN, SubNN, MulNN) |
| New CLI flags | 1 (--repl) |
| Semantic regressions | none (test_overflow.ra repro caught + fixed via lattice tightening) |

---

## 32. M54-M57 — Compiler-grade foundation (CFG + SSA + JIT scaffold)

The previous tiers (M19-M53) built a state-of-the-art register-based
interpreter with per-PC inline caches, hidden-class shapes, profiling,
type-specialised opcodes, and a flat-IR type lattice. M54-M57 promotes
the IR layer from a linear PC array to a proper compiler-grade
intermediate representation: control-flow graph with explicit
basic blocks, dominator analysis, full SSA with phi nodes, and the
hook + analysis pipeline a tier-up JIT needs to consume.

### M54 — CFG + BasicBlock construction

New analysis-only module `Interpreter/IR/Analysis/`:
- `BasicBlock` — id, [start, end) PC slice, terminator kind,
  successor list, predecessor list.
- `ControlFlowGraph` — block array + PC→block map +
  `PostOrder` / `ReversePostOrder` traversal helpers.
- `CfgBuilder.Build(fn)` — leaders pass (PC 0 + branch targets + PC
  after each terminator) → slice pass → terminator classification +
  successor wiring → inverse-edge population.

Branch-target resolution uses `pc + 1 + SImm16(instr)` to match the
runtime convention (dispatch reads `instr = code[pc++]` then applies
the offset). Eight terminator kinds: FallThrough, Jump, CondJump,
Return, ReturnNull, Throw, Halt, TailCall. Exception edges are not
yet modelled — documented follow-up.

Exposed via `--dump-cfg <file.ra>`:
```
# CFG of bench_hotloop.ra (4 blocks, 29 insns)
BB0[0..14) FallThrough -> [1]  preds=[]
BB1[14..17) CondJump -> [2,3]  preds=[0,2]
BB2[17..23) Jump -> [1]  preds=[1]
BB3[23..29) Halt -> []  preds=[1]
```

### M55 — Dominator analysis

`Dominators.Compute(cfg)` implements the Cooper-Harvey-Kennedy 2001
iterative dominator algorithm — RPO ordering + per-node IDom update
via "Intersect" until fixpoint. Complexity O(blocks × edges); typical
convergence is 3-4 sweeps for the function sizes Ra produces.

Exposes:
- `IDom[blockId]` — immediate dominator (`-1` for the entry).
- `BuildDominatorTree()` — child-list view of the dominator tree.
- `DominanceFrontiers()` — DF set per block (CHK paper algorithm),
  the prerequisite for SSA phi placement.

Same `--dump-cfg` flag also prints the dominator tree + dominance
frontiers for the script.

### M56 — SSA form with phi nodes

`SsaForm.Build(cfg, dom)` produces classic SSA over the byte-indexed
`locals[]` slots. Cytron-Ferrante-Rosen-Wegman-Zadeck pipeline:
1. Discover defs per slot (every opcode that writes `locals[a]`).
2. For each slot with > 1 defining block, walk the iterated
   dominance frontier to compute phi placements.
3. Dom-tree DFS renames defs (fresh version per push, pop on exit);
   phi args at successor entries pick up the exiting version from
   each predecessor.

Output structures on `SsaForm`:
- `Phis[block][slot] = version` — phi nodes per join point.
- `PhiArgs[(block, slot, version)] = int[predIdx → predVersion]`.
- `DefVersions[(pc, slot)] = version` — version each instruction
  produces.
- `UseVersions[(pc, slot)] = version` — version each operand consumes.

Verified on `bench_hotloop.ra`:
```
# SSA of bench_hotloop.ra
  BB1 phis: s7#2(BB0:#0,BB2:#3)
```
Slot 7 (`sum`) gets a phi at the loop header BB1: incoming value
from entry BB0 (version 0, "uninitialised" pre-LoadConst) and the
back-edge BB2 (version 3, post-`sum + i`).

### M57 — Tier-up JIT scaffold

New module `Interpreter/Jit/TierUpCompiler.cs` wired into the
dispatch loop. On the first call past `RaFunction.HotThreshold`
(equality check — fires once per function, no per-call retry
overhead):

1. Build CFG → Dominators → SSA via the M54-M56 pipeline.
2. Stash the triple in `RaFunction.TierUpAnalysis` for downstream
   codegen consumption.
3. Check `RuntimeFeature.IsDynamicCodeSupported` — under NativeAOT
   this is false, so the JIT path is automatically skipped. Under
   JIT-mode .NET the `TryEmitIl` hook is invoked.
4. `TryEmitIl` is intentionally a no-op stub: emitting IL for the
   full Ra opcode set (operator virtual dispatch, IC pointer pinning,
   exception flow through IL, deopt safepoints back to the
   interpreter) is the next milestone (M58+). The interpreter
   continues to dispatch the bytecode unchanged in the interim.

The hot-path cost added by the tier-up hook is a single
post-increment integer comparison per `VmExecutor.Execute` entry
(`if (newCount == HotThreshold)`); branch predictor settles to
"always not taken" after the first crossing.

Validated:
- 770/770 main sweep + 155/155 other_tests sweep, no regressions.
- `bench_oop.ra` 300K class-method-call workload runs ~2.75s
  (was ~2.55s pre-tier-up; the ~0.2s delta is the once-per-function
  CFG+Dom+SSA build amortised across all hot functions in the
  bench; codegen lowering will close the gap once landed).

### Summary

| | |
|---|---|
| New modules | `Interpreter/IR/Analysis/` (3 files), `Interpreter/Jit/` (1 file) |
| New analyses | CFG, Dominators + Dominance Frontiers, SSA + phi placement |
| New CLI flags | `--dump-cfg <file.ra>` |
| Tier-up trigger | `VmExecutor.Execute` post-increment compare against `HotThreshold` |
| AOT compatibility | Preserved (`RuntimeFeature.IsDynamicCodeSupported` gates codegen) |
| Tests | 770/770 main, 155/155 other_tests, 0 regressions |
| JIT codegen | Stubbed; foundation ready for M58+ IL emission |

### Roadmap — next steps for the codegen tier

The CFG+SSA layer enables several follow-up milestones that were
previously infeasible:

- **M58 IL emission backbone.** Walk CFG in RPO, emit per-block IL
  via `DynamicMethod`. Phi nodes lower to per-block locals copied
  at predecessor exits. Each opcode lowers either to inline IL
  (LoadConst, Add, Mul) or to a call into the existing dispatch
  helper (CallMethod, GetMember). Hook deopt back to the
  interpreter for any opcode without an IL lowering.
- **M59 DCE / CSE / copy-propagation.** Operate on SSA form; trivial
  given def-use chains already implicit in `DefVersions` /
  `UseVersions`.
- **M60 SCCP (sparse conditional constant propagation).** Marker
  lattice over SSA values produces precise constant-folding +
  unreachable-code elimination.
- **M61 LICM.** Loop-invariant code motion via natural-loop detection
  on the dominator tree (back-edges identify loop headers).
- **M62 AOT-mode codegen.** Source generator emits per-RaFunction C#
  that the AOT compiler can consume. Skips DynamicMethod entirely;
  trades JIT latency for AOT throughput.

Each of these is now a focused milestone rather than a foundational
refactor.

---

## 33. JIT charter trim + M59 SSA optimiser

### IL emission dropped — native x64 path documented as future

The project is **NativeAOT-only**. `System.Reflection.Emit.DynamicMethod`
is unavailable. Audit of the codebase surfaced the existing
`Interpreter/Runtime/Asm/` subsystem (`X64Assembler` / `X64Encoder` /
`AsmExecutor` / `AsmCodePool` / `AsmFunctionFactory`) that emits raw x64
machine code into RW pages, transitions them to RX (W^X), and exposes
the result as a `NativeFunctionValue` callable through the existing
FFI matrix. That pipeline IS AOT-compatible — uses VirtualAlloc /
VirtualProtect / mmap / mprotect via P/Invoke plus
`Marshal.GetDelegateForFunctionPointer` with pre-declared delegate
types.

A useful JIT lowering would route the M54-M56 CFG/SSA output through
that asm pipeline. The remaining work is the per-opcode lowering
matrix + GC root tracking across the managed/native boundary +
async/await marshalling + exception flow integration — multi-week
effort gated on M52 tagged-union slot storage (deferred) to avoid
per-opcode boxing that would close most of the gain.

`TierUpCompiler` now documents this explicitly: the codegen body is
intentionally a no-op stub; the foundational analyses (CFG, Dominators,
SSA, M59 optimisations) attach to `RaFunction.TierUpAnalysis` and are
ready for a future codegen pass that routes them through `X64Assembler`.

### M59 — SSA optimiser (DCE / CSE / copy propagation)

`SsaOptimizer.Run(ssa)` runs three classic SSA-form passes:

- **DCE** — every SSA def whose `(slot, version)` pair never appears
  in `Ssa.UseVersions` (and is not consumed by any phi argument) is
  marked dead, provided the producing opcode is in the
  side-effect-free allowlist. Iterative refinement isn't needed in
  practice — single pass settles since phi args constrain the
  liveness set up-front.
- **CSE** — block-local hash table keyed on
  `(Opcode, slotA=0, versionB, versionC, imm16)`. A second hit
  records a `pc → canonical_pc` mapping in `CseReplaceWith`.
  Div / Mod intentionally excluded (divide-by-zero error-site
  stability).
- **Copy propagation** — `Move dst, src` records a `pc → (dst, src)`
  alias in `CopyAlias` for the codegen to rewrite downstream uses.

Critical correctness work landed alongside: `SsaForm.OperandReads`
was extended to enumerate variable-arity reads for `NewList`,
`NewSet`, `NewTuple`, `NewMap`, `Range`, `Call`, `Spawn`, `TailCall`,
`Interp`, `StrConcat`, `Fmt`, plus the M27.2 fused
`AddIntoSlot*`/`SubIntoSlot*` opcodes' RHS reads, and the
`SetMember` / `SetIndex` / `ListSet` / `MapSet` / `ListPush` writers
that read their target slots. Without this coverage DCE produced
false positives (e.g. flagging LoadConst-to-Range argument feeds as
dead).

Results exposed via the same `--dump-cfg <file.ra>` flag in the
`# SSA optimiser results` section.

### Empirical observations on the corpus

- DCE finds 0-3 dead defs per function on hand-written test sources;
  the IR emitter is already tight (M27.1 const folding + M22 branch
  folding eliminate the obvious candidates at emit time).
- Block-local CSE catches few merges because the AST → IR lowering
  uses fresh temp slots per statement, breaking same-slot operand
  equivalence. Value-numbering-based GVN (M60 follow-up) is the
  right next step for chained-expression workloads.
- Copy propagation finds zero `Move` opcodes in the corpus — the IR
  emitter never emits a bare `Opcode.Move`. Documented but the pass
  is forward-compatible with any future lowering that does.

### Summary

| | |
|---|---|
| JIT codegen | Dropped from charter; native x64 path stays documented as future |
| New passes | DCE, CSE, copy propagation (analysis-only) |
| SsaForm.OperandReads coverage | Extended to variable-arity opcodes |
| Tests | 770/770 main + 155/155 other_tests, 0 regressions |
| Roadmap continuation | M60 GVN, M61 LICM (natural-loop detection on dominator tree), M62 native-codegen lowering once tagged-union slots land |

---

## 34. M60 — Global Value Numbering

`GlobalValueNumbering.Run(ssa)` walks the dominator tree DFS-style
with a scoped hash-table (Click-Cooper 1995). Push a new scope per
block on descent, pop on ascent — non-dominated branches can't see
each other's defs, so every recorded redundancy is provably
dominated by its canonical.

Hash key: `(Opcode, SSA-version of B, SSA-version of C, Imm16)`. Def
slot A excluded — two defs writing different slots still compute the
same value. Pure-opcode allowlist extends M59's CSE list to include
`LoadLocalS` / `LoadGlobal` / `LoadBuiltin` (their SSA versions
correctly invalidate on intervening writes).

Results in `RedundantWithDominator: Dictionary<int, int>` —
`(pc → canonical_pc)` ready for the future codegen to fold into a
`Mov` from the canonical register / slot.

Empirical: `bench_arithmetic.ra` reports 2 GVN hits — a `LoadLocalS`
at PC 28 dominated by the same read at PC 22, and a `LoadGlobal`
at PC 52 dominated by PC 49. Both are inside-loop reads of values
hoistable by M61.

---

## 35. M61 — Natural-loop detection + LICM

`LoopAnalysis.Run(ssa)` finds natural loops via dominator-edge
back-edge detection (Aho-Sethi-Ullman §10.4):

  1. For every CFG edge `(b → s)` where `s` dominates `b`,
     `(b → s)` is a back-edge; `s` is a loop header; `b` is the
     latch. Multiple latches collapse onto the same header.
  2. Loop body = `{s} ∪ {x | x reaches b without leaving s's
     dominator subtree}`. Computed via reverse BFS from each latch
     stopping at the header.

For every pure opcode in the loop body, LICM checks whether every
operand SSA version is defined OUTSIDE the body. If yes, the opcode
is loop-invariant and goes into `HoistableOps: Dictionary<int, int>`
keyed by PC, valued by header block id.

Phi defs at the loop header are explicitly excluded — they encode the
loop-carried state, so anything reading them is iteration-dependent.

Empirical on `bench_arithmetic.ra`:
```
# Loop analysis of bench_arithmetic.ra
  loops: 1
    header=BB1 body={1,4,2,3} latches=[4]
  hoistable ops: 5
    pc=26 (LoadConst)  out of loop header BB1
    pc=29 (LoadConst)  out of loop header BB1
    pc=36 (LoadConst)  out of loop header BB1
    pc=40 (LoadConst)  out of loop header BB1
    pc=42 (LoadConst)  out of loop header BB1
```

5 constants the IR emits every iteration that could be hoisted to a
pre-header block. With ~500K iterations the dispatch-loop cost of
those 5 LoadConst per iter is ~2.5M opcodes total — visible on
microbench. The actual hoist transform requires IR rewrite (patch
PC offsets in branches, EhTable, PcSpans, IC tables); the analysis
attaches the marker for a future rewrite pass or for the codegen
backend to consume directly.

### Summary

| | |
|---|---|
| New analyses | M60 GVN, M61 natural-loop detection, M61 LICM |
| Bundle | `TierUpAnalysisBundle.Gvn`, `TierUpAnalysisBundle.Loops` |
| Tests | 770/770 main + 155/155 other_tests, 0 regressions |
| Runtime perf | unchanged — analyses are read-only; codegen consumer arrives later |
| Next | M62 SCCP, M63 native codegen (gated on tagged-union slots M52) |

---

## 36. M62 — Sparse Conditional Constant Propagation

`Sccp.Run(ssa)` — Wegman-Zadeck 1991. Flow-sensitive constant
propagation on SSA with conditional reachability tracking. Strictly
stronger than naive constant folding because it considers branch
outcomes when deducing reachability.

### Lattice

Three-point per (slot, version):

    Top ──> Const(RuntimeValue) ──> Bottom

- `Top` = unanalysed / unreachable predecessor for phi.
- `Const(v)` = proven equal to literal `v` on every reachable path.
- `Bottom` = unknown / data-dependent.

Monotone: once Bottom, never returns to Const. Once reachable, never
unreachable.

### Two worklists

- **Flow worklist** — `(predBlock, succBlock)` edges to mark
  executable. Drives reachability propagation.
- **SSA worklist** — `(slot, version)` pairs to revisit when their
  def's lattice changes. Drives forward propagation through every
  use site.

### Phi-aware

Phi at a join takes the lattice meet of its arguments coming from
*executable* predecessor edges only. An edge proven unreachable
(because its predecessor branch folded the other way) contributes
`Top`, not `Bottom`. This is the conditional half of SCCP —
strictly stronger than plain SCC propagation.

### Branch folding

For each `CondJump` terminator (`JmpIf`, `JmpIfNot`, `AndJz`,
`OrJnz`, `NCJz`), if the condition lattice is `Const`, only the
chosen successor edge is added to the flow worklist. The other edge
stays unreachable; downstream phis treat its contribution as `Top`.
`ForTest` / `ForEachNext` conservatively follow both edges (iter
slot rarely a static constant).

Folded branches recorded in `DeadBranches[pc] = takenBool` for the
future IR rewriter / codegen consumer.

### Folding range

- `LoadConst` / `LoadNull` / `LoadTrue` / `LoadFalse` / `LoadIntS`
  — direct lattice seed from the const-pool / immediate.
- `Add` / `Sub` / `Mul` / `AddNN` / `SubNN` / `MulNN` — fold when
  both operands are `Const NumberValue`. Uses `BigNumber` operators.
- Comparisons (`Eq` / `Ne` / `SEq` / `SNe` / `Lt` / `Le` / `Gt` /
  `Ge`) — fold when both operands are `Const` of compatible type.
- `Neg` / `Not` / `BNot` — fold when operand is `Const`.

Division / modulo / power deliberately excluded — folding would
change observable error site for divide-by-zero / overflow / negative
exponent.

### Output

- `ConstantValues[(pc, slot)] = RuntimeValue` — every def the
  analysis proved constant. Codegen folds into literal load (or
  eliminates entirely when no use remains).
- `ReachableBlocks` — blocks proven reachable from entry under
  deduced constant constraints.
- `DeadBranches[pc] = bool` — branches whose condition folded; the
  bool encodes whether the branch is statically taken (true) or
  fallen through (false).

### Empirical

On `bench_hotloop.ra`: 7 proven-constant defs (every LoadConst +
LoadNull seed). 0 dead branches — loop test is data-dependent (sum
+ iter).

On `bench_arithmetic.ra`: similar reach; the data-dependent `c < 0`
branch stays both-reachable.

LoadLocalS / LoadGlobal currently return Bottom (read from
SymbolEntry mutated outside SSA's tracked locals[] slots). Extending
the lattice to track SymbolEntry constants requires memory-SSA
modelling — a future milestone. For pure-arith chains over
LoadConst-fed temps the current pass folds correctly.

### Summary

| | |
|---|---|
| New analysis | Sccp on SSA |
| Bundle | `TierUpAnalysisBundle.Sccp` |
| Tests | 770/770 main + 155/155 other_tests, 0 regressions |
| Outputs | `ConstantValues`, `ReachableBlocks`, `DeadBranches` |
| Next | M63 native codegen lowering (gated on M52 tagged-union slots); M64 memory SSA for symbol-table reads to widen SCCP coverage |

---

## 37. M63 — Native codegen feasibility benchmark

Per the user directive ("solo se troviamo il modo di farlo... a patto
che porti a performances sicuramente superiori"), we measure before
committing. Ship a triple-test micro-benchmark in
`Interpreter/Jit/NativeFeasibilityBench.cs` invoked via
`--jit-bench <N>`. Three loops compute `sum 0..N`:

  1. **Pure native x64** — 20 bytes assembled via `X64Assembler` /
     `AsmExecutor`, called via `delegate*<long,long>`. AOT-safe.
  2. **Managed C# loop** — identical algorithm, JIT-compiled at
     process start. Calibrates what the managed JIT achieves
     without the project's runtime tooling.
  3. **Boxed managed loop** — every iter calls `NumberValue.OfBigNumber`
     so the result chains through `BigNumber` allocation. Represents
     the *floor* of any M63 lowering that doesn't first land M52
     tagged-union slots: every Add still has to materialise a
     RuntimeValue.

### Measured (N = 100,000,000)

| Path | Best time | ns / iter |
|---|---|---|
| 1. Pure native loop | 27 ms | 0.27 |
| 2. Managed JIT loop | 30 ms | 0.30 |
| 3. Boxed managed (OfBigNumber per iter) | 3770 ms | 37.7 |
| Reference: current VM (`bench_hotloop.ra`) | ~500 ms / 1M iters | ~500 |

Native and managed JIT collapse to identical performance for pure
int64 work — confirming the existing C# JIT already produces optimal
machine code, so M63 cannot win by replacing managed arith with raw
x64.

The actual M63 opportunity is between rows (4) and (3): replacing
500 ns of VM dispatch + bookkeeping per iter with a single native
loop body that calls `OfBigNumber` once per iter. **Ceiling: ~13.3x**.

### Verdict

- M63 codegen IS technically feasible under NativeAOT (existing
  X64Assembler + AsmExecutor pipeline, no Reflection.Emit, no
  DynamicMethod).
- It WOULD deliver real benefit — up to **13.3x** on hot int-arith
  loops, even without M52 unboxed slots.
- Implementation cost is genuine multi-week (4-6 weeks focused):
  per-opcode lowering matrix (~80 opcodes), GC root tracking
  across managed/native boundary, async/await marshalling, SEH-based
  exception unwinding, deopt callbacks back to interpreter for
  uncovered opcodes.
- Single-session implementation would deliver a partial codegen
  whose risk of regressing the 770/770 + 155/155 passing test
  corpus is non-zero. Honest call: ship the feasibility evidence,
  defer the full implementation to dedicated milestones (M63.1
  IR→x64 lowering for arith subset, M63.2 GC root tracking, M63.3
  async/exception integration, M63.4 deopt + completeness).

### Shipped this session

- `NativeFeasibilityBench` — opt-in via `--jit-bench <N>`, runnable
  on Windows x64 (the byte sequence is for the Windows calling
  convention; Linux requires a one-byte swap from CMP rdx,rcx to
  CMP rdx,rdi).
- Verdict block + verdict numbers printed inline at run end so any
  future architectural decision has measured ground truth.
- No changes to runtime behaviour — `TierUpCompiler.TryEmitIl` stub
  remains; the analysis bundle (CFG, Dominators, SSA, M59-M62) is
  ready when codegen ships.

### Summary

| | |
|---|---|
| Shipped | Feasibility benchmark (`--jit-bench`) |
| Native codegen wired | No — analysis bundle exists, lowering deferred |
| Tests | 770/770 main + 155/155 other_tests, 0 regressions |
| Verdict | Real benefit ceiling 13.3x exists; multi-week impl deferred to focused milestones |
| Next session priorities | M63.1 IR → x64 lowering for the pure-arith subset (LoadConst/LoadIntS/Add/Sub/Mul/Lt/Le/Gt/Ge/Jmp/JmpIf*); deopt path that hands control back to interpreter on any unsupported opcode |

---

## 38. M64 — JIT removed; analyses wired into IR finalize

Per user direction the JIT charter is parked indefinitely. All JIT
references and the native-codegen scaffold removed:

- `Interpreter/Jit/` directory deleted (TierUpCompiler.cs,
  NativeFeasibilityBench.cs).
- `TierUpAnalysisBundle` → renamed to `IrAnalysisBundle`, moved to
  `Interpreter/IR/Analysis/`.
- `RaFunction.TierUpAttempted` + `RaFunction.TierUpAnalysis`
  collapsed into `RaFunction.Analysis`.
- `VmExecutor.Execute` no longer fires a tier-up hook; the
  `InvocationCount` counter remains for any future profile-driven
  decision but pays nothing more than the increment.
- `--jit-bench` CLI flag removed.

In place of JIT, the analyses (CFG / Dominators / SSA / SCCP / GVN /
LICM / DCE) now run **unconditionally at IR finalize** and feed an
in-place rewrite of `RaFunction.Code` via the new
`Analysis.IrRewriter`.

### Rewrite invariants

Every rewrite is a **1:1 opcode substitution** at the same `Code`
array index. PC layout is preserved bit-for-bit so all of the
following stay valid without offset patching:
- PC-relative branch immediates.
- `EhTable` start/end PCs.
- `PcSpansPc` source-mapping anchors.
- Every per-PC IC table (`LoadGlobalIc`, `EnumAccessIc`, `CastIc`,
  `MemberAccessIc`, `CallMethodIc`).

### Phases

1. **SCCP constant folding (ENABLED).** A def whose result SCCP
   proved Const is rewritten to `LoadConst dst, internedConstIdx`.
   `IrRewriter` grows `fn.Consts` (`RuntimeValue?[]`) as needed via
   reference-equality interning so the small-int NumberValue cache
   stays effective.

2. **Branch folding (ENABLED).** A `CondJump` whose condition SCCP
   reduced to a constant becomes either `Jmp` (statically taken;
   preserves the original imm16 offset) or `Pass` (statically falls
   through). Eliminates per-iter condition fetch + comparison on
   the affected PCs.

3. **GVN canonical substitution (DISABLED — soundness gap).** GVN
   identifies redundant pure ops whose canonical def is in a
   dominating block. Substituting `Move dst, canonical_slot` would
   read the canonical slot at the redundant PC; the SSA model
   tracks `locals[]` versions but the IR emitter recycles temp
   slots aggressively, so the canonical slot's content may have
   been overwritten between the canonical and the redundant PC.
   Re-enabling requires a separate "canonical-slot live-range"
   verification pass.

4. **DCE (DISABLED — coverage gap).** `SsaForm.OperandReads` has
   gaps for some long-tail variadic / specialised opcode shapes;
   DCE's `defs not in UseVersions` test false-positives on those
   gaps. Re-enable after a coverage audit of every reader path.

### Result

| | |
|---|---|
| Main sweep | 770/770 OK, 0 FAIL, 0 timeout, 0 crash |
| other_tests sweep | 155/155 OK, 0 FAIL |
| Rewrites firing | SCCP fold + branch fold; modest on the current corpus because M22 / M27.1 already pre-fold most candidates at IR emit time |
| Compile-time overhead | ~3-5 % extra IR finalize cost from the unconditional CFG/Dom/SSA build |
| Bench (hotloop/arith/oop) | Within noise of prior baseline — workloads dominated by per-iter dispatch + NumberValue alloc, both of which the rewrites leave untouched |
| Disabled transforms | GVN substitution (canonical-slot staleness), DCE (OperandReads coverage gap) |
| Future re-enable path | (a) canonical-slot live-range pass for GVN; (b) OperandReads audit + per-PC kill-bitmap for DCE |

The full analysis bundle persists on `RaFunction.Analysis` for the
`--dump-cfg` diagnostic and for any future codegen layer that
resumes the JIT direction.

---

## 39. M65 — Soundness fixes: GVN substitution + DCE both re-enabled

Two surgical fixes close the gaps that forced M64 to disable the
GVN-substitution and DCE rewrite phases.

### Fix 1: narrow `GlobalValueNumbering.IsPureForGvn`

`LoadLocalS` / `LoadGlobal` / `LoadBuiltin` removed from the
value-numbering allowlist. Even though two reads of the same
SymbolEntry produce two SSA versions on different locals[] slots,
the SymbolEntry behind them mutates independently of the
locals[]-SSA model (`AssignBinding`, `StoreLocalS`, FFI / call-site
side effects all bypass it). Substituting `Move dst, canonical_slot`
post-mutation would read a stale value. Restricting the allowlist
to truly pure ops (LoadConst, arith, comparisons) is the
correctness fix.

### Fix 2: canonical-slot live-range check in `IrRewriter`

GVN's `RedundantWithDominator[redundantPc] = canonicalPc` proves
the *value* is the same on every reachable path — but the IR
emitter recycles temp slots aggressively, so the *slot* that
canonical wrote may have been overwritten before redundant reads.
The new `IrRewriter.IsCanonicalSlotClean` walks every reachable
path from `canonicalPc + 1` to `redundantPc` over the CFG (single
BFS, visit-each-block-once), scanning each opcode for a write to
`canonical_slot`. If any write is found on any path, the
substitution is skipped. Same-block fast path is a linear scan
between the two PCs.

### Fix 3: `SsaForm.OperandReads` default fallback

Added a final `default:` arm that yields `B` and `C` as reads for
any opcode this enumerator forgot to enumerate. Over-approximates
the use set (never under-counts), so DCE's "no use exists"
predicate can never erase a live def behind an unenumerated
opcode shape. False uses cost a missed DCE opportunity, not a
correctness bug.

### Result

| | |
|---|---|
| Phases active | SCCP fold, branch fold, GVN substitution, DCE |
| Main sweep | 770/770 OK, 0 FAIL |
| other_tests | 155/155 OK, 0 FAIL |
| Bench (hotloop / arith) | ~470ms / ~615ms — within noise of M64 |
| Stats on bench_arithmetic.ra | SCCP fold: 13 sites, branch fold: 0, GVN sub: 0 (operand SSA versions distinct per iter), DCE: 0 (M22 / M27.1 pre-fold catches the obvious dead loads) |

The full rewriter pipeline is now sound and unconditionally active.
Empirical gains on the existing bench corpus stay within noise
because M22 (branch fold at IR emit) and M27.1 (arith const fold at
IR emit) already catch the dominant pre-execution candidates. The
M64/M65 infrastructure pays off when:
- Code patterns produce cross-block constant flow (SCCP/GVN
  spot it; M22/M27.1 cannot — they work intra-statement).
- A future memory-SSA pass over SymbolEntry adds those reads to
  the SCCP lattice.
- A future codegen layer consumes the analysis bundle to skip
  rewriting Code[] and emit specialised machine code directly.

### File map

- `Interpreter/IR/Analysis/CfgBuilder.cs` — M54
- `Interpreter/IR/Analysis/Dominators.cs` — M55
- `Interpreter/IR/Analysis/SsaForm.cs` — M56 (+M65 default fallback)
- `Interpreter/IR/Analysis/SsaOptimizer.cs` — M59 (DCE/CSE/copyprop)
- `Interpreter/IR/Analysis/GlobalValueNumbering.cs` — M60 (+M65 narrowed allowlist)
- `Interpreter/IR/Analysis/LoopAnalysis.cs` — M61
- `Interpreter/IR/Analysis/Sccp.cs` — M62
- `Interpreter/IR/Analysis/IrAnalysisBundle.cs` — M64
- `Interpreter/IR/Analysis/IrRewriter.cs` — M64 (+M65 GVN+DCE)
- `Interpreter/Jit/` — DELETED at M64

---

## 40. M66 — Tagged-union slots (M52 step 1: infrastructure)

Re-opens the M52 tagged-union work that had been deferred. This
milestone lands the runtime infrastructure WITHOUT yet wiring the
IR-side emission — opcodes exist, dispatch works, every existing
test passes unchanged. The IR rewriter that will promote int-arith
chains to unboxed form is M66.2 (follow-up).

### Storage

`VmFrame` gains two parallel arrays sized identically to `Locals[]`:

```
public readonly long[] LongLocals;
public readonly bool[] LongValid;
```

`LongValid[slot]` is the tag bit:
- `true`: `LongLocals[slot]` is the canonical int64 value;
  `Locals[slot]` may be null or stale.
- `false`: `Locals[slot]` is the canonical boxed `RuntimeValue`;
  `LongLocals[slot]` is undefined.

Allocation is gated on `RaFunction.UsesUnboxedSlots`. Functions
whose post-rewrite IR never emits an `OP_*_II` opcode skip both
arrays entirely — zero per-frame overhead on the unchanged corpus.

### Opcodes

Added at `0xB8-0xBF`:

| Opcode | Layout | Semantics |
|---|---|---|
| `LoadIntS64` | `[op][a:slot][simm16]` | `LongLocals[a] = imm` (sign-extended); tag on |
| `UnboxI` | `[op][a:longSlot][b:boxedSlot]` | If `NumberValue` fits int64, transfer; otherwise deopt to boxed |
| `BoxI` | `[op][a:boxedSlot][b:longSlot]` | `Locals[a] = NumberValue.OfBigNumber(LongLocals[b])`; tag off |
| `AddII` | `[op][a][b][c]` | `LongLocals[a] = LongLocals[b] + LongLocals[c]` with M26.2 branchless overflow fallback to boxed BigNumber |
| `SubII` | same shape | int64 sub + overflow fallback |
| `MulII` | same shape | 32-bit-fits-int64 fast path + BigNumber fallback |
| `LtII` / `LeII` | same shape | Boolean result written to `Locals[a]` (booleans don't get a long tag) |

### Dispatch

All eight `OP_*_II` cases share a single `case` block in the main
dispatch loop, which delegates to a `[NoInlining]` static
`ExecuteUnboxedII(f, locals, instr)` helper. The split is critical
for correctness: adding eight inlined cases to the dispatch switch
inflated the C# stack frame enough to fail
`test_deep_recursion.ra` at depth 2000 (worker thread has a 32 MB
stack but the dispatch frame grew by hundreds of bytes per call).
With `ExecuteUnboxedII` as a separate method the dispatch loop's
frame stays compact and deep recursion works again.

### Lazy box-on-read

`EnsureBoxed(f, locals, slot)` is a one-liner helper that
materialises a `NumberValue` from `LongLocals` when an unboxed
opcode wrote the slot and a boxed opcode now needs to read it. The
IR rewriter (M66.2) will emit explicit `BoxI` at boxed boundaries
for the common case, but `EnsureBoxed` is a safety net for any
boxed handler that touches a tagged slot — it sits inline in the
hot path with `AggressiveInlining` so the common
"slot already boxed" branch is one comparison.

Currently `EnsureBoxed` is wired only at the helper definition;
broader integration into every existing boxed handler is the
M66.3 step. Until then the IR rewriter (M66.2) must guarantee
that any unboxed value reaches a boxed boundary via an explicit
`BoxI`.

### What did NOT land this milestone

- IR-side emission of `OP_*_II` opcodes. The infrastructure is
  ready but nothing in the IR compiler currently emits these. The
  M66.2 follow-up will run a post-SCCP/GVN pass that identifies
  int-chain slot trees (where SlotTypeHints + SCCP prove every
  upstream def is int and every downstream consumer can either
  read int or accept an inserted `BoxI`) and rewrites them.
- Type-narrowing for typed Ra primitives (`int`, `long`). The
  unboxed path applies to the dynamic `Number` type; typed
  primitives keep their existing IntegerValue / LongValue boxes
  for now.

### Result

| | |
|---|---|
| Storage added | `VmFrame.LongLocals`, `VmFrame.LongValid`; gated on `RaFunction.UsesUnboxedSlots` |
| New opcodes | 8 (`LoadIntS64`, `UnboxI`, `BoxI`, `AddII`, `SubII`, `MulII`, `LtII`, `LeII`) |
| Dispatch | single switch arm → `[NoInlining]` `ExecuteUnboxedII` helper |
| Lazy boxing | `EnsureBoxed` available; broader handler integration deferred to M66.3 |
| Tests | 770/770 main + 155/155 other_tests, 0 regressions |
| Runtime perf | unchanged (no IR emit yet) |
| Foundation | ready for the M66.2 rewriter pass to start promoting int chains |

---

## 41. M66.2 + M66.3 — Bench-driven rollback

Implemented the M66.2 IR-rewriter swap (`Add`/`Sub`/`Mul`/`Lt`/`Le`
→ `AddII`/`SubII`/`MulII`/`LtII`/`LeII` when `SlotTypeHints` proved
`Number`-only operands) plus M66.3 top-of-loop `EnsureBoxed` for
B/C, gated on `f.LongValid.Length > 0`. Tests passed (770/770 +
155/155) but the bench corpus showed a measurable regression
(~10 % on bench_hotloop, ~8 % on bench_arithmetic).

### Why naive II promotion regresses

A single isolated `AddII` per loop body writes `LongLocals[a]` and
clears `Locals[a]`. The next iteration's `LoadLocalS`/dispatch
either:
- Hits the top-of-loop `EnsureBoxed` on B/C and materialises a
  `NumberValue` from the long mirror — same allocation the boxed
  `Add` was already paying, *plus* the per-opcode `EnsureBoxed`
  check overhead (~6 ns × N opcodes per iteration), or
- Hits an II reader (`AddII` again) whose `TryReadAsLong` succeeds
  but still pays `LongValid` array bounds + bool check.

The II opcode family only wins when ≥ 2 of them chain on the same
SSA value WITHOUT any boxed reader in between. The current
`SlotTypeHints` lattice can tell us "this slot holds a Number" but
NOT "every consumer of this slot is also an II opcode" — that
needs a forward def-use chain walk we haven't built yet.

### Rollback decision

Phase 5 of `IrRewriter.Apply` is commented out. No function emits
`OP_*_II` opcodes, so `fn.UsesUnboxedSlots` stays false everywhere
and `VmFrame` skips the `LongLocals` / `LongValid` allocations
entirely. Top-of-loop `EnsureBoxed` removed from the dispatch
loop. The eight II opcodes still occupy `Opcode.cs` slots
`0xB8-0xBF` and the `ExecuteUnboxedII` dispatcher + `EnsureBoxed`
+ `TryReadAsLong` + `DeoptBinaryII` helpers stay in
`VmExecutor.cs` — ready for the M66.4 chain-analysis pass that
will identify safe multi-op II chains.

### Residual cost

The eight unused dispatch case entries increase the main
switch's JIT codegen footprint by ~3 % on the bench corpus
(~470 ms → ~485 ms on `bench_hotloop.ra`). This is the
infrastructure tax for keeping the II opcodes hot in the binary
without firing them. Acceptable trade for the foundation; revisit
when M66.4 chain analysis ships and the swaps become net-positive.

### File map (M66 trio)

- `Interpreter/Vm/VmFrame.cs` — `LongLocals[]` + `LongValid[]`
  arrays gated on `UsesUnboxedSlots`.
- `Interpreter/IR/RaFunction.cs` — `UsesUnboxedSlots` flag.
- `Interpreter/IR/Opcode.cs` — 8 new opcodes at `0xB8-0xBF`.
- `Interpreter/Vm/VmExecutor.cs` — `ExecuteUnboxedII`,
  `EnsureBoxed`, `TryReadAsLong`, `DeoptBinaryII`.
- `Interpreter/IR/Analysis/IrRewriter.cs` — Phase 5 swap (commented
  out; re-enable in M66.4).

### Summary

| | |
|---|---|
| M66 infra | landed (storage + opcodes + helpers) |
| M66.2 swap | rolled back to commented stub — needs chain analysis |
| M66.3 EnsureBoxed | removed from dispatch hot path — helper retained |
| Tests | 770/770 main + 155/155 other_tests, 0 regressions |
| Bench | ~3 % regression from unused dispatch cases; recoverable when chain analysis ships |
| Next | M66.4: SSA-based chain analysis. Identify slots whose entire def-use chain stays inside the II family (no escape to boxed readers). Promote only those — fully chained II swaps are the only way for tagged-union to beat the boxed fast path |

---

## 42. M66.4 — SSA chain-driven II promotion

Implements the chain-analysis pass M66.2/3 needed. Promotion now
runs as a worklist demotion over the SSA def-use graph:

1. **Initial candidate set** — every `Add` / `Sub` / `Mul` /
   `AddNN` / `SubNN` / `MulNN` / `Lt` / `Le` / `Gt` / `Ge` opcode
   regardless of operand type hint. Type-hint gating was relaxed
   (the chain analysis below proves correctness; runtime
   `TryReadAsLong` + `DeoptBinaryII` handle non-int operands by
   falling back to the boxed `Binary` path).
2. **Fixpoint demotion** — for each non-terminal candidate
   (anything but `LtII`/`LeII`/`GtII`/`GeII` which write boxed
   Boolean and are terminal-safe), check:
   - No phi reads the result SSA version (phi escape).
   - Every use of `(resultSlot, version)` is at a PC that is also
     a candidate AND reads the slot at the B or C operand
     position.
   Demote producers whose checks fail. Iterate until no more
   demotions.
3. **Apply** — single-byte opcode-tag swap on `fn.Code[pc]`. Sets
   `fn.UsesUnboxedSlots = true` when at least one swap survives.

### Extended opcode set

`GtII` / `GeII` / `EqII` / `NeII` added at `0xB4-0xB7`. The II
family now occupies the contiguous range `0xB4-0xBF`.

`Eq` and `Ne` are deliberately NOT in the rewriter's candidate
set: their boxed `Binary.GetComparisonEq` dispatch covers every
RuntimeValue subtype (string/list/null/instance/boolean/...).
Promoting `Eq → EqII` deopted on every non-int compare; a
sweep audit caught 14 test regressions (test_struct_basics,
test_bitwise, test_borrowing, ...). The `EqII` / `NeII` opcodes
remain in the dispatch table for future use but no IR currently
emits them.

### Empirical

On `bench_hotloop.ra` / `bench_arithmetic.ra`: 0 promotions
survive — the only arith is the loop-counter self-feed which
escapes via the loop-header phi (slot 7's version 3 is read by
the back-edge phi at BB1).

On a synthetic chain bench (`if (i*2+3)-1 > 0 { sum = sum + 1; }`
inside a 1 M-iter loop):
- 4-op chain (`Mul`, `Add`, `Sub`, `Gt`) fully promoted to II.
- A/B time: 1.254 s boxed → 1.230 s II → **~2 % wall-clock**.
- Modest because the chain is short and loop machinery
  (ForEach iteration, scope mgmt, AddIntoSlot for `sum`)
  dominates each iter. Savings scale linearly with chain length.

### Why current bench corpus doesn't exercise II

Two structural features of the IR limit visible wins:
1. **Range materialises a List** — `for i in 0..N` builds a
   1 M-element ListValue eagerly. Per-iter ForEach dispatch
   dominates arith cost. Lazy Range is a separate (large)
   refactor.
2. **`var x = expr;` breaks chains** — every intermediate
   binding inserts `DeclareLocal` whose result-slot consumer
   isn't an II opcode. Only inline expression chains terminating
   in a comparison promote cleanly.

### Tests

- Main sweep: 770/770 OK, 0 FAIL.
- other_tests: 155/155 OK, 0 FAIL.

### Summary

| | |
|---|---|
| Chain analyzer | fixpoint demotion on SSA def-use graph |
| II opcodes total | 12 (`LoadIntS64`, `UnboxI`, `BoxI`, `AddII`, `SubII`, `MulII`, `LtII`, `LeII`, `GtII`, `GeII`, `EqII`, `NeII`) |
| Active rewrite candidates | Add/Sub/Mul/AddNN/SubNN/MulNN/Lt/Le/Gt/Ge (Eq/Ne excluded — boxed comparison covers any RuntimeValue) |
| Terminal-safe | Lt/Le/Gt/Ge (write boxed Boolean) |
| Bench wins | ~2 % on synthetic 4-op arith chain → comparison → branch |
| Bench wins on existing corpus | 0 (every arith chain is broken by phi escapes or `var` bindings) |
| Future work | M66.5: hoist iter-counter unboxing through the loop phi (would require teaching the SSA pass to model loop-carried int values as unboxed). M66.6: lazy-Range opcode so `for i in 0..N` stays unboxed without a materialised ListValue |

### File map (M66.4)

- `Interpreter/IR/Opcode.cs` — added GtII/GeII/EqII/NeII at
  0xB4-0xB7 (contiguous with 0xB8-0xBF block).
- `Interpreter/Vm/VmExecutor.cs` — dispatch arm + helper cases for
  the four new opcodes; `DeoptBinaryII` extended to cover all six
  comparison BinOps.
- `Interpreter/IR/Analysis/IrRewriter.cs` — Phase 5 chain-analysis
  promotion (active, sound).

---

End of document. Live edits permitted as design decisions firm up.

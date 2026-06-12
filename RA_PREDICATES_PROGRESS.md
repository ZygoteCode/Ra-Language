# Ra Predicates — Implementation Progress & Handoff

Status: **P0, P1, P2 shipped and validated. P3 (stdlib HOFs), P4 (narrowing), P5 (tests/bench/docs) remain.**
Branch: `claude/fervent-galileo-3d6911`. Zero regressions across the 324-file corpus (319 pass / 5 fail-by-design).

This document lets a fresh session finish the predicate system exactly to the original brief: predicates as a **first-class, composable, narrowing-capable boolean function** — better than C#/Java/Kotlin/Dart/Python — with zero new opcodes, zero new AST node kinds, zero new visitors, AOT-safe.

---

## 1. Semantic model (DECIDED — do not re-litigate)

A predicate is **a distinct first-class boolean function value** (`RuntimeValueType.Predicate`). Not "a function that returns bool" — it is a `PredicateValue : BaseFunctionValue`, so it IS-A function (passes anywhere `fn(T) -> bool` is wanted) but carries composition + narrowing semantics.

Three pillars:

1. **Declaration / literal** (`pred` keyword)
   ```ra
   pred even(n: int) => n % 2 == 0          // named, arrow body
   pred adult(p: Person) { ret p.age >= 18 } // named, block body
   let big = pred(n) => n > 100              // anonymous literal
   ```
   Return type is `bool` by definition (defaulted; explicit non-bool ⇒ RA0209).

2. **Composition algebra** — closed, short-circuit, RHS auto-lifts a plain `fn->bool`:
   ```ra
   pred valid = adult & verified & !banned   // & | ! operators
   let m = even.xor(positive)                 // methods: .negate() .xor() .implies() .iff() .test()
   ```
   `(p & q)(x)` runs `p(x)`, only evaluates `q(x)` if `p(x)` is true.

3. **Type narrowing (the crown jewel — P4, NOT YET WIRED)**
   ```ra
   pred is_text(x: any) => x is string
   if is_text(v) { /* v : string here */ }   // user-defined type guard
   ```

### Locked decisions
- **`&` / `|` / `!` are THE and/or/not operators.** `and`/`or`/`not` are Ra keywords and stay boolean control-flow (the IR lowers them to truthiness short-circuit `OP_AND_JZ`/`OP_OR_JNZ`). Do NOT overload `AndedBy`/`OredBy` on `PredicateValue` — that would silently diverge AST vs IR. (We tried; removed it.)
- **`^` is exponent (pow), not XOR.** XOR is the method `.xor(q)` / combinator `all_of`/`any_of`, never an operator.
- **Methods cannot be `and`/`or`/`not`** — the member-access parser requires `IDENTIFIER` after `.`, and those are keywords. So the method surface is `.negate()`, `.xor(q)`, `.implies(q)`, `.iff(q)`, `.test(x)`. Operators cover and/or/not.
- **`pred` is a reserved keyword.** Migration cost was 2 corpus files that used `pred` as an identifier (already fixed). Document this in the final design's "Migration" section.
- **`pred<T>` / `Pred<T>` is sugar for `fn(T)->bool`** (`Pred<A,B>` → `fn(A,B)->bool`, bare `Pred` → `fn(any)->bool`). Implemented in the type parser.
- **Anonymous literal is `pred(params) => expr`** (user-chosen). Composition surface is **both** operators and methods (user-chosen). Scope is **full first-class including narrowing** (user-chosen).

---

## 2. Done — P0, P1, P2

### P0 — keyword, value, lowering, calls ✅
- `Keyword.Pred` + lexer map (`pred`).
- `RuntimeValueType.Predicate`.
- `FunctionDefinitionNode.IsPredicate` + narrowing fields `NarrowsParamName` / `NarrowsToType` / `NarrowsNegated` (fields present; **populated only in P4**).
- `ParseFunctionDefinition(..., bool isPredicate)`: reuses the entire fn machinery (params, generics, captures, destructuring, arrow/block bodies); skips the `fn` check; defaults/validates `bool` return.
- Parser hooks: atom-position `case Keyword.Pred` (literals + named in expr position), `pub pred` declaration path, `ParsePredicateDefinition` wrapper (`Parser/Parser.Predicates.cs`).
- `PredicateValue : BaseFunctionValue` (leaf + And/Or/Not/Xor composites), short-circuit `Execute`, `& | !` operators with auto-lift + double-negation fold (`!!p → p`).
- `FunctionDefinitionHelper.Apply` wraps a predicate node's `FunctionValue` in `PredicateValue.Leaf` (registration + return).
- **VM fix (important):** `Opcode.NotB` (the typed bool-not the IR rewrites `!` into) coerced via `IsTrue()` and bypassed `Notted()`. Made it dispatch `Notted()` for a boxed `PredicateValue` operand BEFORE the truthiness coercion — aligning IR with the AST `UnaryOperationNodeVisitor`. See `VmExecutor.cs` `ExecuteUnboxedBB` / `case Opcode.NotB`.

### P1 — composition polish + methods ✅
- Auto-lift of a plain `fn->bool` on the RHS of `&`/`|` (only the left side must be a predicate to anchor a chain).
- Method surface via member access: `BoundPredicateMethodValue` + a `RuntimeValueType.Predicate` case in `MemberAccessHelper.Apply`. `ApplyWithIc` delegates predicates to `Apply` (no IC priming needed); `MemberAccessNodeVisitor` (AST path) delegates too.
- Public `PredicateValue` methods: `And`/`Or`/`Not`/`Xor`/`Implies`/`Iff`.

### P2 — `Pred<T>` type + diagnostics ✅
- `pred<T>` / `Pred<T>` type sugar in `Parser.Types.cs` → `TypeDescriptor.FunctionType(args, bool)`. A predicate (leaf or composite) and a plain lambda both satisfy a `pred<int>` slot; a predicate satisfies `fn(int)->bool`.
- Diagnostics: `RA0209` predicate-return-type (non-bool body), `RA0416` predicate-composition (combine with non-callable). Predicate-method-not-found rides `RA0402` with a help line.

### Validation
- Full corpus sweep with the **fresh** interpreter: 319 pass / 5 fail. The 5 are by-design (circular-import fixtures `circle_a/b`, `.rac` treeshake fixture `big_entry`, and two negative tests `test_equality`/`test_string_operators` whose output legitimately contains a captured `error[...]`).
- Verified by smoke scripts (now deleted): direct calls, anonymous literals, `& | !` + short-circuit, `!!` fold, block bodies, `.negate/.xor/.implies/.iff/.test`, method+operator chaining, `pred<int>` assignment from literal/composite/lambda, predicate → `fn(int)->bool`, and both new diagnostics.

---

## 3. Files touched so far

New:
- `Parser/Parser.Predicates.cs`
- `Interpreter/Values/Functions/Predicates/PredicateValue.cs`
- `Interpreter/Values/Functions/Predicates/BoundPredicateMethodValue.cs`

Modified:
- `Lexer/Tokens/Keyword.cs`, `Lexer/Lexer.cs`
- `Interpreter/Values/RuntimeValueType.cs`
- `Parser/Nodes/Functions/FunctionDefinitionNode.cs`
- `Parser/Parser.Declarations.cs`, `Parser/Parser.Expressions.cs`, `Parser/Parser.Types.cs`
- `Interpreter/Runtime/FunctionDefinitionHelper.cs`
- `Interpreter/Vm/VmExecutor.cs` (NotB)
- `Interpreter/Runtime/MemberAccessHelper.cs`, `Interpreter/Visitors/Structs/MemberAccessNodeVisitor.cs`
- `Errors/DiagnosticCode.cs`
- `bin/x64/Release/net10.0/tests/functions/test_lambdas_full.ra` (`pred`→`posf`)
- `bin/x64/Release/net10.0/tests/integration/test_data_pipeline.ra` (`pred`→`keep`)

---

## 4. Remaining work

### P3 — stdlib higher-order predicate builtins (READY TO CODE)

The recon found a real GAP: stream HOFs exist (`stream_filter`/`stream_any`/…) but **list HOFs do not**. Fill it.

**Where:** `Interpreter/Values/Functions/Builtins/CollectionBuiltins.cs` (group `"collections"` → `std.prelude.collections`). Combinators go in a new `PredicateBuiltins.cs` registered under `"func"` (→ `std.prelude.func`) via `RegisterGrouped("func", PredicateBuiltins.Register)` in `BuiltInRegistry.EnsureInitialized`.

**Wiring is ONE `Register()` call — verified.** `Program.cs:133–138` auto-surfaces every `BuiltInRegistry.AllNames` into `BuiltinSymbolTable` as a `BuiltInFunctionValue`, and the std taxonomy (`Program.cs:90–94`) already includes them. So registering under an existing group is fully sufficient: the name becomes callable after `import std.prelude.*` (or `std.prelude.collections` / `std.prelude.func`), routed `BuiltInFunctionValue.Execute → BuiltInRegistry.Invoke`. Do NOT add anything to `Program._builtInFunctions` — that array is only the always-available, switch-dispatched directs (`print`, etc.). The existing `list_take`/`list_sort`/… are registered exactly this way and are used in the corpus, so the path is proven.

**Names are all free** (checked): `filter reject find find_index any all none count partition take_while drop_while` and combinators `all_of any_of none_of negate always_true always_false`. Use **bare** names (ergonomic; the user wants memorable predicates). Note `count` here counts matches-of-a-predicate, distinct from existing `list_count` (counts a value).

**Handler signature** (sync): `private static RuntimeResult Name(Context ctx, List<RuntimeValue> args, Position p1, Position p2)`.

**Calling a predicate synchronously** (the key pattern, copied from `DelegateBuiltins.Invoke`):
```csharp
var r = RaLanguage.Interpreter.Runtime.Async.SyncAwait.Get(pred.Execute(new List<RuntimeValue> { elem }));
if (r.Error != null) return new RuntimeResult().Failure(r.Error);
var v = r.Value ?? r.FuncReturnValue;
bool match = v != null && v.IsTrue();
```

**Helpers available in the file:** `ExpectArgs(name, args, n, ctx, p1, p2, out err)`, `Ok(value, ctx, p1, p2)`, `Fail(ctx, p1, p2, msg)`. Constructors: `new ListValue(List<RuntimeValue>)`, `new IntegerValue(int)`, `BooleanValue.Of(bool)`, `new TupleValue(List<RuntimeValue>)`, `NullValue.Null`. `BaseFunctionValue` is visible without a using (the file's namespace is nested under `...Values.Functions`). Cast the list arg with `args[0] is ListValue lv` and the predicate with `args[1] is BaseFunctionValue f` (accept ANY callable, not just `PredicateValue` — a plain `fn->bool` must work too).

**Suggested HOF set** (all take `(list, predicate)` unless noted):
- `filter` → new list of matching elements; `reject` → inverse.
- `find` → first matching element or `NullValue.Null`; `find_index` → `int` index or `-1`.
- `any` → `bool` (∃, short-circuit true); `all` → `bool` (∀, short-circuit false); `none` → `bool` (¬∃).
- `count` → `int` number matching.
- `partition` → `TupleValue([matching_list, nonmatching_list])`.
- `take_while` / `drop_while` → prefix / suffix by predicate.

**Combinator set** (in `PredicateBuiltins.cs`, produce `PredicateValue`s — reuse `PredicateValue.Lift`, `.And`, `.Or`, `.Not`):
- `all_of(p1, p2, …)` → AND of all (varargs; fold left with `.And` after lifting). Empty ⇒ `always_true`.
- `any_of(p1, p2, …)` → OR of all. Empty ⇒ `always_false`.
- `none_of(p1, …)` → `negate(any_of(...))`.
- `negate(p)` → `Lift(p).Not()`.
- `always_true` / `always_false` → register as builtin predicate functions `(x) -> true` / `(x) -> false` (they're callables, so they lift into composition: `p & always_true`). Optional nicety: teach `PredicateValue` the folds `p & always_true → p`, `p | always_false → p` (skipped so far; mark as future work if not done).

**After adding:** every test/bench/fixture `.ra` that uses these must `import std.prelude.*;` (already the convention). Run `--selftest-stdlib` to confirm the taxonomy still covers the live built-in set exactly (no orphan builtins). The group tag auto-derives — no hand-edit to `StdLibrary.cs` needed as long as you reuse existing groups (`collections`, `func`).

### P4 — type-guard predicates in the narrowing analyzer (READ THE REALITY CHECK FIRST)

**Reality check — VERIFIED in the source, do not skip.** Ra's `NarrowingAnalyzer` (`Interpreter/Runtime/Narrowing/NarrowingAnalyzer.cs`) is a **compile-time, diagnostics-only** pass. For `is` tests it flags *impossible* (`x:int; x is string` ⇒ always false) and *trivially-true* tests, and it checks `match` union-exhaustiveness. It does **NOT** flow-type `if x is T { … }` to treat `x` as `T` inside the branch: the `IfNode` handler (line ~323) walks the condition, then does `PushScope()` / walk body / `PopScope()` with a **fresh empty** scope — no narrowed binding is ever injected, and nothing consumes one. Ra is dynamically typed; member access inside the branch already works at runtime regardless of declared type. So **"narrowing" in Ra means static guard-awareness / diagnostics**, which is the coherent-with-Ra reading of the brief's "type guards … se semanticamente possibile." Do NOT build TypeScript-style branch flow-typing for predicates — it doesn't exist for inline `is` either, so it would be a whole-language feature (see the stretch note below), out of predicate scope.

**So P4 = give user-defined predicate guards the SAME diagnostic treatment as inline `is`.** Entirely inside `NarrowingAnalyzer`, metadata read from the **AST** (the runtime `PredicateValue` does not exist during this pass — line 52 says the analyzer works "without consulting the runtime symbol table").

Plan:
1. **`CollectPredicates(root, state)` pre-pass**, mirroring `CollectEnums` (line ~93), run alongside it in `Analyze` (line ~53). For every `FunctionDefinitionNode` with `IsPredicate == true`: unwrap the body (arrow → the expression; block → a lone `ret <expr>` / trivial `{ <expr> }`); if it is an `IsTypeNode` (`Parser/Nodes/Operations/IsTypeNode.cs`: `Expression`, `TestedType`, `Negated`) whose `Expression` is a `VariableAccessNode` naming the predicate's sole parameter, record `state.PredicateGuards[name] = (TestedType, Negated)`. (Add the dict to `State`.) The P0 node fields `NarrowsParamName/NarrowsToType/NarrowsNegated` are optional convenience — populate them here too if you want them on the runtime value for reflection; nothing reads them yet.
2. **Recognise a guard call in `Walk`.** When a condition expression is a call `p(v)` — the callee a `VariableAccessNode` named `p ∈ state.PredicateGuards`, the single argument a `VariableAccessNode` `v` (handle a `!p(v)` / `is not` by flipping `Negated`) — synthesize the equivalent of `CheckIsTest` for `v is TestedType` and emit the impossible / always-true diagnostics. `CheckIsTest` (line ~424) is the exact template; it currently early-returns when the `is` LHS is a function call (line ~428–430) — you are filling that gap, but via the *call* form. Reuse `TypeSystem.TypesOverlap` (used at line ~453).
3. **Find the call node shape:** predicate/function calls are `FunctionCallNode` (confirm the class name under `Parser/Nodes/`); the callee is a `VariableAccessNode`. Conditions live on `IfNode.Cases[i].Condition`, `WhileNode.ConditionNode`, `TernaryNode.Condition`.

**Tests (diagnostics):** `pred is_text(x: any) => x is string`; `let s: string = "a"; if is_text(s) {}` → no diagnostic; `let n: int = 1; if is_text(n) {}` → "always false"; `if !is_text(n) {}` → "always true"; a non-guard predicate (body not `param is T`) registers nothing and emits nothing. Runtime behaviour of the predicate is already correct (`x is T` returns bool) — P4 adds only the static guard-awareness.

**Stretch (defer + document, do NOT do under predicates):** true flow-typing — inject narrowed bindings into branch scopes that a type checker consumes — is a language-wide feature absent for inline `is` today. Building it only for predicate guards would be inconsistent. Note it as future work in the design doc, not P4.

### P5 — tests, bench, docs, memory

- `tests_predicates.ra` — hard-assert suite (`check`/`check_eq` that `print "OK ..."` on success and `throw` on failure; import header `import std.prelude.*;`). Cover: direct call, anonymous literal, `& | !` + short-circuit (use a side-effect counter to prove short-circuit), `!!` fold, block body, all methods, `pred<T>` assignment + `fn(T)->bool` interop, every stdlib HOF, combinators, predicate type-guard diagnostics (impossible / always-true), and negative cases (non-bool body rejected, compose-with-non-callable rejected, unknown method rejected). **Comments must be on their own line** (see gotcha #2).
- `bench_predicates.ra` — microbench: predicate call vs raw fn call; composed `(a & b & c)(x)` vs hand-written `a(x) && b(x) && c(x)`; `filter` over a large list. Structure like `bench_lambdas.ra` (loop with `sum` accumulator, `print("done")`).
- `RA_PREDICATES_DESIGN.md` — match the house style (see `RA_LAMBDAS_DESIGN.md` / `RA_CONSTRUCTORS_DESIGN.md`): Status line; Motivation; Syntax+grammar (EBNF); Semantics table (Lexer→Parser→Resolver→IrCompiler→VM→Runtime, noting "zero new opcodes/nodes/visitors"); Runtime+Performance (AOT, short-circuit, fold); Diagnostics (RA0209/RA0416 + method help); the **competitive comparison table** vs C#/Java/Kotlin/Dart/Python/C++ that the brief explicitly asked for; Test matrix; Out-of-scope (composite narrowing, arg-type-compat check on composition, always/never folds); Migration (`pred` reserved word, 2 files renamed).
- CLAUDE.md — add a `### Predicates` subsection (1–2 sentences + links to the design doc and `tests_predicates.ra`/`bench_predicates.ra`), mirroring the Lambdas/Constructors entries.
- Memory — add a `project_predicates.md` file + a one-line pointer in `MEMORY.md`.

---

## 5. Gotchas / non-obvious facts (READ THESE — they cost real time)

1. **Build output path vs test corpus path.** `dotnet build -c Release` writes the FRESH exe to `bin/Release/net10.0/RaLanguage.exe`. The committed test corpus + `std/` live beside the **STALE** `bin/x64/Release/net10.0/RaLanguage.exe` (last published binary; does NOT have your changes). Running the stale exe makes regression sweeps meaningless. Always test with the fresh exe. (`dotnet build -c Debug` → `bin/Debug/net10.0/`.)
2. **Comments eat the trailing newline.** `SkipComment` (both `#` and `//` line comments) consumes through `\n`, so a **trailing** comment merges its line with the next statement (→ RA0207). Put comments on their **own line** in every `.ra` file.
3. **`!` lowers to `NotB`, not `Not`.** The IR rewrites unary `!` to the typed bool opcode `NotB`, which used `IsTrue()` not `Notted()`. The fix dispatches `Notted()` for a boxed `PredicateValue`. If you add more overloaded-NOT types, generalize the same spot.
4. **Two member-access resolvers.** `MemberAccessHelper.ApplyWithIc` (inline-cache, used by `OP_GET_MEMBER` when an IC slot exists) and `MemberAccessHelper.Apply`. Predicates delegate from `ApplyWithIc` to `Apply`. There is also the AST `MemberAccessNodeVisitor`. `obj.method(args)` compiles to GET_MEMBER + CALL (no fused `CallMethod` in the VM).
5. **`pred` is reserved.** Any existing/new `.ra` using `pred` as an identifier breaks. Only 2 corpus files needed it; already migrated.
6. **Operators ride existing dispatch.** `&`/`|` → VM `BAnd`/`BOr` generic path → `left.BitwiseAndedBy/BitwiseOredBy(right)` → `PredicateValue` overrides. One override covers both AST-walk and IR. No new opcodes.

---

## 6. Build & test recipe

```bash
# from worktree root
dotnet build -c Release          # fresh exe -> bin/Release/net10.0/RaLanguage.exe
EXE="$(pwd)/bin/Release/net10.0/RaLanguage.exe"

# run a smoke (virtual std.prelude.* needs no std/ dir, runs from anywhere)
"$EXE" tests_predicates.ra

# full regression sweep — MUST cd to the corpus dir so std/ + relative paths resolve
cd bin/x64/Release/net10.0
for f in $(find tests -name "*.ra" | sort); do
  out=$("$EXE" "$f" 2>&1); code=$?
  if [ $code -ne 0 ] || echo "$out" | grep -q "error\["; then echo "FAIL $f ($code)"; fi
done
# expected baseline failures (by design): big_entry.ra, circle_a.ra, circle_b.ra,
# test_equality.ra, test_string_operators.ra
```

`--selftest-stdlib` after P3 to audit the builtin taxonomy.

---

## 7. Acceptance criteria (from the brief) — tracking

| Requirement | State |
|---|---|
| First-class native feature, not a workaround | ✅ `PredicateValue`, `pred` keyword, `pred<T>` type |
| Clear, rigorous, documented semantics | ✅ model decided; design doc pending (P5) |
| Solid in IR + VM | ✅ zero new opcodes; rides BAnd/BOr/NotB + GET_MEMBER |
| Excellent ergonomics | ✅ operators + methods + literals; HOFs pending (P3) |
| Robust with generics/capture/short-circuit/composition | ✅ (reuses fn machinery; short-circuit in composite Execute) |
| Composition `not/and/or/xor` | ✅ `! & |` + `.xor/.implies/.iff` |
| Null/optional safety, no toxic overloads | ✅ predicates strictly return bool; `and`/`or` left boolean |
| Type guards / narrowing (Ra narrowing = static `is`-diagnostics, not runtime flow-typing) | ⏳ **P4** — guard-aware diagnostics; true flow-typing is N/A in Ra (future, language-wide) |
| Compatible with generics / arity variants | ✅ `pred<A,B>`; generic predicates via fn machinery |
| Reduce allocations / no boxing / AOT-friendly | ✅ thin `BaseFunctionValue` subclass, no reflection |
| IR fold/peephole where sensible | ◑ `!!p→p` done; `p & always_true→p` pending (P3, optional) |
| Compiler-grade diagnostics + suggestions | ◑ RA0209/RA0416 + method help; expand in P3/P4 |
| Tests (core + edges) | ⏳ **P5** |
| Better/cleaner than other languages | ⏳ prove in the design-doc comparison table (P5) |

Legend: ✅ done · ◑ partial · ⏳ remaining.

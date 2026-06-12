---
name: project_predicates
description: Predicates feature for Ra Language — first-class `pred`; P0-P2 shipped, P3-P5 remain; handoff doc + PR
metadata:
  type: project
---

First-class **Predicates** for Ra Language (started 2026-06-12). A predicate is a `PredicateValue : BaseFunctionValue` (`RuntimeValueType.Predicate`) — composable boolean function, zero new opcodes/AST-kinds/visitors. Built per a detailed brief (must beat C#/Java/Kotlin/Dart/Python; composition, narrowing, stdlib HOFs, AOT-safe, great diagnostics).

**COMPLETE — P0–P5 all shipped & validated; zero corpus regressions (325 files / 5 fail-by-design).** `pred` keyword + `pred(x) => e` literals; `& | !` composition (auto-lift RHS, `!!p→p` + side-effect-preserving constant folds); methods `.negate/.xor/.implies/.iff/.test`; `pred<T>` = `fn(T)->bool`; stdlib HOFs `filter/reject/find/find_index/any/all/none/count/partition/take_while/drop_while` + combinators `pred_all/pred_any/pred_none/negate/always_true/always_false`; type-guard diagnostics (`pred p(x)=>x is T` → analyzer flags impossible/redundant `p(v)`); diagnostics RA0209/RA0416; VM NotB dispatches `Notted()` for predicates.

**Key late decision:** combinators are `pred_*`, NOT `all_of/any_of/none_of` — those are reserved built-in **validator annotation** names (always-in-scope), so a bare `all_of(...)` resolves to the non-callable annotation type → `RA0401`. Constant folds preserve short-circuit side effects (`p & always_false` / `p | always_true` NOT folded).

**Canonical reference: [`RA_PREDICATES_DESIGN.md`](RA_PREDICATES_DESIGN.md)** (semantics, grammar, comparison table, test matrix). Historical handoff/decision log in [`RA_PREDICATES_PROGRESS.md`](RA_PREDICATES_PROGRESS.md). Tests `tests/functions/test_predicates.ra` (60 hard asserts); bench `bench/bench_predicates.ra`. Original PR: https://github.com/ZygoteCode/Ra-Language/pull/52.

Critical gotchas: `dotnet build -c Release` → `bin/Release/net10.0/` (fresh exe), but the test corpus + `std/` sit beside the STALE `bin/x64/Release/net10.0/` — run the fresh exe with CWD there. `.ra` comments eat the trailing newline, so comments must be on their own line. Related: [[project_ra_language]], [[project_lambdas]].
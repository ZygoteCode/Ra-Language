---
name: project_predicates
description: Predicates feature for Ra Language — first-class `pred`; P0-P2 shipped, P3-P5 remain; handoff doc + PR
metadata:
  type: project
---

First-class **Predicates** for Ra Language (started 2026-06-12). A predicate is a `PredicateValue : BaseFunctionValue` (`RuntimeValueType.Predicate`) — composable boolean function, zero new opcodes/AST-kinds/visitors. Built per a detailed brief (must beat C#/Java/Kotlin/Dart/Python; composition, narrowing, stdlib HOFs, AOT-safe, great diagnostics).

**Done & validated (P0-P2), zero corpus regressions (319 pass / 5 fail-by-design):** `pred` keyword + `pred(x) => e` literals; `& | !` composition (auto-lift RHS, `!!p→p` fold); methods `.negate/.xor/.implies/.iff/.test`; `pred<T>` = `fn(T)->bool`; diagnostics RA0209/RA0416; VM NotB dispatches `Notted()` for predicates.

**Remaining: P3** stdlib list HOFs (`filter/find/any/all/none/count/partition/take_while/drop_while` + combinators) — fills a real gap; one `BuiltInRegistry.Register()` per name suffices. **P4** predicate type-guards = guard-aware `is`-diagnostics ONLY (Ra's `NarrowingAnalyzer` is diagnostics-only — no runtime flow-typing; don't build TS-style narrowing). **P5** tests/bench/`RA_PREDICATES_DESIGN.md`/CLAUDE.md.

**To resume in a new chat: read [`RA_PREDICATES_PROGRESS.md`](RA_PREDICATES_PROGRESS.md) at the repo root** — full design, locked decisions, ready-to-code notes, gotchas, build/test recipe. PR: https://github.com/ZygoteCode/Ra-Language/pull/52 (branch `claude/fervent-galileo-3d6911`).

Critical gotchas: `dotnet build -c Release` → `bin/Release/net10.0/` (fresh exe), but the test corpus + `std/` sit beside the STALE `bin/x64/Release/net10.0/` — run the fresh exe with CWD there. `.ra` comments eat the trailing newline, so comments must be on their own line. Related: [[project_ra_language]], [[project_lambdas]].
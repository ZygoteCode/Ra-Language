# Known bugs / inconsistencies surfaced by the test campaign

Each entry is a real defect found while hardening the suite. "Verified" = I
reproduced it directly; "Reported" = surfaced by a focused test-writing pass
with a high-confidence repro. Tests that currently *pin the buggy behavior*
(so they stay green) are noted — flip them to assert the correct value once
the bug is fixed.

The interpreter previously exited 0 even on fatal errors, which masked most of
these. That is now fixed (uncaught error / compile abort → exit 1), which is
how several of these became visible at all.

## Correctness — evaluation / operators

1. **`list != list` is always `false`** (Verified).
   `[1,2,3] != [1,2,4]` → `false` (should be `true`). `==` on lists is
   correct; tuples/sets/maps handle `!=` correctly. Workaround in tests:
   `not (a == b)`. Likely the list `Ne` operator forgets to negate `Eq`.

2. **`null == 0` and `null != 0` are BOTH `true`** (Verified).
   A value cannot be simultaneously equal and not-equal. Loose `==`/`!=`
   are not consistent negations for `null` vs a number/bool.

3. **`??`, `??=`, `&&=`, `||=` do not short-circuit** (Verified for `??`).
   The RHS is always evaluated even when the LHS makes it unnecessary
   (`5 ?? rhs()` still calls `rhs()`). Plain `&&`/`||` short-circuit
   correctly; the coalesce/compound forms do not.

4. **`int128`-annotated literal loses precision** (Verified).
   `var i: int128 = 9223372036854775807` binds `9223372036854775808`
   (off by one) once the JIT is warm — the literal routes through `double`.
   The plain (unannotated) literal stays exact; int128/uint128 *max* literals
   are unaffected.

## Correctness — codegen / VM (IR optimizer)

5. **[FIXED]** **`while`-loop string accumulator miscompiled to `0`** (Verified).
   `var out = ""; while … { out = out + s; … }` returned `0` (type `number`).
   Also `out = out + (i as string)` → `"000"` (stale boxed iter mirror). Root
   cause: M87 typed-accumulator promotion in `IrCompiler.cs` assumed the
   accumulator was `Int64` (`UnboxI("")→0`) without checking its type, and the
   typed iter's boxed mirror was not published before non-redirectable reads.
   **Fix:** (a) a `NumericInitBindings` pre-pass gates promotion to bindings
   whose initializer is provably numeric — string accumulators are never
   promoted; (b) the iter/accumulators are marked dirty at loop-body top so the
   boxed mirror publishes before each read. Benchmarks unaffected (all use
   numeric-literal init). Live guard: `control_flow/test_while_string_accumulator.ra`.

5b. **[FIXED]** **Native-int result in specialised arithmetic crashed the VM**
   (Verified). A native `@dll_import` function returning `int` produces an
   `IntegerValue` (`Type == Integer`), not a `NumberValue`. When such a value
   flowed into the VM's specialised `AddNN`/`SubNN`/`MulNN` opcodes (emitted
   when the static `SlotTypeHints` lattice optimistically proved both operands
   "Number"), the VM hard-cast `(NumberValue)operand` and threw an uncatchable
   `InvalidCastException`, aborting the process. This was originally mis-filed
   as a "non-deterministic FFI marshalling crash" — it is deterministic given
   the lattice-triggering call pattern (a 2-arg native call priming a later
   1-arg-call loop). **Fix:** `VmExecutor` guards the int64 fast path with
   `is NumberValue` and falls back to the virtual `AddedTo`/`SubbedBy`/
   `MultedBy` (defined on every `RuntimeValue`) for other numeric classes.
   Also: `Program.ExecuteMainFile` now surfaces escaping managed exceptions on
   stderr instead of swallowing them in path mode. Live guard:
   `native/test_native_int_arithmetic.ra` + `native/test_str_marshal.ra` M15.

6. **Generic free-function call inside a cast crashes the VM** (Reported).
   `(f<int>(x) as string)` → `VM: NativeDefine unsupported NodeType Cast`.
   Calling first / comparing with `==` works; generic *method* calls cast fine.

7. **`+=` on a static field is a no-op-ish bug** (Reported).
   `C.n += 1` sticks at 1 regardless of call count; `C.n = C.n + 1` works.

## Correctness — control flow / errors

8. **`finally` swallows an in-flight throw when escaping a `catch`-less inner
   `try`** (Reported). `try { try { throw x } finally { … } } catch (e) { … }`
   runs the inner `finally` but the outer `catch` never fires — the exception
   is silently dropped.

## Robustness — uncatchable failures (should be catchable or not crash)

9. **`inf()` operands hard-crash the process, uncatchably** (Reported).
   Any arithmetic/comparison with an `inf()` operand (even `1.0/inf()` or
   `inf() > 1.0`) exits the process; `try/catch` does not catch it. Passing
   `inf()`/`nan()` to built-ins is safe. Finite overflow-to-infinity is a
   *catchable* "Double overflow" — inconsistent.

10. **Out-of-range `as`-cast narrowing crashes uncatchably** (Reported).
    `300 as byte`, `(0-1) as byte` exit the process (host `byte.Parse`/overflow
    escapes `try/catch`). `to_byte(...)` wraps safely instead.

11. **Non-exhaustive `match` is uncatchable** (Reported).
    A `match` with no covering arm and no `_` raises an error `try/catch` does
    not catch (prints a Traceback; the catch body is skipped).

## Lexer

12. **Single-quoted strings mis-lexed as lifetimes** (Verified).
    `'a b'`, `'a.b'`, `'say "hi"'` fail with "unterminated string literal":
    a single-quote body of `<letter-run><non-quote-non-backslash>` is taken
    for a lifetime token. Use double quotes. Root: the `case '\''`
    disambiguation in `Lexer.cs`.

13. **Trailing line comment eats the newline terminator** (Reported).
    A bare statement (no `;`) followed by a `#` / `//` / `---` comment merges
    with the next line → RA0207 "nowhere to attach". Block comments `/* */`
    don't. Terminate bare statements with `;`.

14. **Only 7 escapes exist; others silently drop the backslash** (Reported).
    `\n \t \r \\ \" \' \`` work; `A`, `\x41`, `\0`, `\b`, … keep the bare
    char (`"A"` → `u0041`). No unicode/hex/control escapes.

## API / naming inconsistencies

15. **Bare `min` / `max` are reachable but throw on call** (Verified).
    `exists("min")` is `true`, but `min(3,1)` throws RA0401. Use
    `math_min` / `math_max`. Register `min`/`max` as aliases or remove them.

16. **`fs_write_text` prepends a UTF-8 BOM** (Verified).
    `fs_write_text(p, "Hi")` writes `EF BB BF 48 69`; `fs_size` = content + 3.
    `fs_read_text` strips it; `fs_write_bytes` does not add it. Usually
    undesirable as a default.

17. **Operator-overload body requires the parameter be named `other`**
    (Verified). `operator +(rhs: V) { … }` throws "`rhs` is not defined";
    renaming the parameter to `other` works. Dispatch binds a hardcoded name.

## Surprises worth documenting (by-design, but easy to trip on)

- `{}` is an empty **set**, not a map — use `make_map()`. (`tests/collections/test_map_basics.ra` historically leaned on this by accident.)
- One unified `number` type: `is_int(5.0)` and `is_float(5)` are both `true`; `type_of(anyNumber) == "number"`.
- `/` is always true division (`7/2 == 3.5`); typed-width annotations (`var b: byte = 300`) are advisory — values never wrap/clamp at the language level.
- `parse_*` return `null` on bad input (no throw); the `as` cast throws.
- Interface conformance is **structural** — `class X : Iface` errors; `:` is base-class only.
- `for k in <map>` iterates `(key, value)` tuples, not keys.
- Labeled `break`/`continue` don't exist; only function-scoped, backward-only `goto LABEL`.
- `round` uses banker's rounding (round-half-to-even).
- Stale "break hangs" comments remain in some `control_flow` tests — `break`/`continue` work correctly in this build; those notes can be cleaned up.

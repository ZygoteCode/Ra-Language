# Ra — bitwise shift / rotate operators

## §1. The arrow ladder

Ra exposes a six-operator family for bit-level shifting and rotation. The
operator's length encodes a stable, monotonic property: **the more arrows, the
more bits are preserved**.

| Operator | Name                          | Semantics                                    |
|----------|-------------------------------|----------------------------------------------|
| `<<`     | arithmetic left shift         | Same as C / C# / Java — zero-fill low bits.  |
| `>>`     | arithmetic right shift        | Sign-extending for signed types, zero-fill for unsigned. |
| `<<<`    | logical / unsigned left shift | Identical bit pattern to `<<`; the distinct token signals "I think of this value as unsigned." |
| `>>>`    | logical / unsigned right shift| Zero-fills the vacated high bits regardless of operand sign. Matches Java / JavaScript `>>>`. |
| `<<<<`   | rotate-left                   | Bits that fall off the high end re-enter on the low end. Fixed-width types only. |
| `>>>>`   | rotate-right                  | Symmetric counterpart to rotate-left.        |

Every operator has a compound-assignment form: `<<=`, `>>=`, `<<<=`, `>>>=`,
`<<<<=`, `>>>>=`.

All six operators share a single precedence band, sitting **above** additive
(`+`, `-`) and **below** comparison (`<`, `>`, …). They associate **left-to-
right**.

## §2. Type interaction

Ra has two integer worlds: arbitrary-precision `number` and fixed-width
families (`int` = i32, `long` = i64, `short` = i16, `int128`, plus the unsigned
counterparts). Shift / rotate semantics differ between them by design.

### Arbitrary-precision `number`

* `<<`, `<<<` — multiply by `2^n`. The result grows without bound (`1 << 200` is
  a 60-digit `number`).
* `>>`  — integer divide by `2^n`. Arithmetic on a sign-bearing
  arbitrary-precision representation: the result rounds toward minus infinity.
* `>>>` — logical right shift. Only defined for **non-negative** operands —
  a negative arbitrary-precision number has no canonical unsigned bit pattern,
  so the operator errors with a precise diagnostic and a hint to cast the
  operand to a fixed-width type first.
* `<<<<`, `>>>>` — rotates. Always error on `number`: there is no canonical
  width to rotate within. Cast to a fixed-width integer.

### Fixed-width integers (`int`, `long`, `short`, `int128`, …)

* Every operator rotates / shifts within the type's bit width.
* The shift count is **masked modulo the width** (`int` masks `& 31`, `long`
  masks `& 63`, `short` masks `& 15`, `int128` masks `& 127`). This matches
  the host CPU (x86 `shl` / `shr` mask `CL` automatically) and C# / Java
  semantics for `int << int` / `long << int`.
* A **negative shift count** is always an error (no implicit operator flip).
* The shift count itself can be any numeric type — `int << long`, `int << number`,
  `long << short`, etc. all parse and dispatch through the centralised
  `ShiftCount.TryGet` helper in [Interpreter/Values/Primitives/ShiftCount.cs](Interpreter/Values/Primitives/ShiftCount.cs).
* Rotations on fixed-width types use the type's bit width as the rotation
  modulus — `1i <<<< 32` is a full revolution and returns `1i`.

## §3. Compiler architecture

The operators traverse the full Ra compiler / VM pipeline.

### Lexer
[Lexer/Lexer.cs](Lexer/Lexer.cs)'s `ProcessLessThan` / `ProcessGreaterThan`
implement strict longest-match scanning over the `<` / `>` family:
`<` → `<<` → `<<<` → `<<<<`, each with an optional trailing `=`. The token
types live in [Lexer/Tokens/TokenType.cs](Lexer/Tokens/TokenType.cs):

```
BITWISE_LEFT_SHIFT, BITWISE_RIGHT_SHIFT,
BITWISE_LOGICAL_LEFT_SHIFT,  BITWISE_LOGICAL_RIGHT_SHIFT,
BITWISE_ROTATE_LEFT,         BITWISE_ROTATE_RIGHT,
+ EQ variants for each.
```

### Parser
[Parser/Parser.cs](Parser/Parser.cs):
* `s_opsShift` includes all six binary forms, sitting one band above range
  and below null-coalescing — the same precedence as the pre-existing `<<` /
  `>>`.
* `AssignmentTokens` enumerates the six compound-assign variants.
* `IsOperatorToken` / `IsTryUnwrapNext` accept the new tokens.

### AST
No new node types. The existing [BinaryOperationNode](Parser/Nodes/Operations/BinaryOperationNode.cs)
carries the operator token unchanged; downstream dispatch reads
`OpTok.Type` to discriminate.

### IR & VM
[Interpreter/IR/Opcode.cs](Interpreter/IR/Opcode.cs) introduces three boxed
opcodes plus three typed-Int64 counterparts:

```
Ushr  = 0xA4   UshrII = 0xDD
Rol   = 0xA5   RolII  = 0xDE
Ror   = 0xA6   RorII  = 0xDF
```

The mapping in [IrCompiler.cs](Interpreter/IR/IrCompiler.cs):

```
BITWISE_LOGICAL_LEFT_SHIFT  → Shl       (identical bit pattern, no new opcode)
BITWISE_LOGICAL_RIGHT_SHIFT → Ushr
BITWISE_ROTATE_LEFT         → Rol
BITWISE_ROTATE_RIGHT        → Ror
```

The dispatch loop in [VmExecutor.cs](Interpreter/Vm/VmExecutor.cs):

* Boxed `Ushr` / `Rol` / `Ror` route to `RuntimeValue.BitwiseUnsignedRightShiftedBy`
  / `BitwiseRotateLeftedBy` / `BitwiseRotateRightedBy` virtuals.
* Typed `UshrII` uses C# `ulong >> n` on an int64 shadow slot for zero-extended
  semantics. `RolII` / `RorII` invoke `System.Numerics.BitOperations.RotateLeft`
  / `RotateRight` on a `ulong`.
* All three deopt to the boxed path on tag mismatch or count out of range,
  preserving the precise error site.

### Typed-Int64 promotion policy
Only `<<`, `<<<` (both → `ShlII`), and `>>` (→ `ShrII`) participate in the
IR rewriter's promotion to typed-Int64 opcodes. **`>>>`, `<<<<`, `>>>>` are
deliberately excluded** because their boxed-path semantics differ from the
naive 64-bit interpretation:

* `>>>` errors on a negative `number`, but a typed-Int64 logical shift would
  silently zero-fill — masking the diagnostic.
* Rotates error on `number` entirely, but a typed-Int64 rotate would silently
  succeed.

A user who wants the typed fast path on rotates / unsigned-shift writes
`long_var <<<< 4l`. The `LongValue` overload then dispatches the same
64-bit rotation that the boxed virtual would have produced — semantics and
performance both intact.

### RuntimeValue surface
[Interpreter/Values/RuntimeValue.cs](Interpreter/Values/RuntimeValue.cs)
adds three new virtuals plus a long-missing `BitwiseXoredBy` (the dispatcher
had a typo routing `BXor` to `BitwiseAndedBy` — repaired alongside this work).

The primitive value classes that already overrode `BitwiseLeftShiftedBy` /
`BitwiseRightShiftedBy` now override the new methods too:
`IntegerValue`, `LongValue`, `UnsignedIntegerValue`, `UnsignedLongValue`,
`ShortValue`, `UnsignedShortValue`, `UnsignedInt128Value`. The fixed-width
implementations delegate to `System.Numerics.BitOperations` for the host's
native rotate intrinsics.

[NumberValue](Interpreter/Values/Primitives/NumberValue.cs) implements
`BitwiseUnsignedRightShiftedBy` (delegates to `BigNumber.RightShift` for
non-negative operands; errors on negatives) and explicitly rejects
`BitwiseRotateLeftedBy` / `BitwiseRotateRightedBy` with width-aware error
text.

### Shared count normalisation
[Interpreter/Values/Primitives/ShiftCount.cs](Interpreter/Values/Primitives/ShiftCount.cs)
centralises the count-extraction rules every numeric primitive needs:

* Accepts any integer-valued RuntimeValue (`Integer`, `Long`, `Short`, `Byte`,
  the unsigned counterparts, `Int128`, `UnsignedInt128`, and integer-scaled
  `Number`).
* Rejects fractional `number`s and non-numeric operands with a precise
  diagnostic.
* Rejects negative counts unconditionally.
* For fixed-width callers (`width > 0`), masks the count modulo the width.
* For arbitrary-precision callers (`width = 0`), passes the count through
  capped at `Int32.MaxValue` (BigInteger's shift API takes an int).

Both pre-existing shifts (`<<`, `>>`) on `IntegerValue`, `LongValue`,
`NumberValue` and the new family use this helper, so the diagnostics and
the cross-type behavior are identical across operators.

### Compound assignment
The compound-assignment paths in [AssignmentHelper.cs](Interpreter/Runtime/AssignmentHelper.cs),
[ListAssignmentHelper.cs](Interpreter/Runtime/ListAssignmentHelper.cs),
[VariableAssignmentNodeVisitor.cs](Interpreter/Visitors/Variables/VariableAssignmentNodeVisitor.cs),
[ListAssignmentNodeVisitor.cs](Interpreter/Visitors/Variables/ListAssignmentNodeVisitor.cs),
and [DereferenceAssignmentNodeVisitor.cs](Interpreter/Visitors/Operations/DereferenceAssignmentNodeVisitor.cs)
all recognise the six new compound tokens and route each through the matching
binary virtual.

The IR compiler's `OP_STORE_GLOBAL` slow path covers compound assignments
end-to-end — it reads the operator from `node.AssignmentToken.Type`, so no
opcode changes were needed at the assignment layer.

## §4. Regression tests

[tests_shifts.ra](tests_shifts.ra) exercises every operator across:

* Arbitrary-precision `number` shifts.
* Fixed-width `int` shifts (including `<< 33` → masked to `<< 1`).
* Fixed-width `int` logical-right (`>>>`) on negative values.
* Rotates and rotate-composition identity.
* All six compound-assign forms.
* Precedence vs. additive ops and left-associativity.
* Dynamic counts (typed and untyped).
* Negative diagnostic: `<<<<` on a `number` errors out, caught at the Ra level.

Run with `dotnet run -- tests_shifts.ra`.

## §5. Decisions explicitly rejected

* **Saturating shifts as a separate operator family.** Too many semantic
  variants (saturate-to-MIN / saturate-to-MAX / wrap-on-overflow). Defer to a
  future explicit annotation if real demand surfaces.
* **A separate opcode for `<<<`.** The bit pattern is identical to `<<`; a
  distinct opcode would burn dispatch bandwidth for no observable difference.
  The token is preserved at the AST layer so future diagnostics can surface
  the operator the user typed.
* **Allowing rotates on `number`.** Defining the rotation modulus as
  "the smallest containing fixed-width type" would silently make
  `huge_bignumber <<<< 4` interpret the operand as int64 (or wider, ambiguously).
  Rejecting is the only choice that preserves the user's intent.
* **Auto-flipping a negative shift count.** `x << -1` does *not* silently
  become `x >> 1`. Negative counts are always programming errors — surfacing
  them is more valuable than the convenience of the implicit flip.

using System.Numerics;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Values.Primitives
{
    // Centralised normalisation of a shift / rotate count operand.
    //
    // Every numeric primitive (Integer, Long, Short, Byte, …, Number) needs to
    // accept the same canonical right-hand side, validate the same edge cases
    // (negative count, oversized count for a fixed-width type), and surface the
    // same diagnostic text. Implementing that inline in each value class would
    // (a) duplicate non-trivial range / sign / type handling and (b) drift over
    // time. This helper is the single source of truth — every shift/rotate
    // virtual on a numeric type calls TryGet to obtain the normalised count,
    // returning the same Error to the user when it cannot.
    //
    // For fixed-width types the caller passes the bit width (32, 64, 16, 8,
    // 128, …). The count is taken modulo the width — matching the host CPU's
    // shift semantics (C# `<<` / `>>` on int / long behaves this way, as do
    // x86 `shl` / `shr` with their masked CL register) and Java / JavaScript
    // shift counts on int / long. For arbitrary-precision `number` the
    // caller passes width=0 and the helper returns the raw count (capped at
    // Int32.MaxValue to satisfy BigInteger's API contract).
    //
    // Negative counts always error out. There is no `x << -1` ≡ `x >> 1`
    // implicit flip — that would silently mask programming bugs.
    internal static class ShiftCount
    {
        // Mask convention shared with the C# host:
        //   int    (32-bit):   count & 31
        //   long   (64-bit):   count & 63
        //   short  (16-bit):   count & 15  (Ra-specific; C# does NOT mask
        //                                  short shifts but Ra exposes a
        //                                  uniform fixed-width semantics)
        //   sbyte  ( 8-bit):   count &  7
        //   int128 (128-bit):  count & 127
        //
        // The width parameter is the bit count; passing width=0 disables
        // masking (used by BigNumber-backed `number`).
        public static Error? TryGet(
            RuntimeValue countOperand,
            int width,
            Position posStart,
            Position posEnd,
            Context context,
            out int count)
        {
            count = 0;

            BigInteger raw;
            switch (countOperand.Type)
            {
                case RuntimeValueType.Integer:
                    raw = ((IntegerValue)countOperand).Value;
                    break;
                case RuntimeValueType.Long:
                    raw = ((LongValue)countOperand).Value;
                    break;
                case RuntimeValueType.Short:
                    raw = ((ShortValue)countOperand).Value;
                    break;
                case RuntimeValueType.Byte:
                    raw = ((ByteValue)countOperand).Value;
                    break;
                case RuntimeValueType.UnsignedInteger:
                    raw = ((UnsignedIntegerValue)countOperand).Value;
                    break;
                case RuntimeValueType.UnsignedLong:
                    raw = ((UnsignedLongValue)countOperand).Value;
                    break;
                case RuntimeValueType.UnsignedShort:
                    raw = ((UnsignedShortValue)countOperand).Value;
                    break;
                case RuntimeValueType.Int128:
                    raw = (BigInteger)((Int128Value)countOperand).Value;
                    break;
                case RuntimeValueType.UnsignedInt128:
                    raw = (BigInteger)((UnsignedInt128Value)countOperand).Value;
                    break;
                case RuntimeValueType.Number:
                {
                    var bn = ((NumberValue)countOperand).Value;
                    if (!bn.Scale.IsZero)
                    {
                        return new RuntimeError(posStart, posEnd,
                            "shift / rotate count must be an integer, not a fractional 'number'",
                            context,
                            code: DiagnosticCode.RuntimeGeneric,
                            primaryLabel: "non-integer shift count",
                            help: "the right-hand side of a shift / rotate operator must be a whole number");
                    }
                    raw = bn.Unscaled;
                    break;
                }
                default:
                    return new RuntimeError(posStart, posEnd,
                        $"shift / rotate count must be an integer type, got '{countOperand.Type.ToString().ToLower()}'",
                        context,
                        code: DiagnosticCode.RuntimeGeneric,
                        primaryLabel: "non-numeric shift count",
                        help: "use an int, long, short, byte, or number as the right-hand side of a shift operator");
            }

            if (raw.Sign < 0)
            {
                return new RuntimeError(posStart, posEnd,
                    $"shift / rotate count cannot be negative ({raw})",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "negative shift count",
                    help: "shift / rotate counts are unsigned; flip the operator instead of negating the count");
            }

            if (width <= 0)
            {
                // Arbitrary-precision path. The BigInteger shift API takes a
                // 32-bit int so we cap at Int32.MaxValue (any practical shift
                // count above that already produces a number too large to
                // represent in memory; rejecting earlier than that does no
                // good).
                if (raw > int.MaxValue) raw = int.MaxValue;
                count = (int)raw;
                return null;
            }

            // Fixed-width path. Mask modulo the width. The mask works on the
            // BigInteger directly so 128-bit widths stay correct.
            BigInteger mask = width - 1;
            count = (int)(raw & mask);
            return null;
        }
    }
}

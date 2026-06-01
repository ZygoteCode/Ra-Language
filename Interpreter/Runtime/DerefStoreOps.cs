using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared `*ref op= value` write-through logic, factored out of
    // DereferenceAssignmentNodeVisitor so the IR-lowered OP_DEREF_STORE handler
    // and the visitor fallback run byte-identical rules. The caller has already
    // evaluated the reference operand and the RHS value; this applies the
    // (possibly compound) assignment operator and writes through the reference.
    public static class DerefStoreOps
    {
        // The IR compiler lowers `*ref op= v` to OP_DEREF_STORE only when the
        // operator is one of these; anything else falls back to the visitor.
        // (Every assignment-operator TokenType is already covered, so the gate
        // is really just "is this an assignment token".)
        public static bool IsSupported(TokenType op) => op switch
        {
            TokenType.EQ or TokenType.PLUS_EQ or TokenType.MINUS_EQ or TokenType.MUL_EQ
            or TokenType.DIV_EQ or TokenType.MODULO_EQ or TokenType.POW_EQ
            or TokenType.BITWISE_AND_EQ or TokenType.BITWISE_OR_EQ
            or TokenType.BITWISE_LEFT_SHIFT_EQ or TokenType.BITWISE_RIGHT_SHIFT_EQ
            or TokenType.BITWISE_LOGICAL_LEFT_SHIFT_EQ or TokenType.BITWISE_LOGICAL_RIGHT_SHIFT_EQ
            or TokenType.BITWISE_ROTATE_LEFT_EQ or TokenType.BITWISE_ROTATE_RIGHT_EQ
            or TokenType.AND_EQ or TokenType.OR_EQ => true,
            _ => false,
        };

        public static ValueResult Apply(
            RuntimeValue? refValue, RuntimeValue newValue, TokenType op,
            Context context, Position posStart, Position posEnd)
        {
            if (refValue is not IReferenceValue refTarget)
                return (null, new RuntimeError(posStart, posEnd,
                    $"left-hand side of '*=' is not a reference (got '{refValue?.Type.ToString().ToLowerInvariant() ?? "null"}')",
                    context,
                    code: DiagnosticCode.RuntimeBorrowViolation,
                    primaryLabel: "operand of '*' is not a borrow / reference",
                    help: "to write through an alias, take '&mut x' first, then assign through that borrow"));

            (RuntimeValue? result, Error? error) = op switch
            {
                TokenType.EQ => (newValue, null),
                TokenType.PLUS_EQ => refTarget.Value.AddedTo(newValue),
                TokenType.MINUS_EQ => refTarget.Value.SubbedBy(newValue),
                TokenType.MUL_EQ => refTarget.Value.MultedBy(newValue),
                TokenType.DIV_EQ => refTarget.Value.DivedBy(newValue),
                TokenType.MODULO_EQ => refTarget.Value.ModuledBy(newValue),
                TokenType.POW_EQ => refTarget.Value.PowedBy(newValue),
                TokenType.BITWISE_AND_EQ => refTarget.Value.BitwiseAndedBy(newValue),
                TokenType.BITWISE_OR_EQ => refTarget.Value.BitwiseOredBy(newValue),
                TokenType.BITWISE_LEFT_SHIFT_EQ => refTarget.Value.BitwiseLeftShiftedBy(newValue),
                TokenType.BITWISE_RIGHT_SHIFT_EQ => refTarget.Value.BitwiseRightShiftedBy(newValue),
                TokenType.BITWISE_LOGICAL_LEFT_SHIFT_EQ => refTarget.Value.BitwiseLeftShiftedBy(newValue),
                TokenType.BITWISE_LOGICAL_RIGHT_SHIFT_EQ => refTarget.Value.BitwiseUnsignedRightShiftedBy(newValue),
                TokenType.BITWISE_ROTATE_LEFT_EQ => refTarget.Value.BitwiseRotateLeftedBy(newValue),
                TokenType.BITWISE_ROTATE_RIGHT_EQ => refTarget.Value.BitwiseRotateRightedBy(newValue),
                TokenType.AND_EQ => refTarget.Value.AndedBy(newValue),
                TokenType.OR_EQ => refTarget.Value.OredBy(newValue),
                _ => (null, new RuntimeError(posStart, posEnd,
                        $"unsupported assignment operator through '*': {op}",
                        context, code: DiagnosticCode.RuntimeGeneric)),
            };

            if (error != null) return (null, error);
            if (result == null)
                return (null, new RuntimeError(posStart, posEnd,
                    "could not compute assigned value", context, code: DiagnosticCode.RuntimeGeneric));

            try
            {
                refTarget.Value = result;
            }
            catch (System.Exception ex)
            {
                return (null, new RuntimeError(posStart, posEnd,
                    $"failed to assign through reference: {ex.Message}",
                    context, code: DiagnosticCode.RuntimeBorrowViolation));
            }

            return (result.SetContext(context).SetPos(posStart, posEnd), null);
        }
    }
}

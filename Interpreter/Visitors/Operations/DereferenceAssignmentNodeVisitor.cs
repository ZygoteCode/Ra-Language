using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    // Handles `*ref = value` and `*ref op= value`. The RefTarget must resolve to
    // an IReferenceValue (BorrowValue, ReferenceValue, ClassFieldReferenceValue,
    // ...). For BorrowValue specifically the setter enforces that the borrow is
    // mutable and live.
    public class DereferenceAssignmentNodeVisitor : NodeVisitor<DereferenceAssignmentNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(DereferenceAssignmentNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var refValue = res.Register(await interpreter.Visit(node.RefTarget, context));
            if (res.ShouldReturn()) return res;

            if (refValue is not IReferenceValue refTarget)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"left-hand side of '*=' is not a reference (got '{refValue?.Type.ToString().ToLowerInvariant() ?? "null"}')",
                    context,
                    code: DiagnosticCode.RuntimeBorrowViolation,
                    primaryLabel: "operand of '*' is not a borrow / reference",
                    help: "to write through an alias, take '&mut x' first, then assign through that borrow"));

            var newValue = res.Register(await interpreter.Visit(node.ValueNode, context));
            if (res.ShouldReturn()) return res;

            // Compute the resulting value taking compound-assignment operators into
            // account: `*r += 5` should read through, add, write back.
            (RuntimeValue? result, Error? error) = node.AssignmentToken.Type switch
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
                TokenType.AND_EQ => refTarget.Value.AndedBy(newValue),
                TokenType.OR_EQ => refTarget.Value.OredBy(newValue),
                _ => (null, new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"unsupported assignment operator through '*': {node.AssignmentToken.Type}",
                        context, code: DiagnosticCode.RuntimeGeneric)),
            };

            if (error != null) return res.Failure(error);
            if (result == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "could not compute assigned value", context, code: DiagnosticCode.RuntimeGeneric));

            try
            {
                refTarget.Value = result;
            }
            catch (System.Exception ex)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"failed to assign through reference: {ex.Message}",
                    context,
                    code: DiagnosticCode.RuntimeBorrowViolation));
            }

            return res.Success(result.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

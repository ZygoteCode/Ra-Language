using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Runtime
{
    // Shared body of DereferenceNodeVisitor — `*expr`. Reads through a
    // BorrowValue or IReferenceValue to yield the underlying value.
    public static class DereferenceHelper
    {
        public static RuntimeResult Apply(DereferenceNode node, Context context, RuntimeValue target)
        {
            var res = new RuntimeResult();
            if (target is BorrowValue bv)
            {
                if (bv.Released)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"cannot dereference '{bv.SourceName}': borrow has been released (use-after-free)",
                        context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: "use-after-free",
                        help: "the borrowed binding went out of scope or was moved"));
                if (bv.SourceEntry.IsMoved)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"cannot dereference '{bv.SourceName}': source was moved",
                        context,
                        code: DiagnosticCode.RuntimeMovedValue));
                var v = bv.SourceEntry.Value;
                return res.Success(v.Aliased().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (target is IReferenceValue refVal)
            {
                var v = refVal.Value;
                return res.Success(v.Aliased().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                $"cannot dereference value of type '{target.Type.ToString().ToLower()}': not a reference",
                context,
                code: DiagnosticCode.RuntimeBorrowViolation,
                primaryLabel: "operand of '*' is not a borrow / reference",
                help: "'*x' requires x to be a borrow ('&y' or '&mut y') or a reference"));
        }
    }
}

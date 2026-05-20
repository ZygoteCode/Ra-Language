using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    // `*expr` — reads through a reference / borrow. Yields the underlying value.
    // For assignment-as-target (`*r = ...`) the visitor for VariableAssignmentNode
    // / similar is responsible for routing the write back through the
    // IReferenceValue setter; this visitor only handles the read case.
    public class DereferenceNodeVisitor : NodeVisitor<DereferenceNode>
    {
        protected sealed override RuntimeResult VisitNode(DereferenceNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var target = res.Register(interpreter.Visit(node.Target, context));
            if (res.ShouldReturn()) return res;
            if (target == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "dereference of null target",
                    context,
                    code: DiagnosticCode.RuntimeBorrowViolation));

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
                return res.Success((v.IsCopy ? v.Copy() : v).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (target is IReferenceValue refVal)
            {
                var v = refVal.Value;
                return res.Success((v.IsCopy ? v.Copy() : v).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
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

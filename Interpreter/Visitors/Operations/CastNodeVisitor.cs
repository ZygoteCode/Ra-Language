using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class CastNodeVisitor : NodeVisitor<CastNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(CastNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var val = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Expression, context, interpreter));
            if (res.ShouldReturn()) return res;

            if (val == null) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Cannot cast null value", context));
            RuntimeValue? casted; Error? error;
            try { (casted, error) = val.CastTo(node.TargetType); }
            catch (System.Exception ex)
            {
                casted = null;
                error = new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"cannot cast '{val.Type}' to '{node.TargetType}'", context,
                    code: DiagnosticCode.RuntimeTypeMismatch, primaryLabel: "conversion failed",
                    help: ex is System.OverflowException ? "value is out of range for the target type — use a wider type, or `as?` for a null-on-failure cast" : "the value cannot be converted to the target type");
            }
            if (error != null)
            {
                // `as?` swallows the conversion failure and yields null.
                if (node.Safe) return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                return res.Failure(error);
            }
            if (casted == null) return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            return res.Success(casted.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
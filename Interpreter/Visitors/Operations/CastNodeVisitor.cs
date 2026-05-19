using RaLanguage.Errors;
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
        protected sealed override RuntimeResult VisitNode(CastNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var val = res.Register(interpreter.Visit(node.Expression, context));
            if (res.ShouldReturn()) return res;

            if (val == null) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Cannot cast null value", context));
            (RuntimeValue? casted, Error? error) = val.CastTo(node.TargetType);
            if (error != null) return res.Failure(error);
            if (casted == null) return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            return res.Success(casted.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}
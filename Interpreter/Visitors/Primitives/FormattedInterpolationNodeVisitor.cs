using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Types.Formatting;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    // Dispatch target for a `${expr:spec}` segment that has been peeled off
    // from a string literal. The parser only emits FormattedInterpolationNode
    // inside an interpolated string, but having a standalone visitor keeps the
    // node fully composable: a visitor for AstNodeType.FormattedInterpolation
    // is required to exist (the dispatch table is dense) and any future
    // call-site that injects such a node will get the same semantics for free.
    public class FormattedInterpolationNodeVisitor : NodeVisitor<FormattedInterpolationNode>
    {
        protected sealed override RuntimeResult VisitNode(FormattedInterpolationNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var inner = res.Register(interpreter.Visit(node.Expression, context));
            if (res.ShouldReturn()) return res;

            var (text, error) = FormatEngine.Format(inner!, node.FormatSpec, node.PositionStart, node.PositionEnd, context);
            if (error != null) return res.Failure(error);

            return res.Success(new StringValue(text ?? string.Empty)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

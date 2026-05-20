using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Patterns;

namespace RaLanguage.Interpreter.Visitors.Patterns
{
    // Implements the `?` postfix try-unwrap operator.
    //
    // The target expression must evaluate to a `Result<T, E>` value. If it is
    // `Result.Ok(v)`, the node yields `v`. If it is `Result.Err(e)`, the
    // surrounding function returns `Result.Err(e)` early — without throwing
    // a host-level exception, by using the existing RuntimeResult propagation
    // (`SuccessReturn`). This integrates with the interpreter's standard
    // function-return mechanism, so it composes naturally with try/catch,
    // loops, and async fibers.
    public class TryUnwrapNodeVisitor : NodeVisitor<TryUnwrapNode>
    {
        protected sealed override RuntimeResult VisitNode(TryUnwrapNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var value = res.Register(interpreter.Visit(node.Target, context));
            if (res.ShouldReturn()) return res;

            if (value is not EnumValue ev || !string.Equals(ev.EnumName, "Result", System.StringComparison.Ordinal))
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "'?' can only be applied to a 'Result<T, E>' value",
                    context,
                    code: DiagnosticCode.RuntimeTypeMismatch,
                    primaryLabel: value == null ? "got nothing" : $"got {value.Type}",
                    help: "use '?' only on expressions that return Result.Ok(v) or Result.Err(e)"));
            }

            if (string.Equals(ev.MemberName, "Ok", System.StringComparison.Ordinal))
            {
                if (ev.Payload.Count != 1)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"Result.Ok payload arity {ev.Payload.Count} is unexpected",
                        context, code: DiagnosticCode.RuntimeTypeMismatch));
                }
                return res.Success(ev.Payload[0].Copy().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            if (string.Equals(ev.MemberName, "Err", System.StringComparison.Ordinal))
            {
                // Early return Result.Err(e) via the standard return channel.
                return res.SuccessReturn(value.Copy().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                $"'?' encountered unexpected Result variant '{ev.MemberName}'",
                context,
                code: DiagnosticCode.RuntimeTypeMismatch,
                primaryLabel: "expected Ok or Err",
                help: "do not redefine the built-in Result enum"));
        }
    }
}

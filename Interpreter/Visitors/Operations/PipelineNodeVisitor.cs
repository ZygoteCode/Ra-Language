using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Calls;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Operations;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    // Pipeline operator `value |> callable`.
    //
    // Semantics:
    //   * the LHS expression is evaluated exactly once and becomes the implicit
    //     first positional argument of the RHS call;
    //   * if the RHS is a `FunctionCallNode` (`value |> f(extra)`), the
    //     existing positional / named arguments of that call are preserved and
    //     `value` is prepended;
    //   * if the RHS is any other callable expression (`value |> f`,
    //     `value |> obj.method`), it is evaluated as a callable and invoked
    //     with `[value]`.
    //
    // The implementation NEVER rewrites the source AST: the RHS call shape is
    // observed, then a fresh argument list is built around the LHS value and
    // handed to the shared FunctionCallExecutor. That keeps source spans
    // intact and guarantees the LHS expression is not visited twice (no
    // duplicate side effects).
    public class PipelineNodeVisitor : NodeVisitor<PipelineNode>
    {
        protected sealed override RuntimeResult VisitNode(PipelineNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (context.AreCallsBlocked)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "pipeline calls are not allowed in this context",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "calls disabled here",
                    help: "this expression runs in a context (e.g. an annotation argument) where calls are forbidden"));
            }

            // Evaluate LHS exactly once. The pipeline guarantees side-effect
            // ordering: LHS runs to completion before any callee resolution
            // or RHS argument evaluation begins.
            var leftValue = res.Register(interpreter.Visit(node.LeftNode, context));
            if (res.ShouldReturn()) return res;
            if (leftValue == null)
            {
                return res.Failure(new RuntimeError(node.LeftNode.PositionStart, node.LeftNode.PositionEnd,
                    "left-hand side of '|>' produced no value",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "expected a value to feed into the pipeline",
                    help: "ensure the expression before '|>' returns a concrete value"));
            }

            if (node.RightNode is FunctionCallNode call)
            {
                var calleeVal = res.Register(interpreter.Visit(call.NodeToCall, context));
                if (res.ShouldReturn()) return res;

                EnsureCallable(calleeVal, node, context, ref res);
                if (res.Error != null) return res;

                var argEval = FunctionCallExecutor.EvaluateArguments(
                    call.ArgNodes, context, interpreter,
                    out var positionalArgs, out var namedArgs);
                if (argEval.Error != null) return res.Failure(argEval.Error);

                // Prepend the piped value as the first positional argument.
                var prepended = new List<RuntimeValue>(positionalArgs.Count + 1) { leftValue };
                prepended.AddRange(positionalArgs);

                return FunctionCallExecutor.Invoke(
                    calleeVal!,
                    prepended,
                    namedArgs,
                    call.GenericTypeArgs,
                    node.PositionStart,
                    node.PositionEnd,
                    context);
            }

            // RHS is any other callable expression: identifier, lambda, member
            // access, parenthesised call expression that returns a callable,
            // etc. Evaluate it once, validate, and invoke with the single
            // piped argument.
            var rhsCallee = res.Register(interpreter.Visit(node.RightNode, context));
            if (res.ShouldReturn()) return res;

            EnsureCallable(rhsCallee, node, context, ref res);
            if (res.Error != null) return res;

            var emptyNamed = new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal);
            var singleArg = new List<RuntimeValue>(1) { leftValue };

            return FunctionCallExecutor.Invoke(
                rhsCallee!,
                singleArg,
                emptyNamed,
                null,
                node.PositionStart,
                node.PositionEnd,
                context);
        }

        private static void EnsureCallable(RuntimeValue? candidate, PipelineNode node, Context context, ref RuntimeResult res)
        {
            if (candidate == null)
            {
                res.Failure(new RuntimeError(node.RightNode.PositionStart, node.RightNode.PositionEnd,
                    "right-hand side of '|>' is null and cannot be invoked",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "callee is null",
                    help: "ensure the right side of '|>' is a function, method, or lambda"));
                return;
            }

            // Every concrete callable (function, lambda, bound method, class
            // constructor, super proxy, native interop value) inherits from
            // BaseFunctionValue, so the single is-check is exhaustive. Any
            // other RuntimeValue would either return an "illegal operation"
            // from Execute (the inherited stub) or compute a useless result
            // - reject it up front so the diagnostic points at the operator.
            if (candidate is BaseFunctionValue) return;

            res.Failure(new RuntimeError(node.RightNode.PositionStart, node.RightNode.PositionEnd,
                "right-hand side of '|>' is not callable",
                context,
                code: DiagnosticCode.RuntimeTypeMismatch,
                primaryLabel: $"got {candidate.Type} where a callable was expected",
                help: "the right of '|>' must be a function, method, lambda, or another invocable value"));
        }
    }
}

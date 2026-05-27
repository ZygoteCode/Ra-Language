using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    // Runtime type test. The expression is evaluated once, then routed
    // through TypeSystem.IsAssignable against the tested type — so the
    // exact same rules that govern `let x: T = ...` declaration checks
    // also decide `x is T`. This keeps narrowing soundness in lockstep
    // with assignment soundness: a value that would not pass `x is T`
    // could not have flowed into a `T`-typed slot either. The `Negated`
    // flag inverts the boolean result for the `is not` form.
    //
    // Apply is the VM entry-point (called from OP_NATIVE_DEFINE), VisitNode
    // is the tree-walking entry-point. Both delegate to the same Apply body
    // so semantics never diverge between the IR-compiled and fallback
    // execution paths.
    public class IsTypeNodeVisitor : NodeVisitor<IsTypeNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(IsTypeNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(IsTypeNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var val = res.Register(await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(node.Expression, context, interpreter));
            if (res.ShouldReturn()) return res;

            if (val == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "Cannot apply 'is' to a missing value", context));
            }

            bool matches = TypeSystem.IsRuntimeTypeMatch(context, node.TestedType, val);
            if (node.Negated) matches = !matches;

            var result = new BooleanValue(matches);
            return res.Success(result.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Records;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Operations
{
    public class WithExpressionNodeVisitor : NodeVisitor<WithExpressionNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(WithExpressionNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(WithExpressionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var recvRes = await interpreter.Visit(node.Receiver, context);
            if (recvRes.Error != null) return res.Failure(recvRes.Error);
            var recv = recvRes.Value;

            // Evaluate every update value up front (receiver, then values in
            // source order) so this matches the IR-lowered OP_WITH path, which
            // necessarily has all values in slots before the handler runs. The
            // shared WithExpressionOps.Apply then validates names/types against
            // the record's primary shape, shallow-clones, and applies overrides
            // — identical rules for both the lowered handler and this fallback.
            var values = new System.Collections.Generic.List<RuntimeValue>(node.Updates.Count);
            foreach (var (_, valueExpr) in node.Updates)
            {
                var valRes = await interpreter.Visit(valueExpr, context);
                if (valRes.Error != null) return res.Failure(valRes.Error);
                values.Add(valRes.Value!);
            }

            var (result, error) = WithExpressionOps.Apply(recv, node, values, context);
            if (error != null) return res.Failure(error);
            return res.Success(result!);
        }
    }
}

using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Namespaces;
using RaLanguage.Interpreter.Values.Namespaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Namespaces;

namespace RaLanguage.Interpreter.Visitors.Namespaces
{
    public class UsingNamespaceNodeVisitor : NodeVisitor<UsingNamespaceNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(
            UsingNamespaceNode node,
            Context context,
            IInterpreter interpreter)
            => Apply(node, context);

        public static RuntimeResult Apply(UsingNamespaceNode node, Context context)
        {
            var res = new RuntimeResult();

            var segments = new string[node.Segments.Count];
            for (int i = 0; i < node.Segments.Count; i++)
            {
                segments[i] = node.Segments[i].Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(segments[i]))
                {
                    return res.Failure(new RuntimeError(
                        node.Segments[i].PositionStart,
                        node.Segments[i].PositionEnd,
                        "Namespace segment is empty",
                        context));
                }
            }

            // Resolve + inject via the shared helper so the IR-lowered
            // OP_DEFINE_TYPE handler runs byte-identical namespace logic.
            var (value, err) = UsingNamespaceOps.Apply(
                segments, node.HasAlias ? node.Alias : null,
                context, node.PositionStart, node.PositionEnd);
            if (err != null) return res.Failure(err);
            return res.Success(value!);
        }
    }
}

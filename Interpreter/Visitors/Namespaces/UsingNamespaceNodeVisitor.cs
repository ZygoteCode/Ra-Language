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

            var target = NamespaceRegistry.Global.Resolve(segments);
            if (target == null)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart,
                    node.PositionEnd,
                    $"Namespace '{node.QualifiedName}' is not defined",
                    context));
            }

            if (context.SymbolTable == null)
            {
                return res.Success(NullValue.Null
                    .SetContext(context)
                    .SetPos(node.PositionStart, node.PositionEnd));
            }

            if (node.HasAlias)
            {
                context.SymbolTable.Set(node.Alias!, target, isPublic: true);
            }
            else
            {
                foreach (var kvp in target.Members.EnumerateLocal())
                {
                    if (!kvp.Value.IsPublic) continue;

                    var existing = context.SymbolTable.GetEntry(kvp.Key);
                    if (existing != null && !ReferenceEquals(existing.Value, kvp.Value.Value))
                    {
                        continue;
                    }

                    context.SymbolTable.Set(
                        kvp.Key,
                        kvp.Value.Value,
                        isLet: kvp.Value.IsLet,
                        declaredType: kvp.Value.DeclaredType,
                        isStaticallyTyped: kvp.Value.IsStaticallyTyped,
                        isPublic: true);
                }
            }

            return res.Success(NullValue.Null
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

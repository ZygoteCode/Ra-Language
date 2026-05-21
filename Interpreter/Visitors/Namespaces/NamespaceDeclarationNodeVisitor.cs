using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Namespaces;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Namespaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Visitors.Namespaces
{
    public class NamespaceDeclarationNodeVisitor : NodeVisitor<NamespaceDeclarationNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(
            NamespaceDeclarationNode node,
            Context context,
            IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var segments = new string[node.Segments.Count];
            for (int i = 0; i < node.Segments.Count; i++)
            {
                string seg = node.Segments[i].Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(seg))
                {
                    return res.Failure(new RuntimeError(
                        node.Segments[i].PositionStart,
                        node.Segments[i].PositionEnd,
                        "Namespace segment is empty",
                        context));
                }
                segments[i] = seg;
            }

            var enclosing = FindEnclosingNamespace(context.SymbolTable);

            NamespaceLookupResult lookup;
            if (enclosing != null)
            {
                lookup = OpenRelative(enclosing, segments);
            }
            else
            {
                lookup = NamespaceRegistry.Global.GetOrCreate(segments);
            }

            if (!lookup.IsOk || lookup.Namespace == null)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart,
                    node.PositionEnd,
                    lookup.ErrorMessage ?? "Failed to open namespace",
                    context));
            }

            var leafNamespace = lookup.Namespace;

            if (enclosing == null)
            {
                var (exposed, exposeErr) = TryExposeRoot(context, leafNamespace);
                if (!exposed)
                {
                    return res.Failure(new RuntimeError(
                        node.PositionStart, node.PositionEnd, exposeErr!, context));
                }
            }

            var outerTable = context.SymbolTable;
            var scopeChain = BuildNamespaceScopeChain(leafNamespace, outerTable);

            var bodyContext = new Context(
                displayName: context.DisplayName,
                parent: context,
                parentEntryPos: node.PositionStart,
                extensions: context.Extensions);
            bodyContext.SymbolTable = scopeChain;
            bodyContext.IsInConstructor = context.IsInConstructor;

            var statements = ExtractStatements(node.Body);
            foreach (var stmt in statements)
            {
                var stmtRes = await interpreter.Visit(stmt, bodyContext);
                if (stmtRes.Error != null) return res.Failure(stmtRes.Error);
                if (stmtRes.FuncReturnValue != null)
                {
                    return res.Failure(new RuntimeError(
                        stmt.PositionStart,
                        stmt.PositionEnd,
                        "'return' is not valid at namespace scope",
                        bodyContext));
                }
            }

            FreezeFunctionClosures(leafNamespace, bodyContext);

            return res.Success(NullValue.Null
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd));
        }

        private static void FreezeFunctionClosures(NamespaceValue ns, Context bodyContext)
        {
            foreach (var kvp in ns.Members.EnumerateLocal())
            {
                if (kvp.Value.Value is BaseFunctionValue bfn)
                {
                    bfn.FreezeBindingContext(bodyContext);
                }
            }
        }

        private static IReadOnlyList<AstNode> ExtractStatements(AstNode body)
        {
            if (body is ScopeNode scope) return scope.Nodes;
            return new[] { body };
        }

        private static SymbolTable BuildNamespaceScopeChain(NamespaceValue leaf, SymbolTable? outer)
        {
            var ancestors = new List<NamespaceValue>();
            for (var cur = leaf; cur != null && !cur.IsRoot; cur = cur.ParentNamespace)
            {
                ancestors.Add(cur);
            }

            SymbolTable? parent = outer;
            for (int i = ancestors.Count - 1; i >= 1; i--)
            {
                parent = new NamespaceScopeView(ancestors[i].Members, parent);
            }

            return new NamespaceScopeView(leaf.Members, parent);
        }

        private static NamespaceValue? FindEnclosingNamespace(SymbolTable? table)
        {
            for (var cur = table; cur != null; cur = cur.Parent)
            {
                if (cur is NamespaceScopeView view)
                    return view.Target.Owner;
            }
            return null;
        }

        private static NamespaceLookupResult OpenRelative(NamespaceValue start, IReadOnlyList<string> segments)
        {
            var current = start;
            for (int i = 0; i < segments.Count; i++)
            {
                string seg = segments[i];
                if (string.IsNullOrEmpty(seg))
                    return NamespaceLookupResult.Fail("Namespace path contains an empty segment");

                var existing = current.Members.GetLocalEntry(seg);
                if (existing == null)
                {
                    current = current.GetOrCreateChild(seg);
                    continue;
                }

                if (existing.Value is NamespaceValue ns)
                {
                    current = ns;
                    continue;
                }

                string qual = current.QualifiedName + "." + string.Join(".", segments.Take(i + 1));
                return NamespaceLookupResult.Fail(
                    $"Cannot open namespace '{qual}': name conflicts with an existing non-namespace symbol");
            }

            return NamespaceLookupResult.Ok(current);
        }

        private static (bool ok, string? error) TryExposeRoot(Context context, NamespaceValue leaf)
        {
            NamespaceValue cur = leaf;
            while (cur.ParentNamespace != null && !cur.ParentNamespace.IsRoot)
            {
                cur = cur.ParentNamespace;
            }

            if (cur.IsRoot) return (true, null);

            var table = context.SymbolTable;
            if (table == null) return (true, null);

            string rootName = cur.Name;

            var existing = table.GetEntry(rootName);
            if (existing != null && !(existing.Value is NamespaceValue))
            {
                return (false, $"Cannot declare namespace '{rootName}': a non-namespace symbol with this name already exists");
            }

            if (existing != null && existing.Value is NamespaceValue existingNs)
            {
                if (ReferenceEquals(existingNs, cur)) return (true, null);
            }

            var top = table;
            while (top.Parent != null) top = top.Parent;
            top.Set(rootName, cur, isPublic: true);
            return (true, null);
        }
    }
}

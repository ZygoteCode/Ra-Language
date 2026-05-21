namespace RaLanguage.Parser.Nodes
{
    // Static analyser used by scope-creating visitors (IfNodeVisitor today; can be
    // reused by Try/Switch arms / match arms tomorrow) to decide whether a fresh
    // child Context+SymbolTable is necessary before executing a body subtree.
    //
    // Cost of a needless Copy: 2 allocations per visit (Context + SymbolTable) and
    // a parent-chain link that the GC must later trace. In hot if-in-loop code that's
    // pure overhead — only declarations require an isolated scope to die in.
    //
    // The analysis is intentionally shallow: it inspects the immediate top-level
    // statement list of the body, not the entire transitive subtree. Nested
    // sub-scopes (inner if/for/while/scope blocks) bring their own Copy when they
    // run, so their declarations cannot leak into the outer body's frame. Same with
    // function / class / struct / enum / interface / trait / extension / annotation
    // bodies — those are bound *in the surrounding scope* (so they count) but their
    // INNER decls don't escape.
    //
    // False-negative behaviour is safe: we always Copy when uncertain. False-positive
    // (i.e. claiming "no decls" when there are some) would corrupt the parent scope,
    // so the allow-list of "binding nodes" below must be kept exhaustive.
    internal static class AstScopeAnalysis
    {
        public static bool NeedsFreshScope(AstNode? body)
        {
            if (body == null) return false;
            if (body.NodeType == AstNodeType.Scope)
            {
                var s = (RaLanguage.Parser.Nodes.Special.ScopeNode)body;
                var nodes = s.Nodes;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (IntroducesBinding(nodes[i])) return true;
                }
                return false;
            }
            return IntroducesBinding(body);
        }

        private static bool IntroducesBinding(AstNode n)
        {
            switch (n.NodeType)
            {
                case AstNodeType.VariableDeclaration:
                case AstNodeType.FunctionDefinition:
                case AstNodeType.ClassDefinition:
                case AstNodeType.StructDefinition:
                case AstNodeType.EnumDefinition:
                case AstNodeType.InterfaceDefinition:
                case AstNodeType.TraitDefinition:
                case AstNodeType.ExtensionDefinition:
                case AstNodeType.AnnotationDefinition:
                case AstNodeType.ImportAll:
                case AstNodeType.ImportSelective:
                case AstNodeType.ImportAlias:
                case AstNodeType.NamespaceDeclaration:
                case AstNodeType.UsingNamespace:
                // TryUnwrap (`let x = expr?`) binds x in current scope.
                case AstNodeType.TryUnwrap:
                    return true;
                default:
                    return false;
            }
        }
    }
}

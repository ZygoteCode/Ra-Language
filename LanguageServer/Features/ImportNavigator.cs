using System.Collections.Generic;
using RaLanguage.Interpreter.Modules;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Navigation for module imports: jump from an import path string (or a module
    /// alias, used at the import or anywhere it is referenced) to the imported file,
    /// and expose import strings as clickable document links. Resolution goes through
    /// the interpreter's <see cref="ModuleResolver"/> via the workspace index.
    /// </summary>
    public static class ImportNavigator
    {
        /// <summary>Absolute path of the module the cursor's import/alias targets, or null.</summary>
        public static string? ResolveAtOffset(AstNode? ast, IReadOnlyList<Token> tokens, int offset, string currentFile, WorkspaceIndex workspace)
        {
            if (ast is not ScopeNode scope) return null;

            var aliases = new Dictionary<string, ModuleSpecifier>(System.StringComparer.Ordinal);
            foreach (var node in scope.Nodes)
            {
                switch (node)
                {
                    case ImportAllNode a:
                        if (Contains(a, offset)) return workspace.ResolveModulePath(a.Specifier, currentFile);
                        break;
                    case ImportSelectiveNode s:
                        if (Contains(s, offset)) return workspace.ResolveModulePath(s.Specifier, currentFile);
                        break;
                    case ImportAliasNode al:
                        aliases[al.Alias] = al.Specifier;
                        if (Contains(al, offset)) return workspace.ResolveModulePath(al.Specifier, currentFile);
                        break;
                }
            }

            // Cursor on a module-alias usage elsewhere (e.g. `things.foo` → go to module).
            if (TokenLocator.TryGetIdentifierAt(tokens, offset, out var tok) &&
                tok.Type == TokenType.IDENTIFIER &&
                aliases.TryGetValue(TokenLocator.Text(tok), out var spec))
            {
                return workspace.ResolveModulePath(spec, currentFile);
            }
            return null;
        }

        /// <summary>(string-token span, target absolute path) for each resolvable import.</summary>
        public static List<(int Start, int End, string TargetPath)> CollectLinks(AstNode? ast, IReadOnlyList<Token> tokens, string currentFile, WorkspaceIndex workspace)
        {
            var links = new List<(int, int, string)>();
            if (ast is not ScopeNode scope) return links;

            foreach (var node in scope.Nodes)
            {
                ModuleSpecifier? spec = node switch
                {
                    ImportAllNode a => a.Specifier,
                    ImportSelectiveNode s => s.Specifier,
                    ImportAliasNode al => al.Specifier,
                    _ => null,
                };
                if (spec == null) continue;

                string? target = workspace.ResolveModulePath(spec, currentFile);
                if (target == null) continue;

                if (TryFindStringToken(tokens, node.PositionStart.Idx, node.PositionEnd.Idx, out int ls, out int le))
                    links.Add((ls, le, target));
            }
            return links;
        }

        private static bool Contains(AstNode node, int offset)
            => offset >= node.PositionStart.Idx && offset <= node.PositionEnd.Idx;

        // The import path STRING token can sit outside the import node's (sometimes
        // narrow) span, so scan the whole logical line around the node anchor.
        private static bool TryFindStringToken(IReadOnlyList<Token> tokens, int start, int end, out int s, out int e)
        {
            s = e = 0;
            int anchor = TokenLocator.FloorIndex(tokens, start);
            if (anchor < 0) anchor = 0;
            int left = anchor;
            while (left - 1 >= 0 && tokens[left - 1].Type != TokenType.NEWLINE) left--;
            for (int i = left; i < tokens.Count && tokens[i].Type != TokenType.NEWLINE; i++)
            {
                if (tokens[i].Type == TokenType.STRING || tokens[i].Type == TokenType.STRING_TEXT)
                {
                    s = tokens[i].PositionStart.Idx;
                    e = tokens[i].PositionEnd.Idx;
                    return true;
                }
            }
            return false;
        }
    }
}

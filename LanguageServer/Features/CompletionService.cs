using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Workspace;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Context-aware completion. After <c>.</c> it resolves the receiver: a module
    /// alias yields that module's exports (cross-module), a type/enum yields its
    /// members/variants; otherwise a heuristic member union. General position offers
    /// in-scope locals/params (binder), declared + imported symbols, keywords, built-in
    /// types and snippets — callables carry their signature, parameter docs and a
    /// snippet insertion. Items are categorized with proper kinds (function vs variable
    /// vs type vs field).
    /// </summary>
    public sealed class CompletionService : ICompletionService
    {
        /// <summary>Set at initialize; enables cross-module member/symbol completion.</summary>
        public WorkspaceIndex? Workspace { get; set; }

        public CompletionList Compute(RaDocument document, Position position, CompletionContext? context)
        {
            var compilation = document.GetCompilation();
            var tokens = compilation.Tokens;
            int offset = document.Document.OffsetAt(position);
            var index = SymbolIndex.Build(compilation.Ast);
            var importedExports = BuildImportedExports(compilation.Ast, document.Document.FileName);

            var items = new List<CompletionItem>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);

            if (TryMemberReceiver(tokens, offset, out string receiver))
            {
                AddMemberCompletions(items, seen, receiver, compilation.Ast, index, importedExports, document.Document.FileName);
                return new CompletionList { IsIncomplete = false, Items = items.ToArray() };
            }

            // 1. In-scope locals / parameters (binder) — highest priority.
            var model = document.GetSemanticModel();
            foreach (var b in model.Symbols)
            {
                if (b.Kind is BoundKind.Variable or BoundKind.Parameter or BoundKind.LoopVariable
                    or BoundKind.CatchVariable or BoundKind.PatternBinding)
                {
                    Add(items, seen, b.Name, b.Kind == BoundKind.Parameter ? CompletionItemKind.Variable : CompletionItemKind.Variable, b.Word, "1");
                }
            }

            // 2. Declared symbols in this file (functions carry signatures).
            foreach (var s in index.TopLevel) AddSymbol(items, seen, s, "1");
            // members too (so a bare in-class member name still completes)
            foreach (var s in index.Flat)
                if (s.Kind is SymbolKind.Method or SymbolKind.Field or SymbolKind.Property)
                    AddSymbol(items, seen, s, "2");

            // 3. Imported module exports.
            foreach (var s in importedExports) AddSymbol(items, seen, s, "2");

            // 4. Keywords + built-in types.
            foreach (var kw in s_keywords) Add(items, seen, kw, CompletionItemKind.Keyword, "RA keyword", "3");
            foreach (var ty in s_builtinTypes) Add(items, seen, ty, CompletionItemKind.Class, "RA built-in type", "3");

            // 5. Snippets.
            foreach (var (label, snippet, detail) in s_snippets)
                items.Add(new CompletionItem
                {
                    Label = label,
                    Kind = CompletionItemKind.Snippet,
                    Detail = detail,
                    InsertText = snippet,
                    InsertTextFormat = InsertTextFormat.Snippet,
                    SortText = "4_" + label,
                });

            return new CompletionList { IsIncomplete = false, Items = items.ToArray() };
        }

        // ---- member completion ----

        private void AddMemberCompletions(List<CompletionItem> items, HashSet<string> seen, string receiver,
            AstNode? ast, SymbolIndex index, IReadOnlyList<RaSymbol> importedExports, string currentFile)
        {
            // Module alias → that module's exported symbols.
            if (Workspace != null)
            {
                string? modulePath = AliasModulePath(ast, receiver, currentFile);
                if (modulePath != null)
                {
                    foreach (var s in Workspace.ExportsOf(modulePath)) AddSymbol(items, seen, s, "1");
                    return;
                }
            }

            var table = new TypeTable(index, importedExports, Workspace);

            // Receiver is a variable of a known type (e.g. `p.` where p: Point, incl `p.inner.`
            // via inferred member types) OR a type/enum name → its members, INCLUDING inherited
            // ones (base classes, interfaces, traits) and extension methods.
            var recvType = VarEnv.Build(ast, table).Get(receiver);
            string? typeName = recvType?.Name ?? (table.IsKnownType(receiver) ? receiver : null);
            if (typeName != null && table.IsKnownType(typeName))
            {
                bool any = false;
                foreach (var m in table.AllMembers(typeName)) { AddSymbol(items, seen, m, "1"); any = true; }
                if (any) return;
            }

            // Unknown receiver type (no inference): offer the union of known member names.
            foreach (var s in index.Flat)
                if (s.Kind is SymbolKind.Method or SymbolKind.Field or SymbolKind.Property
                    or SymbolKind.EnumMember or SymbolKind.Event)
                    AddSymbol(items, seen, s, "2");
        }

        private List<RaSymbol> BuildImportedExports(AstNode? ast, string currentFile)
        {
            var list = new List<RaSymbol>();
            if (Workspace == null) return list;
            foreach (var path in Workspace.ResolveImports(currentFile, ast))
                list.AddRange(Workspace.ExportsOf(path));
            return list;
        }

        private string? AliasModulePath(AstNode? ast, string alias, string currentFile)
        {
            if (Workspace == null || ast is not ScopeNode scope) return null;
            foreach (var node in scope.Nodes)
                if (node is ImportAliasNode al && al.Alias == alias)
                    return Workspace.ResolveModulePath(al.Specifier, currentFile);
            return null;
        }

        private static bool TryMemberReceiver(IReadOnlyList<Token> tokens, int offset, out string receiver)
        {
            receiver = string.Empty;
            int i = TokenLocator.FloorIndex(tokens, offset);
            // The cursor sits right after the dot; FloorIndex can land on the next token
            // (e.g. NEWLINE) that starts exactly at the cursor. Step back to the last token
            // that starts strictly before the cursor (the dot, or the partial member ident).
            while (i >= 0 && tokens[i].PositionStart.Idx >= offset) i--;
            if (i < 0) return false;

            int dotIdx = -1;
            if (tokens[i].Type == TokenType.DOT) dotIdx = i;
            else if (tokens[i].Type == TokenType.IDENTIFIER && i > 0 && tokens[i - 1].Type == TokenType.DOT) dotIdx = i - 1;
            if (dotIdx <= 0) return false;

            int r = PrevMeaningful(tokens, dotIdx - 1);
            if (r < 0 || tokens[r].Type != TokenType.IDENTIFIER) return false;
            receiver = TokenLocator.Text(tokens[r]);
            return true;
        }

        private static int PrevMeaningful(IReadOnlyList<Token> tokens, int from)
        {
            for (int i = from; i >= 0; i--)
                if (tokens[i].Type != TokenType.NEWLINE) return i;
            return -1;
        }

        // ---- item builders ----

        private static void AddSymbol(List<CompletionItem> items, HashSet<string> seen, RaSymbol symbol, string sortBucket)
        {
            if (string.IsNullOrEmpty(symbol.Name) || !seen.Add(symbol.Name)) return;
            var kind = MapKind(symbol.Kind);

            if (symbol.Kind is SymbolKind.Function or SymbolKind.Method or SymbolKind.Constructor)
            {
                var parameters = symbol.Parameters ?? new List<string>();
                string signature = symbol.Name + "(" + string.Join(", ", parameters) + ")";
                items.Add(new CompletionItem
                {
                    Label = symbol.Name,
                    Kind = kind,
                    Detail = signature,
                    Documentation = MarkupContent.Markdown(
                        "```ra\n" + KindWord(symbol.Kind) + " " + signature + "\n```" +
                        (parameters.Count > 0 ? "\n\nParameters: " + string.Join(", ", parameters) : "")),
                    InsertText = BuildCallSnippet(symbol.Name, parameters),
                    InsertTextFormat = InsertTextFormat.Snippet,
                    SortText = sortBucket + "_" + symbol.Name,
                });
                return;
            }

            items.Add(new CompletionItem
            {
                Label = symbol.Name,
                Kind = kind,
                Detail = KindWord(symbol.Kind) + " " + symbol.Name + (symbol.Detail ?? string.Empty),
                SortText = sortBucket + "_" + symbol.Name,
            });
        }

        private static string BuildCallSnippet(string name, List<string> parameters)
        {
            if (parameters.Count == 0) return name + "()";
            var sb = new System.Text.StringBuilder(name);
            sb.Append('(');
            for (int i = 0; i < parameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("${").Append(i + 1).Append(':').Append(parameters[i]).Append('}');
            }
            sb.Append(')');
            return sb.ToString();
        }

        private static bool Add(List<CompletionItem> items, HashSet<string> seen, string label, CompletionItemKind kind, string detail, string sortBucket)
        {
            if (string.IsNullOrEmpty(label) || !seen.Add(label)) return false;
            items.Add(new CompletionItem
            {
                Label = label,
                Kind = kind,
                Detail = detail,
                SortText = sortBucket + "_" + label,
            });
            return true;
        }

        private static CompletionItemKind MapKind(SymbolKind kind) => kind switch
        {
            SymbolKind.Function => CompletionItemKind.Function,
            SymbolKind.Method => CompletionItemKind.Method,
            SymbolKind.Constructor => CompletionItemKind.Constructor,
            SymbolKind.Class => CompletionItemKind.Class,
            SymbolKind.Struct => CompletionItemKind.Struct,
            SymbolKind.Enum => CompletionItemKind.Enum,
            SymbolKind.EnumMember => CompletionItemKind.EnumMember,
            SymbolKind.Interface => CompletionItemKind.Interface,
            SymbolKind.Field => CompletionItemKind.Field,
            SymbolKind.Property => CompletionItemKind.Property,
            SymbolKind.Event => CompletionItemKind.Event,
            SymbolKind.Namespace => CompletionItemKind.Module,
            SymbolKind.Constant => CompletionItemKind.Constant,
            _ => CompletionItemKind.Variable,
        };

        private static string KindWord(SymbolKind kind) => kind switch
        {
            SymbolKind.Function or SymbolKind.Method => "fn",
            SymbolKind.Constructor => "constructor",
            SymbolKind.Class => "class",
            SymbolKind.Struct => "struct",
            SymbolKind.Enum => "enum",
            SymbolKind.EnumMember => "enum member",
            SymbolKind.Interface => "interface",
            SymbolKind.Field => "field",
            SymbolKind.Property => "prop",
            SymbolKind.Event => "event",
            SymbolKind.Namespace => "namespace",
            _ => "var",
        };

        private static readonly string[] s_keywords =
        {
            "and", "as", "abstract", "async", "await", "break", "cancellable", "case", "catch", "class",
            "const", "continue", "default", "del", "delay", "delegate", "do", "elif", "else", "emit",
            "enum", "event", "extend", "factory", "final", "finally", "fn", "for", "from", "goto",
            "if", "impl", "import", "in", "interface", "is", "lazy", "let", "match", "move", "mut",
            "namespace", "nameof", "not", "null", "operator", "or", "override", "pass", "prop", "pub",
            "record", "ref", "ret", "retry", "self", "spawn", "static", "step", "struct", "super",
            "switch", "throw", "times", "to", "tolerant", "trait", "try", "typeof", "using", "var",
            "where", "while", "with", "yield", "true", "false",
        };

        private static readonly string[] s_builtinTypes =
        {
            "int", "number", "long", "float", "double", "uint", "ulong", "short", "ushort",
            "int128", "uint128", "decimal", "byte", "bool", "string", "char", "void", "object", "any",
        };

        private static readonly (string Label, string Snippet, string Detail)[] s_snippets =
        {
            ("if", "if (${1:condition})\n{\n\t$0\n}", "If statement"),
            ("if else", "if (${1:condition})\n{\n\t$2\n}\nelse\n{\n\t$0\n}", "If / else"),
            ("while", "while (${1:condition})\n{\n\t$0\n}", "While loop"),
            ("for in", "for ${1:item} in ${2:collection}\n{\n\t$0\n}", "For-in loop"),
            ("fn", "fn ${1:name}(${2:args})\n{\n\t$0\n}", "Function"),
            ("pub fn", "pub fn ${1:name}(${2:args})\n{\n\t$0\n}", "Public function"),
            ("class", "class ${1:Name}\n{\n\t$0\n}", "Class"),
            ("struct", "struct ${1:Name}\n{\n\t$0\n}", "Struct"),
            ("enum", "enum ${1:Name}\n{\n\t${2:Variant}\n}", "Enum"),
            ("match", "match ${1:value}\n{\n\t${2:pattern} => $0\n}", "Match expression"),
            ("try", "try\n{\n\t$1\n}\ncatch (${2:error})\n{\n\t$0\n}", "Try / catch"),
        };
    }
}

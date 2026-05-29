using System.Collections.Generic;
using System.Linq;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Contextual hover. Resolves the lexeme under the cursor and renders: keyword
    /// help, built-in type help, or — for identifiers — the matching declaration's
    /// kind + signature taken from the structural <see cref="SymbolIndex"/>.
    /// </summary>
    public sealed class HoverService : IHoverService
    {
        public Hover? Compute(RaDocument document, Position position)
        {
            var compilation = document.GetCompilation();
            var doc = document.Document;
            int offset = doc.OffsetAt(position);

            if (!TokenLocator.TryGetIdentifierAt(compilation.Tokens, offset, out var token))
            {
                return null;
            }

            int start = token.PositionStart.Idx;
            int end = token.PositionEnd.Idx;
            string lexeme = Slice(doc.Text, start, end);
            var range = doc.RangeOf(start, end);

            string markdown;
            if (token.Type == TokenType.KEYWORD)
            {
                markdown = KeywordHover(lexeme);
            }
            else if (s_builtinTypes.TryGetValue(lexeme, out var typeDoc))
            {
                markdown = Fenced(lexeme) + "\n\n" + typeDoc;
            }
            else
            {
                var bound = document.GetSemanticModel().SymbolAt(offset);
                markdown = bound != null
                    ? Fenced(bound.Word + " " + lexeme)
                    : IdentifierHover(compilation.Ast, lexeme);
            }

            return new Hover { Contents = MarkupContent.Markdown(markdown), Range = range };
        }

        private static string IdentifierHover(Parser.Nodes.AstNode? ast, string name)
        {
            var index = SymbolIndex.Build(ast);
            var match = index.FindByName(name).FirstOrDefault();
            if (match == null)
            {
                return Fenced("(identifier) " + name);
            }

            string kindWord = KindWord(match.Kind);
            string signature = kindWord + " " + match.Name + (match.Detail ?? string.Empty);
            string body = Fenced(signature);
            if (!match.IsPublic)
            {
                body += "\n\n_private_";
            }
            return body;
        }

        private static string KeywordHover(string keyword)
        {
            string desc = s_keywordDocs.TryGetValue(keyword, out var d) ? d : "Ra language keyword.";
            return Fenced(keyword) + "\n\n" + desc;
        }

        private static string KindWord(SymbolKind kind) => kind switch
        {
            SymbolKind.Function => "fn",
            SymbolKind.Method => "fn",
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
            SymbolKind.Constant => "const",
            _ => "var",
        };

        private static string Fenced(string code) => "```ra\n" + code + "\n```";

        private static string Slice(string text, int start, int end)
        {
            if (start < 0) start = 0;
            if (end > text.Length) end = text.Length;
            if (end <= start) return string.Empty;
            return text.Substring(start, end - start);
        }

        private static readonly Dictionary<string, string> s_builtinTypes = new(System.StringComparer.Ordinal)
        {
            ["int"] = "Signed integer.",
            ["number"] = "Arbitrary-precision number.",
            ["long"] = "64-bit signed integer.",
            ["float"] = "32-bit floating point.",
            ["double"] = "64-bit floating point.",
            ["uint"] = "Unsigned integer.",
            ["ulong"] = "64-bit unsigned integer.",
            ["short"] = "16-bit signed integer.",
            ["ushort"] = "16-bit unsigned integer.",
            ["int128"] = "128-bit signed integer.",
            ["uint128"] = "128-bit unsigned integer.",
            ["decimal"] = "High-precision decimal.",
            ["byte"] = "8-bit unsigned integer.",
            ["bool"] = "Boolean (`true` / `false`).",
            ["string"] = "UTF-16 text.",
            ["char"] = "Single character.",
            ["void"] = "No value.",
            ["object"] = "Base object type.",
            ["any"] = "Dynamically-typed value.",
        };

        private static readonly Dictionary<string, string> s_keywordDocs = new(System.StringComparer.Ordinal)
        {
            ["fn"] = "Declares a function.",
            ["class"] = "Declares a class.",
            ["struct"] = "Declares a value-type struct.",
            ["record"] = "Declares a record type.",
            ["enum"] = "Declares an enumeration.",
            ["interface"] = "Declares an interface.",
            ["trait"] = "Declares a trait (mixin).",
            ["extend"] = "Opens an extension block on a type.",
            ["let"] = "Declares a move/affine binding.",
            ["var"] = "Declares a mutable variable.",
            ["const"] = "Declares a compile-time constant.",
            ["final"] = "Declares a single-assignment binding.",
            ["if"] = "Conditional branch.",
            ["elif"] = "Else-if branch.",
            ["else"] = "Else branch.",
            ["for"] = "Loop.",
            ["while"] = "Conditional loop.",
            ["do"] = "Do/while loop.",
            ["switch"] = "Multi-way branch.",
            ["match"] = "Pattern match expression.",
            ["ret"] = "Returns from a function.",
            ["yield"] = "Yields a value from a generator.",
            ["import"] = "Imports a module.",
            ["from"] = "Selective import source.",
            ["pub"] = "Marks a member public.",
            ["static"] = "Marks a member static.",
            ["abstract"] = "Marks a member abstract.",
            ["override"] = "Overrides a base member.",
            ["async"] = "Marks a function asynchronous.",
            ["await"] = "Awaits an async value.",
            ["self"] = "The current instance.",
            ["super"] = "The base type / constructor.",
            ["operator"] = "Declares an operator overload.",
            ["prop"] = "Declares a property.",
            ["event"] = "Declares an event.",
            ["delegate"] = "Declares a delegate type.",
            ["annotation"] = "Declares an annotation.",
            ["factory"] = "Declares a factory constructor.",
            ["namespace"] = "Declares a namespace.",
            ["throw"] = "Throws a value.",
            ["try"] = "Begins a try/catch block.",
        };
    }
}

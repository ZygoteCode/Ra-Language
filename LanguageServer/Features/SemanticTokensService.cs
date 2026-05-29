using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Text;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Computes semantic tokens from the lexer output. Classification is contextual
    /// but token-local (left/right neighbours only), which keeps it tolerant of
    /// broken code and cheap to recompute. Output uses the LSP delta encoding
    /// (5 ints per token, relative line/character) and never emits a multi-line
    /// token — multi-line literals are clamped to their first line.
    /// </summary>
    public sealed class SemanticTokensService : ISemanticTokensService
    {
        // Legend (indices are the wire contract — keep aligned with the arrays).
        private const int TypeNamespace = 0;
        private const int TypeType = 1;
        private const int TypeClass = 2;
        private const int TypeStruct = 3;
        private const int TypeInterface = 4;
        private const int TypeEnum = 5;
        private const int TypeEnumMember = 6;
        private const int TypeFunction = 7;
        private const int TypeMethod = 8;
        private const int TypeVariable = 9;
        private const int TypeParameter = 10;
        private const int TypeProperty = 11;
        private const int TypeKeyword = 12;
        private const int TypeString = 13;
        private const int TypeNumber = 14;
        private const int TypeOperator = 15;
        private const int TypeRegexp = 16;
        private const int TypeDecorator = 17;
        private const int TypeComment = 18;

        private const int ModDeclaration = 1 << 0;
        private const int ModReadonly = 1 << 1;
        private const int ModStatic = 1 << 2;
        private const int ModDeprecated = 1 << 3;
        private const int ModDefaultLibrary = 1 << 4;

        private static readonly string[] s_tokenTypes =
        {
            "namespace", "type", "class", "struct", "interface", "enum", "enumMember",
            "function", "method", "variable", "parameter", "property", "keyword",
            "string", "number", "operator", "regexp", "decorator", "comment",
        };

        private static readonly string[] s_tokenModifiers =
        {
            "declaration", "readonly", "static", "deprecated", "defaultLibrary",
        };

        private static readonly HashSet<string> s_builtinTypes = new(System.StringComparer.Ordinal)
        {
            "int", "number", "long", "float", "double", "uint", "ulong", "short", "ushort",
            "int128", "uint128", "decimal", "byte", "bool", "string", "char", "void",
            "object", "any",
        };

        public static SemanticTokensLegend CreateLegend() => new()
        {
            TokenTypes = s_tokenTypes,
            TokenModifiers = s_tokenModifiers,
        };

        public SemanticTokensLegend Legend => CreateLegend();

        public SemanticTokens Compute(RaDocument document, System.Collections.Generic.ISet<string> typeNames)
        {
            // Lexer output only — fast path, no full parse on every keystroke.
            var tokens = document.GetTokens();
            var lines = document.Document.Lines;

            var data = new List<int>(tokens.Count * 5);
            int prevLine = 0;
            int prevChar = 0;

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Type == TokenType.EOF) break;

                int classification = Classify(tokens, i, typeNames, out int modifiers);
                if (classification < 0) continue;

                int startOffset = token.PositionStart.Idx;
                int endOffset = token.PositionEnd.Idx;
                var (line, character) = lines.OffsetToPosition(startOffset);

                // Clamp to the start line's content so the token stays single-line.
                int contentEnd = LineContentEnd(lines, line);
                if (endOffset > contentEnd) endOffset = contentEnd;
                int length = endOffset - startOffset;
                if (length <= 0) continue;

                int deltaLine = line - prevLine;
                int deltaChar = deltaLine == 0 ? character - prevChar : character;

                data.Add(deltaLine);
                data.Add(deltaChar);
                data.Add(length);
                data.Add(classification);
                data.Add(modifiers);

                prevLine = line;
                prevChar = character;
            }

            return new SemanticTokens { Data = data.ToArray() };
        }

        private static int LineContentEnd(LineIndex lines, int line)
        {
            int end = lines.LineEndExclusive(line);
            string text = lines.Text;
            if (end > 0 && end <= text.Length && end - 1 < text.Length && text[end - 1] == '\n') end--;
            if (end > 0 && end - 1 < text.Length && text[end - 1] == '\r') end--;
            return end;
        }

        private static int Classify(IReadOnlyList<Token> tokens, int index, System.Collections.Generic.ISet<string> typeNames, out int modifiers)
        {
            modifiers = 0;
            var token = tokens[index];

            switch (token.Type)
            {
                case TokenType.STRING:
                case TokenType.STRING_TEXT:
                    return TypeString;
                case TokenType.INT:
                case TokenType.FLOAT:
                    return TypeNumber;
                case TokenType.REGEX_LITERAL:
                    return TypeRegexp;
                case TokenType.AT_SIGN:
                    return TypeDecorator;
                case TokenType.KEYWORD:
                    // true/false/null are language constants, not control keywords.
                    // Don't emit a semantic token so the TextMate `constant.language`
                    // scope (blue) wins instead of the keyword color (rosé).
                    if (token.Value is Keyword k && (k == Keyword.True || k == Keyword.False || k == Keyword.Null))
                        return -1;
                    return TypeKeyword;
                case TokenType.IDENTIFIER:
                    return ClassifyIdentifier(tokens, index, typeNames, out modifiers);
                default:
                    return -1; // operators / punctuation handled by TextMate grammar
            }
        }

        private static int ClassifyIdentifier(IReadOnlyList<Token> tokens, int index, System.Collections.Generic.ISet<string> typeNames, out int modifiers)
        {
            modifiers = 0;
            string name = TokenLocator.Text(tokens[index]);

            if (s_builtinTypes.Contains(name))
            {
                modifiers = ModDefaultLibrary;
                return TypeType;
            }

            int prev = PrevMeaningful(tokens, index - 1);
            int next = NextMeaningful(tokens, index + 1);

            // Declaration sites: the kind keyword sits immediately to the left.
            if (prev >= 0 && tokens[prev].Type == TokenType.KEYWORD && tokens[prev].Value is Keyword kw)
            {
                switch (kw)
                {
                    case Keyword.Fn:
                        modifiers = ModDeclaration;
                        return TypeFunction;
                    case Keyword.Class:
                        modifiers = ModDeclaration;
                        return TypeClass;
                    case Keyword.Struct:
                    case Keyword.Record:
                        modifiers = ModDeclaration;
                        return TypeStruct;
                    case Keyword.Enum:
                        modifiers = ModDeclaration;
                        return TypeEnum;
                    case Keyword.Interface:
                    case Keyword.Trait:
                        modifiers = ModDeclaration;
                        return TypeInterface;
                    case Keyword.Annotation:
                        modifiers = ModDeclaration;
                        return TypeDecorator;
                    case Keyword.Namespace:
                    case Keyword.Using:
                        return TypeNamespace;
                    case Keyword.Prop:
                        modifiers = ModDeclaration;
                        return TypeProperty;
                    case Keyword.Event:
                        modifiers = ModDeclaration;
                        return TypeProperty;
                }
            }

            if (prev >= 0 && tokens[prev].Type == TokenType.AT_SIGN)
            {
                return TypeDecorator;
            }

            if (prev >= 0 && tokens[prev].Type == TokenType.DOT)
            {
                // Member access: a call still reads as a method.
                if (next >= 0 && tokens[next].Type == TokenType.LPAREN) return TypeMethod;
                return TypeProperty;
            }

            // User-declared / imported type (class/struct/enum/interface/trait/record),
            // including type-as-constructor like `Foo(...)`. Checked before the call
            // heuristic so a type used as a constructor colors as a type, not a function.
            if (typeNames.Contains(name))
            {
                return TypeType;
            }

            if (next >= 0 && tokens[next].Type == TokenType.LPAREN)
            {
                return TypeFunction;
            }

            return TypeVariable;
        }

        private static int PrevMeaningful(IReadOnlyList<Token> tokens, int from)
        {
            for (int i = from; i >= 0; i--)
                if (tokens[i].Type != TokenType.NEWLINE) return i;
            return -1;
        }

        private static int NextMeaningful(IReadOnlyList<Token> tokens, int from)
        {
            for (int i = from; i < tokens.Count; i++)
                if (tokens[i].Type != TokenType.NEWLINE) return i;
            return -1;
        }
    }
}

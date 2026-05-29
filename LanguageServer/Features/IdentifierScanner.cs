using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Name-based identifier analysis over the token stream. This backs
    /// definition / references / highlight / rename. It is intentionally textual and
    /// scoped to a single document: it does not perform full binder-level scope or
    /// overload resolution, so two unrelated locals sharing a name are treated as the
    /// same symbol. This matches the behaviour of many shipping language servers at
    /// this maturity and is a deliberate, documented v1 boundary — the binder hook is
    /// the natural place to tighten it later.
    /// </summary>
    public static class IdentifierScanner
    {
        public static List<int> FindOccurrences(IReadOnlyList<Token> tokens, string name)
        {
            var result = new List<int>();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Type == TokenType.IDENTIFIER && TokenLocator.Text(tokens[i]) == name)
                {
                    result.Add(i);
                }
            }
            return result;
        }

        /// <summary>True when the identifier at <paramref name="index"/> introduces a binding.</summary>
        public static bool IsDeclaration(IReadOnlyList<Token> tokens, int index)
        {
            int prev = PrevMeaningful(tokens, index - 1);
            if (prev < 0) return false;
            if (tokens[prev].Type != TokenType.KEYWORD || tokens[prev].Value is not Keyword kw) return false;
            return s_declKeywords.Contains(kw);
        }

        /// <summary>True when the identifier is written to (declaration or assignment target).</summary>
        public static bool IsWrite(IReadOnlyList<Token> tokens, int index)
        {
            if (IsDeclaration(tokens, index)) return true;
            int next = NextMeaningful(tokens, index + 1);
            return next >= 0 && s_assignmentOps.Contains(tokens[next].Type);
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

        private static readonly HashSet<Keyword> s_declKeywords = new()
        {
            Keyword.Var, Keyword.Let, Keyword.Const, Keyword.Final, Keyword.Auto, Keyword.Mut,
            Keyword.Fn, Keyword.Class, Keyword.Struct, Keyword.Enum, Keyword.Interface,
            Keyword.Trait, Keyword.Record, Keyword.Prop, Keyword.Event, Keyword.Delegate,
            Keyword.Annotation, Keyword.Namespace,
        };

        private static readonly HashSet<TokenType> s_assignmentOps = new()
        {
            TokenType.EQ, TokenType.PLUS_EQ, TokenType.MINUS_EQ, TokenType.MUL_EQ, TokenType.DIV_EQ,
            TokenType.MODULO_EQ, TokenType.AND_EQ, TokenType.OR_EQ, TokenType.BITWISE_AND_EQ,
            TokenType.BITWISE_OR_EQ, TokenType.BITWISE_LEFT_SHIFT_EQ, TokenType.BITWISE_RIGHT_SHIFT_EQ,
            TokenType.BITWISE_LOGICAL_LEFT_SHIFT_EQ, TokenType.BITWISE_LOGICAL_RIGHT_SHIFT_EQ,
            TokenType.BITWISE_ROTATE_LEFT_EQ, TokenType.BITWISE_ROTATE_RIGHT_EQ, TokenType.POW_EQ,
            TokenType.NULL_COALESCE_EQ, TokenType.DOUBLE_PLUS, TokenType.DOUBLE_MINUS,
        };

        public static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            char c0 = name[0];
            if (!(char.IsLetter(c0) || c0 == '_')) return false;
            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            }
            return true;
        }
    }
}

using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.LanguageServer.Features
{
    public readonly struct BracketPair
    {
        public readonly int OpenIndex;
        public readonly int CloseIndex;
        public readonly TokenType OpenType;

        public BracketPair(int openIndex, int closeIndex, TokenType openType)
        {
            OpenIndex = openIndex;
            CloseIndex = closeIndex;
            OpenType = openType;
        }
    }

    /// <summary>
    /// Matches <c>()</c>, <c>[]</c> and <c>{}</c> pairs across the token stream with a
    /// simple stack. Unbalanced brackets (common while typing) are tolerated: an
    /// unmatched closer is dropped and unmatched openers are discarded at EOF.
    /// </summary>
    public static class BracketMatcher
    {
        public static List<BracketPair> Match(IReadOnlyList<Token> tokens)
        {
            var pairs = new List<BracketPair>();
            var stack = new Stack<int>();

            for (int i = 0; i < tokens.Count; i++)
            {
                switch (tokens[i].Type)
                {
                    case TokenType.LPAREN:
                    case TokenType.LSQUARE:
                    case TokenType.LBRACKET:
                        stack.Push(i);
                        break;

                    case TokenType.RPAREN:
                        Close(tokens, stack, pairs, i, TokenType.LPAREN);
                        break;
                    case TokenType.RSQUARE:
                        Close(tokens, stack, pairs, i, TokenType.LSQUARE);
                        break;
                    case TokenType.RBRACKET:
                        Close(tokens, stack, pairs, i, TokenType.LBRACKET);
                        break;
                }
            }

            return pairs;
        }

        private static void Close(
            IReadOnlyList<Token> tokens,
            Stack<int> stack,
            List<BracketPair> pairs,
            int closeIndex,
            TokenType expectedOpen)
        {
            if (stack.Count == 0) return;
            int openIndex = stack.Pop();
            // Mismatched kind (e.g. "( ]") — still pair them so nesting recovers.
            pairs.Add(new BracketPair(openIndex, closeIndex, tokens[openIndex].Type));
        }
    }
}

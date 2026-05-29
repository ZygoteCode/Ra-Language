using System.Collections.Generic;
using RaLanguage.Lexer.Tokens;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>
    /// Signature help. Walks left from the cursor (balancing brackets) to the
    /// enclosing call's <c>(</c>, identifies the callee, and reports the matching
    /// declared functions/methods with the active parameter highlighted.
    /// </summary>
    public sealed class SignatureHelpService : ISignatureHelpService
    {
        public SignatureHelp? Compute(RaDocument document, Position position)
        {
            var compilation = document.GetCompilation();
            var tokens = compilation.Tokens;
            int offset = document.Document.OffsetAt(position);

            int i = TokenLocator.FloorIndex(tokens, offset);
            if (i < 0) return null;

            if (!TryFindEnclosingCall(tokens, i, out int openIndex, out int activeParameter))
            {
                return null;
            }

            int callee = PrevMeaningful(tokens, openIndex - 1);
            if (callee < 0 || tokens[callee].Type != TokenType.IDENTIFIER)
            {
                return null;
            }

            string name = TokenLocator.Text(tokens[callee]);
            var index = SymbolIndex.Build(compilation.Ast);

            var signatures = new List<SignatureInformation>();
            foreach (var symbol in index.FindByName(name))
            {
                if (symbol.Kind is not (SymbolKind.Function or SymbolKind.Method or SymbolKind.Constructor))
                    continue;

                var parameters = symbol.Parameters ?? new List<string>();
                var paramInfos = new ParameterInformation[parameters.Count];
                for (int p = 0; p < parameters.Count; p++)
                {
                    paramInfos[p] = new ParameterInformation { Label = parameters[p] };
                }

                signatures.Add(new SignatureInformation
                {
                    Label = name + "(" + string.Join(", ", parameters) + ")",
                    Parameters = paramInfos,
                    ActiveParameter = activeParameter,
                });
            }

            if (signatures.Count == 0) return null;

            return new SignatureHelp
            {
                Signatures = signatures.ToArray(),
                ActiveSignature = 0,
                ActiveParameter = activeParameter,
            };
        }

        private static bool TryFindEnclosingCall(IReadOnlyList<Token> tokens, int from, out int openIndex, out int activeParameter)
        {
            openIndex = -1;
            activeParameter = 0;
            int depth = 0;

            for (int k = from; k >= 0; k--)
            {
                switch (tokens[k].Type)
                {
                    case TokenType.RPAREN:
                    case TokenType.RSQUARE:
                    case TokenType.RBRACKET:
                        depth++;
                        break;

                    case TokenType.LPAREN:
                        if (depth == 0) { openIndex = k; return true; }
                        depth--;
                        break;

                    case TokenType.LSQUARE:
                    case TokenType.LBRACKET:
                        if (depth == 0) return false; // cursor sits in a list/block, not a call
                        depth--;
                        break;

                    case TokenType.COMMA:
                        if (depth == 0) activeParameter++;
                        break;
                }
            }
            return false;
        }

        private static int PrevMeaningful(IReadOnlyList<Token> tokens, int from)
        {
            for (int i = from; i >= 0; i--)
                if (tokens[i].Type != TokenType.NEWLINE) return i;
            return -1;
        }
    }
}

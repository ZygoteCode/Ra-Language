using System.Collections.Generic;
using RaLanguage.LanguageServer.Protocol;
using RaLanguage.LanguageServer.Workspace;

namespace RaLanguage.LanguageServer.Features
{
    /// <summary>Hierarchical document outline from the structural <see cref="SymbolIndex"/>.</summary>
    public sealed class DocumentSymbolService : IDocumentSymbolService
    {
        public DocumentSymbol[] Compute(RaDocument document)
        {
            var index = SymbolIndex.Build(document.GetCompilation().Ast);
            var doc = document.Document;
            var result = new DocumentSymbol[index.TopLevel.Count];
            for (int i = 0; i < index.TopLevel.Count; i++)
            {
                result[i] = Convert(index.TopLevel[i], doc);
            }
            return result;
        }

        private static DocumentSymbol Convert(RaSymbol symbol, TextDocument doc)
        {
            // SelectionRange must be inside Range — clamp the name span into the node span.
            int selStart = Clamp(symbol.SelectionStart, symbol.RangeStart, symbol.RangeEnd);
            int selEnd = Clamp(symbol.SelectionEnd, selStart, symbol.RangeEnd);

            DocumentSymbol[]? children = null;
            if (symbol.Children.Count > 0)
            {
                children = new DocumentSymbol[symbol.Children.Count];
                for (int i = 0; i < symbol.Children.Count; i++)
                    children[i] = Convert(symbol.Children[i], doc);
            }

            return new DocumentSymbol
            {
                Name = string.IsNullOrEmpty(symbol.Name) ? "<anonymous>" : symbol.Name,
                Detail = symbol.Detail,
                Kind = symbol.Kind,
                Range = doc.RangeOf(symbol.RangeStart, symbol.RangeEnd),
                SelectionRange = doc.RangeOf(selStart, selEnd),
                Children = children,
            };
        }

        private static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);
    }
}

namespace RaLanguage.LanguageServer.Protocol
{
    // All LSP "enum" values travel on the wire as integers (LSP 3.17). They are
    // therefore modelled as plain C# enums with explicit numeric values and are
    // serialized with the System.Text.Json default (numeric) — deliberately NOT
    // with a string converter. String-valued LSP kinds (FoldingRangeKind,
    // MarkupKind, SemanticToken type/modifier names) are kept as string constants
    // instead, since they appear on the wire as strings.

    public enum TextDocumentSyncKind
    {
        None = 0,
        Full = 1,
        Incremental = 2,
    }

    public enum DiagnosticSeverity
    {
        Error = 1,
        Warning = 2,
        Information = 3,
        Hint = 4,
    }

    public enum CompletionItemKind
    {
        Text = 1,
        Method = 2,
        Function = 3,
        Constructor = 4,
        Field = 5,
        Variable = 6,
        Class = 7,
        Interface = 8,
        Module = 9,
        Property = 10,
        Unit = 11,
        Value = 12,
        Enum = 13,
        Keyword = 14,
        Snippet = 15,
        Color = 16,
        File = 17,
        Reference = 18,
        Folder = 19,
        EnumMember = 20,
        Constant = 21,
        Struct = 22,
        Event = 23,
        Operator = 24,
        TypeParameter = 25,
    }

    public enum CompletionTriggerKind
    {
        Invoked = 1,
        TriggerCharacter = 2,
        TriggerForIncompleteCompletions = 3,
    }

    public enum InsertTextFormat
    {
        PlainText = 1,
        Snippet = 2,
    }

    public enum SymbolKind
    {
        File = 1,
        Module = 2,
        Namespace = 3,
        Package = 4,
        Class = 5,
        Method = 6,
        Property = 7,
        Field = 8,
        Constructor = 9,
        Enum = 10,
        Interface = 11,
        Function = 12,
        Variable = 13,
        Constant = 14,
        String = 15,
        Number = 16,
        Boolean = 17,
        Array = 18,
        Object = 19,
        Key = 20,
        Null = 21,
        EnumMember = 22,
        Struct = 23,
        Event = 24,
        Operator = 25,
        TypeParameter = 26,
    }

    public enum DocumentHighlightKind
    {
        Text = 1,
        Read = 2,
        Write = 3,
    }

    public enum MessageType
    {
        Error = 1,
        Warning = 2,
        Info = 3,
        Log = 4,
    }

    /// <summary>String-valued LSP kinds. Used as literal wire strings.</summary>
    public static class MarkupKind
    {
        public const string PlainText = "plaintext";
        public const string Markdown = "markdown";
    }

    public static class FoldingRangeKind
    {
        public const string Comment = "comment";
        public const string Imports = "imports";
        public const string Region = "region";
    }
}

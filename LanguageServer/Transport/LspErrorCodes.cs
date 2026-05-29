namespace RaLanguage.LanguageServer.Transport
{
    /// <summary>JSON-RPC 2.0 + LSP reserved error codes.</summary>
    public static class LspErrorCodes
    {
        // JSON-RPC 2.0
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        // LSP-specific
        public const int ServerNotInitialized = -32002;
        public const int RequestFailed = -32803;
        public const int ServerCancelled = -32802;
        public const int ContentModified = -32801;
        public const int RequestCancelled = -32800;
    }
}

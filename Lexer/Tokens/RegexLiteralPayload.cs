namespace RaLanguage.Lexer.Tokens
{
    // Payload carried by a REGEX_LITERAL token. Keeps pattern text and flag
    // characters separate so the parser does not have to re-scan the source
    // span looking for the trailing flag suffix.
    public sealed class RegexLiteralPayload
    {
        public string Pattern { get; }
        public string Flags { get; }

        public RegexLiteralPayload(string pattern, string flags)
        {
            Pattern = pattern ?? string.Empty;
            Flags = flags ?? string.Empty;
        }

        public override string ToString() => Flags.Length == 0
            ? $"re\"{Pattern}\""
            : $"re\"{Pattern}\"{Flags}";
    }
}

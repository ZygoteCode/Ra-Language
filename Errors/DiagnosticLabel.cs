namespace RaLanguage.Errors
{
    public readonly struct DiagnosticLabel
    {
        public SourceSpan Span { get; }
        public string? Message { get; }
        public bool IsPrimary { get; }

        public DiagnosticLabel(SourceSpan span, string? message, bool isPrimary)
        {
            Span = span;
            Message = message;
            IsPrimary = isPrimary;
        }

        public static DiagnosticLabel Primary(SourceSpan span, string? message = null) =>
            new(span, message, true);

        public static DiagnosticLabel Secondary(SourceSpan span, string? message = null) =>
            new(span, message, false);
    }
}

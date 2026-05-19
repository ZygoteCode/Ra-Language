namespace RaLanguage.Errors
{
    public readonly struct TracebackFrame
    {
        public string DisplayName { get; }
        public SourceSpan Span { get; }

        public TracebackFrame(string displayName, SourceSpan span)
        {
            DisplayName = displayName ?? string.Empty;
            Span = span;
        }

        public override string ToString()
        {
            if (Span.IsValid)
                return $"at {DisplayName} ({Span})";
            return $"at {DisplayName}";
        }
    }
}

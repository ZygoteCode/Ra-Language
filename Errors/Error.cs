using RaLanguage.Lexer;

namespace RaLanguage.Errors
{
    /// <summary>
    /// Base error type. Backed by a <see cref="Diagnostic"/>. Legacy constructor
    /// signature (positionStart, positionEnd, errorName, details) is kept so the
    /// existing ~900 call sites continue to compile unchanged while gaining the
    /// richer diagnostic rendering, error chaining, and traceback support.
    /// </summary>
    public class Error
    {
        public Diagnostic Diagnostic { get; private set; }
        public Error? Cause { get; private set; }

        public Position PositionStart => Diagnostic.PrimarySpan.Start;
        public Position PositionEnd => Diagnostic.PrimarySpan.End;
        public string ErrorName => string.IsNullOrEmpty(Diagnostic.Category) ? Diagnostic.Title : Diagnostic.Category!;
        public string Details => Diagnostic.Message ?? Diagnostic.Title ?? string.Empty;

        public Error(Position positionStart, Position positionEnd, string errorName, string details)
        {
            Diagnostic = new Diagnostic(
                title: string.IsNullOrEmpty(details) ? (errorName ?? "error") : details,
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(positionStart, positionEnd),
                code: default,
                phase: DiagnosticPhase.Unknown,
                category: errorName,
                message: null);
        }

        protected Error(Diagnostic diagnostic)
        {
            Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        }

        /// <summary>Attach an inner cause. Renders below the primary diagnostic as a chain.</summary>
        public Error WithCause(Error? cause)
        {
            Cause = cause;
            if (cause != null) Diagnostic.WithCause(cause.Diagnostic);
            return this;
        }

        public Error WithCause(Diagnostic? cause)
        {
            if (cause != null) Diagnostic.WithCause(cause);
            return this;
        }

        protected void ReplaceDiagnostic(Diagnostic diagnostic)
        {
            Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
            if (Cause != null) Diagnostic.WithCause(Cause.Diagnostic);
        }

        public override string ToString() => DiagnosticRenderer.Render(Diagnostic);
    }
}

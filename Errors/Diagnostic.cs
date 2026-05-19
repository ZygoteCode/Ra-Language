using System.Collections.Generic;
using RaLanguage.Lexer;

namespace RaLanguage.Errors
{
    public enum DiagnosticSeverity
    {
        Error,
        Warning,
        Info,
        Note,
        Help
    }

    /// <summary>
    /// Structured compiler diagnostic. Carries severity, a stable code, a primary source
    /// span, optional secondary labels, help/notes, traceback frames (for runtime), and
    /// a chained inner cause. Rendering is delegated to <see cref="DiagnosticRenderer"/>.
    /// </summary>
    public sealed class Diagnostic
    {
        public DiagnosticSeverity Severity { get; }
        public DiagnosticCode Code { get; }
        public DiagnosticPhase Phase { get; }

        /// <summary>Short, high-signal headline describing the problem.</summary>
        public string Title { get; }

        /// <summary>Optional sub-category label rendered when no code is set.</summary>
        public string? Category { get; }

        /// <summary>Longer explanation rendered as a note when distinct from <see cref="Title"/>.</summary>
        public string? Message { get; }

        /// <summary>Actionable fix suggestion shown beside `help:`.</summary>
        public string? Help { get; }

        /// <summary>Inline label attached to the primary caret underline.</summary>
        public string? PrimaryLabel { get; }

        public SourceSpan PrimarySpan { get; }
        public IReadOnlyList<DiagnosticLabel>? SecondaryLabels { get; }
        public IReadOnlyList<string>? Notes { get; }
        public IReadOnlyList<TracebackFrame>? Traceback { get; }

        public Diagnostic? Cause { get; private set; }

        // ---- Legacy compatibility helpers (used by older call sites) ----
        public Position? PositionStart => PrimarySpan.IsValid ? PrimarySpan.Start : null;
        public Position? PositionEnd => PrimarySpan.IsValid ? PrimarySpan.End : null;
        public string? FileName => PrimarySpan.FileName;

        public Diagnostic(
            string title,
            DiagnosticSeverity severity,
            SourceSpan primarySpan,
            DiagnosticCode code = default,
            DiagnosticPhase phase = DiagnosticPhase.Unknown,
            string? category = null,
            string? message = null,
            string? help = null,
            string? primaryLabel = null,
            IReadOnlyList<DiagnosticLabel>? secondaryLabels = null,
            IReadOnlyList<string>? notes = null,
            IReadOnlyList<TracebackFrame>? traceback = null,
            Diagnostic? cause = null)
        {
            Title = title ?? string.Empty;
            Severity = severity;
            PrimarySpan = primarySpan;
            Code = code;
            Phase = phase;
            Category = category;
            Message = message;
            Help = help;
            PrimaryLabel = primaryLabel;
            SecondaryLabels = secondaryLabels;
            Notes = notes;
            Traceback = traceback;
            Cause = cause;
        }

        /// <summary>Legacy constructor: simple (message, severity, [posStart, posEnd]).</summary>
        public Diagnostic(string message, DiagnosticSeverity severity, Position? positionStart = null, Position? positionEnd = null)
            : this(
                title: message ?? string.Empty,
                severity: severity,
                primarySpan: BuildLegacySpan(positionStart, positionEnd),
                code: default,
                phase: DiagnosticPhase.Unknown)
        {
        }

        public Diagnostic WithCause(Diagnostic? cause)
        {
            Cause = cause;
            return this;
        }

        public Diagnostic WithCause(IEnumerable<Diagnostic>? causes)
        {
            if (causes == null) return this;
            Diagnostic? head = null;
            Diagnostic? tail = null;
            foreach (var c in causes)
            {
                if (c == null) continue;
                if (head == null) { head = c; tail = c; }
                else
                {
                    tail!.WithCause(c);
                    tail = c;
                }
            }
            if (head != null) WithCause(head);
            return this;
        }

        public override string ToString() => DiagnosticRenderer.Render(this);

        private static SourceSpan BuildLegacySpan(Position? start, Position? end)
        {
            if (start.HasValue && end.HasValue) return new SourceSpan(start.Value, end.Value);
            if (start.HasValue) return new SourceSpan(start.Value, start.Value);
            return default;
        }
    }
}

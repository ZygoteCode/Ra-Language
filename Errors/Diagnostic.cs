using RaLanguage.Lexer;

namespace RaLanguage.Errors
{
    public enum DiagnosticSeverity
    {
        Error,
        Warning,
        Info
    }

    public class Diagnostic
    {
        public string Message { get; }
        public DiagnosticSeverity Severity { get; }
        public Position? PositionStart { get; }
        public Position? PositionEnd { get; }
        public string? FileName { get; }

        public Diagnostic(string message, DiagnosticSeverity severity, Position? positionStart = null, Position? positionEnd = null)
        {
            Message = message;
            Severity = severity;
            PositionStart = positionStart;
            PositionEnd = positionEnd;
            FileName = positionStart?.Fn;
        }

        public override string ToString()
        {
            string severityStr = Severity switch
            {
                DiagnosticSeverity.Error => "Error",
                DiagnosticSeverity.Warning => "Warning",
                DiagnosticSeverity.Info => "Info",
                _ => "Unknown"
            };

            string positionStr = "";
            if (PositionStart != null)
            {
                positionStr = $" Line {PositionStart.Value.Ln + 1}, Col {PositionStart.Value.Col + 1}";
                if (FileName != null)
                {
                    positionStr = $" in {FileName}{positionStr}";
                }
            }

            return $"[{severityStr}]{positionStr}: {Message}";
        }
    }
}

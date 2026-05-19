using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class InvalidSyntaxError : Error
    {
        public InvalidSyntaxError(Position positionStart, Position positionEnd, string details = "")
            : base(BuildDiagnostic(positionStart, positionEnd, details, code: DiagnosticCode.ParserInvalidSyntax, help: null, label: null))
        {
        }

        public InvalidSyntaxError(Position positionStart, Position positionEnd, string details, DiagnosticCode code,
            string? help = null, string? primaryLabel = null)
            : base(BuildDiagnostic(positionStart, positionEnd, details, code, help, primaryLabel))
        {
        }

        private static Diagnostic BuildDiagnostic(Position s, Position e, string details, DiagnosticCode code, string? help, string? label)
        {
            return new Diagnostic(
                title: string.IsNullOrEmpty(details) ? "syntax error" : details,
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(s, e),
                code: code.IsEmpty ? DiagnosticCode.ParserInvalidSyntax : code,
                phase: DiagnosticPhase.Parsing,
                category: "Invalid Syntax",
                help: help,
                primaryLabel: label);
        }
    }
}

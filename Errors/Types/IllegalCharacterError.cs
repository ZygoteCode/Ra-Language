using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class IllegalCharacterError : Error
    {
        public IllegalCharacterError(Position positionStart, Position positionEnd, string details)
            : base(new Diagnostic(
                title: string.IsNullOrEmpty(details) ? "illegal character" : details,
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(positionStart, positionEnd),
                code: DiagnosticCode.LexerIllegalCharacter,
                phase: DiagnosticPhase.Lexing,
                category: "Illegal Character"))
        {
        }
    }
}

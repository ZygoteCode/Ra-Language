using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class ExpectedCharacterError : Error
    {
        public ExpectedCharacterError(Position positionStart, Position positionEnd, string details)
            : base(new Diagnostic(
                title: string.IsNullOrEmpty(details) ? "expected character" : details,
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(positionStart, positionEnd),
                code: DiagnosticCode.LexerExpectedCharacter,
                phase: DiagnosticPhase.Lexing,
                category: "Expected Character"))
        {
        }
    }
}

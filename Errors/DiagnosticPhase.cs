namespace RaLanguage.Errors
{
    public enum DiagnosticPhase
    {
        Unknown,
        Lexing,
        Parsing,
        StaticAnalysis,
        Runtime,
        Module,
        Internal
    }
}

namespace RaLanguage.Errors
{
    public readonly struct DiagnosticCode : IEquatable<DiagnosticCode>
    {
        public string Id { get; }
        public string Slug { get; }

        public DiagnosticCode(string id, string slug)
        {
            Id = id ?? string.Empty;
            Slug = slug ?? string.Empty;
        }

        public bool IsEmpty => string.IsNullOrEmpty(Id);

        public bool Equals(DiagnosticCode other) =>
            string.Equals(Id, other.Id, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is DiagnosticCode c && Equals(c);

        public override int GetHashCode() => Id is null ? 0 : Id.GetHashCode(StringComparison.Ordinal);

        public override string ToString() => Id;

        public static bool operator ==(DiagnosticCode a, DiagnosticCode b) => a.Equals(b);
        public static bool operator !=(DiagnosticCode a, DiagnosticCode b) => !a.Equals(b);

        public static readonly DiagnosticCode None = new(string.Empty, string.Empty);

        // ---- Lexer (RA01xx) ----
        public static readonly DiagnosticCode LexerIllegalCharacter      = new("RA0101", "illegal-character");
        public static readonly DiagnosticCode LexerUnterminatedString    = new("RA0102", "unterminated-string");
        public static readonly DiagnosticCode LexerUnterminatedInterp    = new("RA0103", "unterminated-interpolation");
        public static readonly DiagnosticCode LexerUnterminatedAsmBlock  = new("RA0104", "unterminated-asm-block");
        public static readonly DiagnosticCode LexerUnterminatedAsmInterp = new("RA0105", "unterminated-asm-interpolation");
        public static readonly DiagnosticCode LexerInvalidNumberLiteral  = new("RA0106", "invalid-number-literal");
        public static readonly DiagnosticCode LexerMissingExponentDigits = new("RA0107", "missing-exponent-digits");
        public static readonly DiagnosticCode LexerUnexpectedDollarSign  = new("RA0108", "unexpected-dollar-sign");
        public static readonly DiagnosticCode LexerExpectedCharacter     = new("RA0109", "expected-character");
        public static readonly DiagnosticCode LexerUnterminatedCsharpBlock  = new("RA0110", "unterminated-csharp-block");
        public static readonly DiagnosticCode LexerUnterminatedCsharpInterp = new("RA0111", "unterminated-csharp-interpolation");

        // ---- Parser (RA02xx) ----
        public static readonly DiagnosticCode ParserUnexpectedToken    = new("RA0201", "unexpected-token");
        public static readonly DiagnosticCode ParserExpectedToken      = new("RA0202", "expected-token");
        public static readonly DiagnosticCode ParserExpectedIdentifier = new("RA0203", "expected-identifier");
        public static readonly DiagnosticCode ParserExpectedExpression = new("RA0204", "expected-expression");
        public static readonly DiagnosticCode ParserExpectedKeyword    = new("RA0205", "expected-keyword");
        public static readonly DiagnosticCode ParserExpectedType       = new("RA0206", "expected-type-annotation");
        public static readonly DiagnosticCode ParserTrailingInput      = new("RA0207", "trailing-input");
        public static readonly DiagnosticCode ParserInvalidSyntax      = new("RA0208", "invalid-syntax");

        // ---- Static analysis (RA03xx) ----
        public static readonly DiagnosticCode StaticAnnotationViolation = new("RA0301", "annotation-violation");

        // ---- Runtime (RA04xx) ----
        public static readonly DiagnosticCode RuntimeGeneric         = new("RA0401", "runtime-error");
        public static readonly DiagnosticCode RuntimeUndefinedSymbol = new("RA0402", "undefined-symbol");
        public static readonly DiagnosticCode RuntimeMovedValue      = new("RA0403", "value-already-moved");
        public static readonly DiagnosticCode RuntimeTypeMismatch    = new("RA0404", "type-mismatch");
        public static readonly DiagnosticCode RuntimeNativeException = new("RA0405", "native-exception");
        public static readonly DiagnosticCode RuntimeAccessViolation = new("RA0406", "access-violation");
        public static readonly DiagnosticCode RuntimeBorrowViolation = new("RA0407", "borrow-violation");
        public static readonly DiagnosticCode RuntimeLifetimeViolation = new("RA0408", "lifetime-violation");
        public static readonly DiagnosticCode RuntimeImmutableBinding = new("RA0409", "immutable-binding");

        // ---- Modules / imports (RA05xx) ----
        public static readonly DiagnosticCode ModuleNotFound       = new("RA0501", "module-not-found");
        public static readonly DiagnosticCode ModuleCircularImport = new("RA0502", "circular-import");
        public static readonly DiagnosticCode ModuleLoadFailure    = new("RA0503", "module-load-failure");
        public static readonly DiagnosticCode ModuleImportConflict = new("RA0504", "import-conflict");
        public static readonly DiagnosticCode ModuleSymbolNotFound = new("RA0505", "symbol-not-found");
    }
}

using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class ModuleNotFoundError : Error
    {
        public ModuleNotFoundError(Position posStart, Position posEnd, string details)
            : base(new Diagnostic(
                title: details ?? "module not found",
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(posStart, posEnd),
                code: DiagnosticCode.ModuleNotFound,
                phase: DiagnosticPhase.Module,
                category: "Module Not Found"))
        {
        }
    }

    public class SymbolNotFoundError : Error
    {
        public SymbolNotFoundError(Position posStart, Position posEnd, string details)
            : base(new Diagnostic(
                title: details ?? "symbol not found",
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(posStart, posEnd),
                code: DiagnosticCode.ModuleSymbolNotFound,
                phase: DiagnosticPhase.Module,
                category: "Symbol Not Found"))
        {
        }
    }

    public class ImportConflictError : Error
    {
        public ImportConflictError(Position posStart, Position posEnd, string details)
            : base(new Diagnostic(
                title: details ?? "import conflict",
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(posStart, posEnd),
                code: DiagnosticCode.ModuleImportConflict,
                phase: DiagnosticPhase.Module,
                category: "Import Conflict"))
        {
        }
    }

    public class CircularImportError : Error
    {
        public CircularImportError(Position posStart, Position posEnd, string details)
            : base(new Diagnostic(
                title: details ?? "circular import",
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(posStart, posEnd),
                code: DiagnosticCode.ModuleCircularImport,
                phase: DiagnosticPhase.Module,
                category: "Circular Import",
                help: "remove or restructure the import cycle"))
        {
        }
    }

    public class ModuleLoadError : Error
    {
        public ModuleLoadError(Position posStart, Position posEnd, string details)
            : base(new Diagnostic(
                title: details ?? "module load failure",
                severity: DiagnosticSeverity.Error,
                primarySpan: new SourceSpan(posStart, posEnd),
                code: DiagnosticCode.ModuleLoadFailure,
                phase: DiagnosticPhase.Module,
                category: "Module Load Error"))
        {
        }
    }
}

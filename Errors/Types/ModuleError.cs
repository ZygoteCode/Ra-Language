using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class ModuleNotFoundError : Error
    {
        public ModuleNotFoundError(Position posStart, Position posEnd, string details)
            : base(posStart, posEnd, "ModuleNotFoundError", details)
        {
        }

        public override string ToString()
        {
            return $"ModuleNotFoundError: {Details}";
        }
    }

    public class SymbolNotFoundError : Error
    {
        public SymbolNotFoundError(Position posStart, Position posEnd, string details)
            : base(posStart, posEnd, "SymbolNotFoundError", details)
        {
        }

        public override string ToString()
        {
            return $"SymbolNotFoundError: {Details}";
        }
    }

    public class ImportConflictError : Error
    {
        public ImportConflictError(Position posStart, Position posEnd, string details)
            : base(posStart, posEnd, "ImportConflictError", details)
        {
        }

        public override string ToString()
        {
            return $"ImportConflictError: {Details}";
        }
    }
}

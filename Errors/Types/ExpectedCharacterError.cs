using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class ExpectedCharacterError : Error
    {
        public ExpectedCharacterError(Position positionStart, Position posEnd, string details)
            : base(positionStart, posEnd, "Expected Character", details) { }
    }
}
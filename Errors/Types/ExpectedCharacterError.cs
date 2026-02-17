using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class ExpectedCharacterError : Error
    {
        public ExpectedCharacterError(Position positionStart, Position positionEnd, string details)
            : base(positionStart, positionEnd, "Expected Character", details) { }
    }
}
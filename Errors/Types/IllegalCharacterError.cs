using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class IllegalCharacterError : Error
    {
        public IllegalCharacterError(Position positionStart, Position positionEnd, string details)
            : base(positionStart, positionEnd, "Illegal Character", details) { }
    }
}
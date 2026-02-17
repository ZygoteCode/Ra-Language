using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class IllegalCharacterError : Error
    {
        public IllegalCharacterError(Position positionStart, Position posEnd, string details)
            : base(positionStart, posEnd, "Illegal Character", details) { }
    }
}
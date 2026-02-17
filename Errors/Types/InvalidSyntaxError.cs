using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class InvalidSyntaxError : Error
    {
        public InvalidSyntaxError(Position positionStart, Position posEnd, string details = "")
            : base(positionStart, posEnd, "Invalid Syntax", details) { }
    }
}
using RaLanguage.Lexer;

namespace RaLanguage.Errors.Types
{
    public class InvalidSyntaxError : Error
    {
        public InvalidSyntaxError(Position positionStart, Position positionEnd, string details = "")
            : base(positionStart, positionEnd, "Invalid Syntax", details) { }
    }
}
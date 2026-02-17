using RaLanguage.Lexer;
using RaLanguage.Utilities;

namespace RaLanguage.Errors
{
    public class Error
    {
        public Position PosStart { get; }
        public Position PosEnd { get; }
        public string ErrorName { get; }
        public string Details { get; }

        public Error(Position positionStart, Position posEnd, string errorName, string details)
        {
            PosStart = positionStart;
            PosEnd = posEnd;
            ErrorName = errorName;
            Details = details;
        }

        public virtual string AsString()
        {
            var result = $"{ErrorName}: {Details}\n";
            result += $"File {PosStart.Fn}, line {PosStart.Ln + 1}";
            result += "\n\n" + Utils.StringWithArrows(PosStart.Ftxt, PosStart, PosEnd);
            return result;
        }
    }
}
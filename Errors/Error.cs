using RaLanguage.Lexer;
using RaLanguage.Utilities;

namespace RaLanguage.Errors
{
    public class Error
    {
        public Position PositionStart { get; }
        public Position PositionEnd { get; }
        public string ErrorName { get; }
        public string Details { get; }

        public Error(Position positionStart, Position positionEnd, string errorName, string details)
        {
            PositionStart = positionStart;
            PositionEnd = positionEnd;
            ErrorName = errorName;
            Details = details;
        }

        public virtual string AsString()
        {
            var result = $"{ErrorName}: {Details}\n";
            result += $"File {PositionStart.Fn}, line {PositionStart.Ln + 1}";
            result += "\n\n" + Utils.StringWithArrows(PositionStart.Ftxt, PositionStart, PositionEnd);
            return result;
        }

        public override string ToString()
        {
            return AsString();
        }
    }
}
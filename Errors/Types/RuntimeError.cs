using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer;
using RaLanguage.Utilities;

namespace RaLanguage.Errors.Types
{
    public class RuntimeError : Error
    {
        public Context Context { get; }

        public RuntimeError(Position positionStart, Position positionEnd, string details, Context context)
            : base(positionStart, positionEnd, "Runtime Error", details)
        {
            Context = context;
        }

        public override string ToString()
        {
            var result = GenerateTraceback();
            result += $"{ErrorName}: {Details}";
            result += "\n\n" + Utils.StringWithArrows(PositionStart.Ftxt, PositionStart, PositionEnd);
            return result;
        }

        private string GenerateTraceback()
        {
            var result = "";
            var pos = PositionStart;
            var ctx = Context;

            while (ctx != null)
            {
                result = $"  File {pos.Fn}, line {pos.Ln + 1}, in {ctx.DisplayName}\n" + result;
                pos = ctx.ParentEntryPos ?? pos;
                ctx = ctx.Parent;
            }

            return "Traceback (most recent call last):\n" + result;
        }
    }
}
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Functions
{
    public enum CaptureMode : byte
    {
        // [x] — snapshot at definition time. Body sees a frozen Aliased() copy.
        ByValue = 0,

        // [&x] — borrow the binding. Body sees a BorrowValue; the closure
        // observes mutations the outer scope makes and (for &mut) can write
        // back through the borrow.
        ByRef = 1,

        // [move x] — transfer ownership into the closure. The outer binding
        // is marked moved; subsequent uses of x outside the closure are a
        // borrow-checker error.
        ByMove = 2,
    }

    public sealed class CaptureSpec
    {
        public CaptureSpec(Token nameTok, CaptureMode mode, bool isMutableBorrow)
        {
            NameTok = nameTok;
            Mode = mode;
            IsMutableBorrow = isMutableBorrow;
            Name = nameTok.Value?.ToString() ?? string.Empty;
            PositionStart = nameTok.PositionStart;
            PositionEnd = nameTok.PositionEnd;
        }

        public Token NameTok { get; }
        public string Name { get; }
        public CaptureMode Mode { get; }
        public bool IsMutableBorrow { get; }
        public Position PositionStart { get; }
        public Position PositionEnd { get; }
    }
}

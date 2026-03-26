using System.Runtime.CompilerServices;

namespace RaLanguage.Lexer.Tokens
{
    public readonly struct Token
    {
        public TokenType Type { get; }
        public object? Value { get; }
        public Position PositionStart { get; }
        public Position PositionEnd { get; }

        public Token(TokenType type, object? value, Position positionStart, Position? positionEnd = null)
        {
            Type = type;
            Value = value;
            PositionStart = positionStart;
            
            if (positionEnd.HasValue)
            {
                PositionEnd = positionEnd.Value;
            }
            else
            {
                PositionEnd = positionStart.Advance();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(Keyword value)
        {
            return Type == TokenType.KEYWORD &&
                   Value is Keyword k &&
                   k == value;
        }

        public override string ToString()
        {
            return Value != null ? $"{Value}" : $"{Type}";
        }
    }
}
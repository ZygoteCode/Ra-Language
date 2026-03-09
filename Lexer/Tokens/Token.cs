namespace RaLanguage.Lexer.Tokens
{
    public class Token
    {
        public TokenType Type { get; }
        public object? Value { get; }
        public Position PositionStart { get; }
        public Position PositionEnd { get; }

        public Token(TokenType type, object? value, Position positionStart, Position? positionEnd = null)
        {
            Type = type;
            Value = value;

            PositionStart = positionStart.Copy();
            PositionEnd = positionEnd?.Copy() ?? positionStart.Copy();

            if (positionEnd == null)
            {
                PositionEnd.Advance();
            }
        }

        public bool Matches(Keyword value)
        {
            return Type == TokenType.KEYWORD && (Keyword)Value! == value;
        }

        public override string ToString()
        {
            return Value != null ? $"{Type}:{Value}" : $"{Type}";
        }
    }
}
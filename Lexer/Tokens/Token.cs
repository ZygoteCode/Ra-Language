namespace RaLanguage.Lexer.Tokens
{
    public class Token
    {
        public TokenType Type { get; }
        public object? Value { get; }
        public Position PosStart { get; }
        public Position PosEnd { get; }

        public Token(TokenType type, object? value, Position positionStart, Position? posEnd = null)
        {
            Type = type;
            Value = value;

            PosStart = positionStart.Copy();
            PosEnd = posEnd?.Copy() ?? positionStart.Copy();

            if (posEnd == null)
            {
                PosEnd.Advance();
            }
        }

        public bool Matches(TokenType type, string value)
        {
            return Type == type && Value?.ToString() == value;
        }

        public override string ToString()
        {
            return Value != null ? $"{Type}:{Value}" : $"{Type}";
        }
    }
}
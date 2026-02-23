namespace RaLanguage.Lexer.Tokens
{
    public enum TokenType
    {
        INT,
        FLOAT,
        STRING,

        IDENTIFIER,
        KEYWORD,

        PLUS,
        MINUS,
        MUL,
        DIV,
        POW,
        EQ,

        LPAREN,
        RPAREN,
        LSQUARE,
        RSQUARE,
        LBRACKET,
        RBRACKET,

        EE,
        NE,
        LT,
        GT,
        LTE,
        GTE,

        BITWISE_AND,
        BITWISE_OR,
        BITWISE_LEFT_SHIFT,
        BITWISE_RIGHT_SHIFT,
        MODULO,
        BITWISE_NOT,

        COMMA,
        ARROW,
        NEWLINE,
        COLON,
        EOF
    }
}
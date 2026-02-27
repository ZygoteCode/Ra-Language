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

        DOUBLE_PLUS,
        DOUBLE_MINUS,

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

        STRICT_EE,
        STRICT_NE,

        BITWISE_AND,
        BITWISE_OR,
        BITWISE_LEFT_SHIFT,
        BITWISE_RIGHT_SHIFT,
        MODULO,
        BITWISE_NOT,

        PLUS_EQ,
        MINUS_EQ,
        MUL_EQ,
        DIV_EQ,
        MODULO_EQ,
        AND_EQ,
        OR_EQ,
        BITWISE_AND_EQ,
        BITWISE_OR_EQ,
        BITWISE_LEFT_SHIFT_EQ,
        BITWISE_RIGHT_SHIFT_EQ,
        POW_EQ,

        COMMA,
        ARROW,
        NEWLINE,
        COLON,
        EOF
    }
}
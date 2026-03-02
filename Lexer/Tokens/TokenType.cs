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

        STRING_TEXT,
        INTERP_START,
        INTERP_END,

        EE,
        NE,
        LT,
        GT,
        LTE,
        GTE,

        DOT,
        DOUBLE_DOT,
        DOUBLE_DOT_EQ,
        SPREAD,

        COLON,
        DOUBLE_COLON,

        QUESTION_MARK,
        NULL_COALESCE,
        NULL_COALESCE_EQ,

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
        ARROW_RIGHT,
        NEWLINE,
        EOF
    }
}
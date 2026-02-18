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

        COMMA,
        ARROW,
        NEWLINE,
        EOF
    }
}
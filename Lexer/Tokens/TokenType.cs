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
        EOF,
        REF,
        AT_SIGN,
        ASM_TEXT,
        LIFETIME,

        // Pipeline forward operator `|>`. Lowest-precedence binary in the
        // expression grammar (sits below cast/ternary, above assignment).
        PIPE_FORWARD,

        // Format specifier carried back from inside a `${expr:spec}` block.
        // Emitted by the lexer right before the matching INTERP_END so the
        // parser can attach the spec to the interpolated expression without a
        // second textual scan.
        FORMAT_SPEC,

        // Regular-expression literal. Value is a `RegexLiteralPayload` carrying
        // the raw pattern text and the trailing flag characters (e.g. `i`, `m`,
        // `s`, `x`, `n`). The lexer captures the whole `re"..."flags` form as a
        // single token so the parser can build the dedicated AST node without
        // peeking over the flag suffix.
        REGEX_LITERAL
    }
}
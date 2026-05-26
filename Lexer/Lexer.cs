using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Utilities;
using System.Runtime.CompilerServices;
using System.Text;

namespace RaLanguage.Lexer
{
    public class Lexer
    {
        private readonly string _fn;
        private readonly string _text;
        private int _idx;
        private int _ln;
        private int _col;
        private bool _asmHeaderPending;
        private readonly DiagnosticBag _diagnostics = new DiagnosticBag();

        private static readonly bool[] s_isDigit = CreateDigitTable();
        private static readonly bool[] s_isLetterOrDigit = CreateLetterOrDigitTable();

        private static bool[] CreateDigitTable()
        {
            var table = new bool[128];
            for (char c = '0'; c <= '9'; c++)
                table[c] = true;
            return table;
        }

        private static bool[] CreateLetterOrDigitTable()
        {
            var table = new bool[128];
            for (char c = 'a'; c <= 'z'; c++)
                table[c] = true;
            for (char c = 'A'; c <= 'Z'; c++)
                table[c] = true;
            for (char c = '0'; c <= '9'; c++)
                table[c] = true;
            table['_'] = true;
            return table;
        }

        public Lexer(string fn, string text)
        {
            _fn = fn;
            _text = text;
            _idx = 0;
            _ln = 0;
            _col = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Advance(char currentChar)
        {
            _idx++;
            if (currentChar == '\n')
            {
                _ln++;
                _col = 0;
            }
            else
            {
                _col++;
            }
        }

        private void AdvanceMultiple(int count, ReadOnlySpan<char> span)
        {
            int end = Math.Min(_idx + count, span.Length);
            int col = _col;
            int ln = _ln;

            for (int i = _idx; i < end; i++)
            {
                char ch = span[i];
                if (ch == '\n')
                {
                    ln++;
                    col = 0;
                }
                else
                {
                    col++;
                }
            }

            _idx = end;
            _ln = ln;
            _col = col;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Position GetPos() => new Position(_idx, _ln, _col, _fn, _text);

        public (List<Token> Tokens, DiagnosticBag Diagnostics) MakeTokens()
        {
            var tokens = new List<Token>(Math.Max(256, _text.Length / 8));
            ReadOnlySpan<char> span = _text.AsSpan();

            while (_idx < span.Length)
            {
                char c = span[_idx];

                switch (c)
                {
                    case ' ':
                    case '\r':
                    case '\t':
                        Advance(c);
                        break;

                    case '#':
                        SkipComment(span);
                        break;

                    case ';':
                    case '\n':
                        tokens.Add(new Token(TokenType.NEWLINE, null, GetPos()));
                        Advance(c);
                        break;

                    case '"':
                    case '`':
                        ProcessString(span, c, false, tokens);
                        break;

                    case '\'':
                    {
                        // Disambiguate string literal vs lifetime annotation.
                        // 'ident NOT followed by another `'` → LIFETIME 'ident (e.g. 'a, 'static, '_).
                        // Anything that closes with `'` (including `'a'`, `'abc'`, `''`) → STRING.
                        // A `\` after the alpha-run means an escape inside a string, so
                        // route to ProcessString and let it scan for the closing `'`.
                        int peek = _idx + 1;
                        bool isLifetime = false;
                        int identEnd = peek;
                        if (peek < span.Length)
                        {
                            char first = span[peek];
                            if (first < 128 && s_isLetterOrDigit[first] && !s_isDigit[first])
                            {
                                int j = peek;
                                while (j < span.Length && span[j] < 128 && s_isLetterOrDigit[span[j]]) j++;
                                // Closing `'` immediately after the alpha-run → 'char-like' string.
                                // `\` after the alpha-run → escape sequence inside a string.
                                // Otherwise the `'ident` is a lifetime annotation.
                                if (j >= span.Length || (span[j] != '\'' && span[j] != '\\'))
                                {
                                    isLifetime = true;
                                    identEnd = j;
                                }
                            }
                        }

                        if (isLifetime)
                        {
                            var lifetimePosStart = GetPos();
                            Advance(span[_idx]); // consume opening apostrophe
                            string ident = span.Slice(peek, identEnd - peek).ToString();
                            while (_idx < identEnd) Advance(span[_idx]);
                            tokens.Add(new Token(TokenType.LIFETIME, ident, lifetimePosStart, GetPos()));
                        }
                        else
                        {
                            ProcessString(span, c, false, tokens);
                        }
                        break;
                    }

                    case '$':
                        if (_idx + 1 < span.Length && (span[_idx + 1] == '"' || span[_idx + 1] == '\'' || span[_idx + 1] == '`'))
                        {
                            char quoteChar = span[_idx + 1];
                            Advance(c);
                            ProcessString(span, quoteChar, true, tokens);
                        }
                        else
                        {
                            var posStart = GetPos();
                            Advance(c);
                            _diagnostics.AddError(
                                title: "unexpected '$' outside of an interpolated string literal",
                                code: DiagnosticCode.LexerUnexpectedDollarSign,
                                positionStart: posStart,
                                positionEnd: GetPos(),
                                phase: DiagnosticPhase.Lexing,
                                help: "use $\"...\" / $'...' / $`...` to enable ${expression} interpolation");
                        }
                        break;

                    case '+': ProcessPlus(span, tokens); break;
                    case '-': ProcessMinus(span, tokens); break;
                    case '*': ProcessMul(span, tokens); break;
                    case '/': ProcessDiv(span, tokens); break;
                    case '%': ProcessModulo(span, tokens); break;
                    case '^': ProcessPow(span, tokens); break;
                    case '=': ProcessEquals(span, tokens); break;
                    case '!': ProcessNot(span, tokens); break;
                    case '<': ProcessLessThan(span, tokens); break;
                    case '>': ProcessGreaterThan(span, tokens); break;
                    case '&': ProcessAnd(span, tokens); break;
                    case '|': ProcessOr(span, tokens); break;
                    case ':': ProcessColon(span, tokens); break;
                    case '.': ProcessDot(span, tokens); break;
                    case '?': ProcessQuestionMark(span, tokens); break;

                    case '(': tokens.Add(new Token(TokenType.LPAREN, null, GetPos())); Advance(c); break;
                    case ')': tokens.Add(new Token(TokenType.RPAREN, null, GetPos())); Advance(c); break;
                    case '[': tokens.Add(new Token(TokenType.LSQUARE, null, GetPos())); Advance(c); break;
                    case ']': tokens.Add(new Token(TokenType.RSQUARE, null, GetPos())); Advance(c); break;
                    case '{':
                        if (_asmHeaderPending)
                        {
                            var lbracePos = GetPos();
                            Advance(c);
                            tokens.Add(new Token(TokenType.LBRACKET, null, lbracePos, GetPos()));
                            _asmHeaderPending = false;
                            ProcessAsmBlock(span, tokens);
                        }
                        else
                        {
                            tokens.Add(new Token(TokenType.LBRACKET, null, GetPos()));
                            Advance(c);
                        }
                        break;
                    case '}': tokens.Add(new Token(TokenType.RBRACKET, null, GetPos())); Advance(c); break;
                    case '~': tokens.Add(new Token(TokenType.BITWISE_NOT, null, GetPos())); Advance(c); break;
                    case ',': tokens.Add(new Token(TokenType.COMMA, null, GetPos())); Advance(c); break;
                    case '@': tokens.Add(new Token(TokenType.AT_SIGN, null, GetPos())); Advance(c); break;

                    default:
                        if (c < 128 && s_isDigit[c])
                        {
                            ProcessNumber(span, tokens);
                        }
                        else if (c == 'r' && _idx + 2 < span.Length && span[_idx + 1] == 'e' && span[_idx + 2] == '"')
                        {
                            // Regex literal: `re"pattern"flags`. Treated as a
                            // first-class lexeme so the parser can build a
                            // dedicated AST node without backtracking. The
                            // `r` / `e` characters must not be preceded by an
                            // identifier (we never reach `default` mid-ident
                            // because ProcessIdentifier consumes greedily).
                            ProcessRegexLiteral(span, tokens);
                        }
                        else if (c < 128 && s_isLetterOrDigit[c])
                        {
                            ProcessIdentifier(span, tokens);
                        }
                        else
                        {
                            var posStart = GetPos();
                            Advance(c);
                            string repr = c >= 0x20 && c != 0x7F
                                ? "'" + c + "'"
                                : "\\u" + ((int)c).ToString("X4");
                            _diagnostics.AddError(
                                title: $"illegal character {repr} in source",
                                code: DiagnosticCode.LexerIllegalCharacter,
                                positionStart: posStart,
                                positionEnd: GetPos(),
                                phase: DiagnosticPhase.Lexing,
                                primaryLabel: "not allowed here",
                                help: "remove the character or quote it inside a string literal");
                        }
                        break;
                }
            }

            tokens.Add(new Token(TokenType.EOF, null, GetPos()));
            return (tokens, _diagnostics);
        }

        private void SkipComment(ReadOnlySpan<char> span)
        {
            int remaining = span.Length - _idx;
            int newLinePos = span.Slice(_idx).IndexOf('\n');
            
            if (newLinePos == -1)
            {
                _idx = span.Length;
                _col += remaining;
            }
            else
            {
                _idx += newLinePos + 1;
                _ln++;
                _col = 0;
            }
        }

        #region Operators Processing

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessPlus(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.PLUS_EQ, null, posStart, GetPos())); return; }
                if (span[_idx] == '+') { Advance(span[_idx]); tokens.Add(new Token(TokenType.DOUBLE_PLUS, null, posStart, GetPos())); return; }
            }
            tokens.Add(new Token(TokenType.PLUS, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessMinus(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '-' && _idx + 1 < span.Length && span[_idx + 1] == '-')
                {
                    AdvanceMultiple(2, span);
                    SkipComment(span);
                    return;
                }
                if (span[_idx] == '-') { Advance(span[_idx]); tokens.Add(new Token(TokenType.DOUBLE_MINUS, null, posStart, GetPos())); return; }
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.MINUS_EQ, null, posStart, GetPos())); return; }
                if (span[_idx] == '>') { Advance(span[_idx]); tokens.Add(new Token(TokenType.ARROW_RIGHT, null, posStart, GetPos())); return; }
            }
            tokens.Add(new Token(TokenType.MINUS, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessMul(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '*')
                {
                    Advance(span[_idx]);
                    if (_idx < span.Length && span[_idx] == '=')
                    {
                        Advance(span[_idx]);
                        tokens.Add(new Token(TokenType.POW_EQ, null, posStart, GetPos()));
                        return;
                    }
                    tokens.Add(new Token(TokenType.POW, null, posStart, GetPos()));
                    return;
                }
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.MUL_EQ, null, posStart, GetPos())); return; }
            }
            tokens.Add(new Token(TokenType.MUL, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessDiv(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '/')
                {
                    Advance(span[_idx]);
                    SkipComment(span);
                    return;
                }
                if (span[_idx] == '*')
                {
                    Advance(span[_idx]);
                    var remaining = span.Slice(_idx);
                    int endComment = remaining.IndexOf("*/");
                    if (endComment == -1)
                        AdvanceMultiple(remaining.Length, span);
                    else
                        AdvanceMultiple(endComment + 2, span);
                    return;
                }
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.DIV_EQ, null, posStart, GetPos())); return; }
            }
            tokens.Add(new Token(TokenType.DIV, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessModulo(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.MODULO_EQ, null, posStart, GetPos())); return; }
            tokens.Add(new Token(TokenType.MODULO, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessPow(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.POW_EQ, null, posStart, GetPos())); return; }
            tokens.Add(new Token(TokenType.POW, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessEquals(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '=')
                {
                    Advance(span[_idx]);
                    if (_idx < span.Length && span[_idx] == '=')
                    {
                        Advance(span[_idx]);
                        tokens.Add(new Token(TokenType.STRICT_EE, null, posStart, GetPos()));
                        return;
                    }
                    tokens.Add(new Token(TokenType.EE, null, posStart, GetPos()));
                    return;
                }
                if (span[_idx] == '>') { Advance(span[_idx]); tokens.Add(new Token(TokenType.ARROW, null, posStart, GetPos())); return; }
            }
            tokens.Add(new Token(TokenType.EQ, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessNot(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == '=')
            {
                Advance(span[_idx]);
                if (_idx < span.Length && span[_idx] == '=')
                {
                    Advance(span[_idx]);
                    tokens.Add(new Token(TokenType.STRICT_NE, null, posStart, GetPos()));
                    return;
                }
                tokens.Add(new Token(TokenType.NE, null, posStart, GetPos()));
                return;
            }
            tokens.Add(new Token(TokenType.KEYWORD, Keyword.Not, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessLessThan(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.LTE, null, posStart, GetPos())); return; }
                if (span[_idx] == '<')
                {
                    Advance(span[_idx]);
                    if (_idx < span.Length && span[_idx] == '=')
                    {
                        Advance(span[_idx]);
                        tokens.Add(new Token(TokenType.BITWISE_LEFT_SHIFT_EQ, null, posStart, GetPos()));
                        return;
                    }
                    tokens.Add(new Token(TokenType.BITWISE_LEFT_SHIFT, null, posStart, GetPos()));
                    return;
                }
                if (span[_idx] == '!')
                {
                    Advance(span[_idx]);
                    if (_idx >= span.Length || span[_idx] != '-')
                    {
                        _diagnostics.AddError(
                            title: "expected '-' to open a CDATA block",
                            code: DiagnosticCode.LexerExpectedCharacter,
                            positionStart: posStart,
                            positionEnd: GetPos(),
                            phase: DiagnosticPhase.Lexing,
                            primaryLabel: "expected '-' after '<!'",
                            help: "CDATA blocks start with '<!--' and end with '-->'");
                        return;
                    }
                    Advance(span[_idx]);
                    if (_idx >= span.Length || span[_idx] != '-')
                    {
                        _diagnostics.AddError(
                            title: "expected '-' to open a CDATA block",
                            code: DiagnosticCode.LexerExpectedCharacter,
                            positionStart: posStart,
                            positionEnd: GetPos(),
                            phase: DiagnosticPhase.Lexing,
                            primaryLabel: "expected a second '-' after '<!-'",
                            help: "CDATA blocks start with '<!--' and end with '-->'");
                        return;
                    }
                    Advance(span[_idx]);

                    int cdataEnd = span.Slice(_idx).IndexOf("-->");
                    if (cdataEnd == -1) AdvanceMultiple(span.Length - _idx, span);
                    else AdvanceMultiple(cdataEnd + 3, span);

                    return;
                }
            }
            tokens.Add(new Token(TokenType.LT, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessGreaterThan(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.GTE, null, posStart, GetPos())); return; }
                if (span[_idx] == '>')
                {
                    Advance(span[_idx]);
                    if (_idx < span.Length && span[_idx] == '=')
                    {
                        Advance(span[_idx]);
                        tokens.Add(new Token(TokenType.BITWISE_RIGHT_SHIFT_EQ, null, posStart, GetPos()));
                        return;
                    }
                    tokens.Add(new Token(TokenType.BITWISE_RIGHT_SHIFT, null, posStart, GetPos()));
                    return;
                }
            }
            tokens.Add(new Token(TokenType.GT, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessAnd(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '&')
                {
                    Advance(span[_idx]);
                    if (_idx < span.Length && span[_idx] == '=')
                    {
                        Advance(span[_idx]);
                        tokens.Add(new Token(TokenType.AND_EQ, null, posStart, GetPos()));
                        return;
                    }
                    tokens.Add(new Token(TokenType.KEYWORD, Keyword.And, posStart, GetPos()));
                    return;
                }
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.BITWISE_AND_EQ, null, posStart, GetPos())); return; }
            }
            tokens.Add(new Token(TokenType.BITWISE_AND, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessOr(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '|')
                {
                    Advance(span[_idx]);
                    if (_idx < span.Length && span[_idx] == '=')
                    {
                        Advance(span[_idx]);
                        tokens.Add(new Token(TokenType.OR_EQ, null, posStart, GetPos()));
                        return;
                    }
                    tokens.Add(new Token(TokenType.KEYWORD, Keyword.Or, posStart, GetPos()));
                    return;
                }
                if (span[_idx] == '>')
                {
                    // `|>` pipeline forward operator. Disambiguates against `||`
                    // (handled above) and `|=` (handled below) because we have
                    // already consumed the single `|`.
                    Advance(span[_idx]);
                    tokens.Add(new Token(TokenType.PIPE_FORWARD, null, posStart, GetPos()));
                    return;
                }
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.BITWISE_OR_EQ, null, posStart, GetPos())); return; }
            }
            tokens.Add(new Token(TokenType.BITWISE_OR, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessColon(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == ':') { Advance(span[_idx]); tokens.Add(new Token(TokenType.KEYWORD, Keyword.As, posStart, GetPos())); return; }
            tokens.Add(new Token(TokenType.COLON, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessDot(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == '.')
            {
                Advance(span[_idx]);
                if (_idx < span.Length)
                {
                    if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.DOUBLE_DOT_EQ, null, posStart, GetPos())); return; }
                    if (span[_idx] == '.') { Advance(span[_idx]); tokens.Add(new Token(TokenType.SPREAD, null, posStart, GetPos())); return; }
                }
                tokens.Add(new Token(TokenType.DOUBLE_DOT, null, posStart, GetPos()));
                return;
            }
            tokens.Add(new Token(TokenType.DOT, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessQuestionMark(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == '?')
            {
                Advance(span[_idx]);
                if (_idx < span.Length && span[_idx] == '=')
                {
                    Advance(span[_idx]);
                    tokens.Add(new Token(TokenType.NULL_COALESCE_EQ, null, posStart, GetPos()));
                    return;
                }
                tokens.Add(new Token(TokenType.NULL_COALESCE, null, posStart, GetPos()));
                return;
            }
            tokens.Add(new Token(TokenType.QUESTION_MARK, null, posStart, GetPos()));
        }

        #endregion

        #region Complex Tokens (Numbers, Strings, Identifiers)

        private static string BuildStringNoUnderscores(ReadOnlySpan<char> span)
        {
            int idx = span.IndexOf('_');
            if (idx == -1) return span.ToString();
            
            var sb = new StringBuilder(span.Length - 1);
            for (int i = 0; i < span.Length; i++)
            {
                char ch = span[i];
                if (ch != '_') sb.Append(ch);
            }
            return sb.ToString();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryReadNumberSuffix(ReadOnlySpan<char> span, ref string? suffix, ref bool isFloat)
        {
            int remaining = span.Length - _idx;
            if (remaining <= 0)
                return false;

            if (remaining >= 2)
            {
                char c0 = span[_idx];
                char c1 = span[_idx + 1];

                if ((c0 | 0x20) == 'u')
                {
                    char c1Lower = (char)(c1 | 0x20);
                    if (c1Lower == 'i' || c1Lower == 'l' || c1Lower == 's')
                    {
                        suffix = new string(new[] { c0, c1 });
                        AdvanceMultiple(2, span);
                        return true;
                    }
                }
            }

            char c = span[_idx];
            char cLower = (char)(c | 0x20);

            switch (cLower)
            {
                case 'i':
                    suffix = "i";
                    Advance(c);
                    return true;
                case 'l':
                    suffix = "l";
                    Advance(c);
                    return true;
                case 'd':
                    suffix = "d";
                    isFloat = true;
                    Advance(c);
                    return true;
                case 'f':
                    suffix = "f";
                    isFloat = true;
                    Advance(c);
                    return true;
                case 'm':
                    suffix = "m";
                    isFloat = true;
                    Advance(c);
                    return true;
                case 's':
                    suffix = "s";
                    Advance(c);
                    return true;
            }

            return false;
        }

        private void ProcessNumber(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            int startIdx = _idx;
            int dotCount = 0;
            bool isFloat = false;
            string? suffix = null;

            if (span[_idx] == '0' && _idx + 1 < span.Length)
            {
                char p = span[_idx + 1];
                if ((p | 0x20) == 'x' || (p | 0x20) == 'b' || (p | 0x20) == 'o')
                {
                    AdvanceMultiple(2, span);
                    bool anyDigit = false;
                    bool isHex = (p | 0x20) == 'x';
                    bool isBinary = (p | 0x20) == 'b';

                    while (_idx < span.Length)
                    {
                        char c = span[_idx];
                        if (c == '_') { Advance(c); continue; }

                        bool isValid = isHex ? Utils.IsHexDigit(c) :
                                       isBinary ? Utils.IsBinaryDigit(c) :
                                                Utils.IsOctalDigit(c);

                        if (!isValid) break;

                        anyDigit = true;
                        Advance(c);
                    }

                    if (!anyDigit)
                    {
                        string family = (p | 0x20) == 'x' ? "hexadecimal"
                                      : (p | 0x20) == 'b' ? "binary"
                                      : "octal";
                        _diagnostics.AddError(
                            title: $"{family} literal has no digits",
                            code: DiagnosticCode.LexerInvalidNumberLiteral,
                            positionStart: posStart,
                            positionEnd: GetPos(),
                            phase: DiagnosticPhase.Lexing,
                            primaryLabel: $"expected at least one {family} digit",
                            help: $"write a {family} digit after the '0{p}' prefix, e.g. 0{p}1");
                    }

                    TryReadNumberSuffix(span, ref suffix, ref isFloat);

                    string numValStr = BuildStringNoUnderscores(_text.Substring(startIdx, _idx - startIdx));
                    tokens.Add(new Token(isFloat ? TokenType.FLOAT : TokenType.INT, numValStr, posStart, GetPos()));
                    return;
                }
            }

            while (_idx < span.Length)
            {
                char c = span[_idx];

                if (c < 128 && s_isDigit[c])
                {
                    Advance(c);
                }
                else if (c == '_')
                {
                    Advance(c);
                }
                else if (c == '.')
                {
                    if (_idx + 1 < span.Length && span[_idx + 1] == '.')
                        break;

                    if (dotCount == 1)
                        break;

                    dotCount++;
                    isFloat = true;
                    Advance(c);
                }
                else if ((c | 0x20) == 'e')
                {
                    isFloat = true;
                    Advance(c);

                    if (_idx < span.Length && (span[_idx] == '+' || span[_idx] == '-'))
                        Advance(span[_idx]);

                    if (_idx >= span.Length || !s_isDigit[span[_idx]])
                    {
                        _diagnostics.AddError(
                            title: "exponent has no digits",
                            code: DiagnosticCode.LexerMissingExponentDigits,
                            positionStart: posStart,
                            positionEnd: GetPos(),
                            phase: DiagnosticPhase.Lexing,
                            primaryLabel: "expected one or more digits after 'e' / 'E'",
                            help: "write the exponent digits, e.g. 1.0e10 or 2E+3");
                        break;
                    }

                    while (_idx < span.Length && s_isDigit[span[_idx]])
                        Advance(span[_idx]);

                    break;
                }
                else
                {
                    break;
                }
            }

            TryReadNumberSuffix(span, ref suffix, ref isFloat);
            string finalNum = BuildStringNoUnderscores(_text.Substring(startIdx, _idx - startIdx));
            tokens.Add(new Token(isFloat ? TokenType.FLOAT : TokenType.INT, finalNum, posStart, GetPos()));
        }

        private void ProcessString(ReadOnlySpan<char> span, char stringChar, bool allowInterpolation, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);

            var segStartPos = GetPos();
            StringBuilder? sb = null;
            int segStartIdx = _idx;

            while (_idx < span.Length)
            {
                char c = span[_idx];

                if (c == stringChar) break;

                if (c == '\\')
                {
                    sb ??= new StringBuilder(span.Length - segStartIdx);
                    sb.Append(span.Slice(segStartIdx, _idx - segStartIdx));

                    Advance(c);
                    if (_idx < span.Length)
                    {
                        char esc = span[_idx];
                        switch (esc)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            case '\'': sb.Append('\''); break;
                            case '`': sb.Append('`'); break;
                            default: sb.Append(esc); break;
                        }
                        Advance(esc);
                    }
                    segStartIdx = _idx;
                    continue;
                }

                if (allowInterpolation && c == '$' && _idx + 1 < span.Length && span[_idx + 1] == '{')
                {
                    string textSeg = sb != null
                        ? sb.Append(span.Slice(segStartIdx, _idx - segStartIdx)).ToString()
                        : span.Slice(segStartIdx, _idx - segStartIdx).ToString();

                    tokens.Add(new Token(TokenType.STRING_TEXT, textSeg, segStartPos, GetPos()));
                    sb?.Clear();

                    var interpStartPos = GetPos();
                    AdvanceMultiple(2, span);

                    tokens.Add(new Token(TokenType.INTERP_START, null, interpStartPos, GetPos()));

                    int innerStartIdx = _idx;
                    int braceCount = 1;

                    while (_idx < span.Length && braceCount > 0)
                    {
                        if (span[_idx] == '{') braceCount++;
                        else if (span[_idx] == '}') braceCount--;
                        Advance(span[_idx]);
                    }

                    if (braceCount != 0)
                    {
                        _diagnostics.AddError(
                            title: "unterminated interpolation in string literal",
                            code: DiagnosticCode.LexerUnterminatedInterp,
                            positionStart: interpStartPos,
                            positionEnd: GetPos(),
                            phase: DiagnosticPhase.Lexing,
                            primaryLabel: "interpolation started here is never closed",
                            help: "add a matching '}' to close the ${...} expression");
                    }

                    string innerText = _text.Substring(innerStartIdx, _idx - 1 - innerStartIdx);

                    // Split off an optional `:spec` suffix before sub-lexing the
                    // expression. The format spec uses a tightly constrained
                    // grammar (see TrySplitFormatSpec) so we can disambiguate it
                    // against ternaries and named arguments without ever
                    // consulting the parser.
                    string exprText = innerText;
                    string? formatSpec = null;
                    int specSplit = TrySplitFormatSpec(innerText);
                    if (specSplit >= 0)
                    {
                        exprText = innerText.Substring(0, specSplit);
                        formatSpec = innerText.Substring(specSplit + 1);
                    }

                    var innerLexer = new Lexer(_fn, exprText);
                    var (innerTokens, innerDiagnostics) = innerLexer.MakeTokens();
                    _diagnostics.AddRange(innerDiagnostics);

                    foreach (var t in innerTokens)
                    {
                        if (t.Type != TokenType.EOF) tokens.Add(t);
                    }

                    var interpEndPos = GetPos();

                    if (formatSpec != null)
                    {
                        tokens.Add(new Token(TokenType.FORMAT_SPEC, formatSpec, interpStartPos, interpEndPos));
                    }

                    tokens.Add(new Token(TokenType.INTERP_END, null, interpEndPos, interpEndPos));

                    segStartIdx = _idx;
                    segStartPos = GetPos();
                    continue;
                }

                Advance(c);
            }

            if (_idx >= span.Length || span[_idx] != stringChar)
            {
                _diagnostics.AddError(
                    title: "unterminated string literal",
                    code: DiagnosticCode.LexerUnterminatedString,
                    positionStart: posStart,
                    positionEnd: posStart.Advance(stringChar),
                    phase: DiagnosticPhase.Lexing,
                    primaryLabel: $"opening {stringChar} has no matching {stringChar}",
                    help: $"add the matching {stringChar} on this line, or escape line breaks with \\n");
                return;
            }

            string finalTextSeg = sb != null
                ? sb.Append(span.Slice(segStartIdx, _idx - segStartIdx)).ToString()
                : span.Slice(segStartIdx, _idx - segStartIdx).ToString();

            tokens.Add(new Token(TokenType.STRING_TEXT, finalTextSeg, segStartPos, GetPos()));
            Advance(span[_idx]);
        }

        // Regex literal `re"pattern"flags`. Backslashes are preserved verbatim
        // inside the pattern (regex engines own their own escape grammar), so
        // only the closing quote needs an escape — written as `\"` inside the
        // pattern body. The flag suffix collects ASCII letters until the first
        // non-letter character; validity is checked by the parser / runtime.
        private void ProcessRegexLiteral(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            // consume 're"' prefix
            AdvanceMultiple(3, span);

            int patternStart = _idx;
            StringBuilder? sb = null;
            int segStart = _idx;

            while (_idx < span.Length)
            {
                char c = span[_idx];
                if (c == '\\' && _idx + 1 < span.Length && span[_idx + 1] == '"')
                {
                    sb ??= new StringBuilder(span.Length - patternStart);
                    sb.Append(span.Slice(segStart, _idx - segStart));
                    sb.Append('"');
                    AdvanceMultiple(2, span);
                    segStart = _idx;
                    continue;
                }
                if (c == '"') break;
                if (c == '\n')
                {
                    _diagnostics.AddError(
                        title: "unterminated regex literal",
                        code: DiagnosticCode.LexerUnterminatedRegex,
                        positionStart: posStart,
                        positionEnd: GetPos(),
                        phase: DiagnosticPhase.Lexing,
                        primaryLabel: "newline before closing '\"'",
                        help: "regex literals must close on the same line; escape the quote as \\\" inside the pattern");
                    return;
                }
                Advance(c);
            }

            if (_idx >= span.Length || span[_idx] != '"')
            {
                _diagnostics.AddError(
                    title: "unterminated regex literal",
                    code: DiagnosticCode.LexerUnterminatedRegex,
                    positionStart: posStart,
                    positionEnd: GetPos(),
                    phase: DiagnosticPhase.Lexing,
                    primaryLabel: "opening re\" has no matching '\"'",
                    help: "add the matching '\"' on this line");
                return;
            }

            string pattern = sb != null
                ? sb.Append(span.Slice(segStart, _idx - segStart)).ToString()
                : span.Slice(segStart, _idx - segStart).ToString();

            // consume closing '"'
            Advance(span[_idx]);

            // Trailing flag characters: ASCII letters that the regex engine
            // recognises. Anything else terminates the literal.
            int flagStart = _idx;
            while (_idx < span.Length)
            {
                char fc = span[_idx];
                if (!IsRegexFlagChar(fc)) break;
                Advance(fc);
            }

            string flags = span.Slice(flagStart, _idx - flagStart).ToString();

            var payload = new Tokens.RegexLiteralPayload(pattern, flags);
            tokens.Add(new Token(TokenType.REGEX_LITERAL, payload, posStart, GetPos()));
        }

        private static bool IsRegexFlagChar(char c)
        {
            switch (c)
            {
                case 'i': case 'I':
                case 'm': case 'M':
                case 's': case 'S':
                case 'x': case 'X':
                case 'n': case 'N':
                    return true;
                default:
                    return false;
            }
        }

        // Looks for a `:format-spec` suffix inside the body of a `${...}` block.
        // Returns the index of the separating ':' in `body` (so the caller can
        // split into expression text and spec text), or -1 if no valid spec is
        // present.
        //
        // The spec colon must sit at bracket depth zero AND be followed by a
        // string that matches the format-spec grammar exactly; this rules out
        // ternary colons (`a ? b : c`), named-argument colons (`f(x: 1)`), and
        // dictionary literal colons, because the trailing text in those cases
        // never matches the format-spec form. Scanning runs right-to-left so a
        // ternary inside the body is invisible to the split.
        private static int TrySplitFormatSpec(string body)
        {
            if (string.IsNullOrEmpty(body)) return -1;

            int depthParen = 0;
            int depthSquare = 0;
            int depthBrace = 0;

            for (int i = body.Length - 1; i >= 0; i--)
            {
                char c = body[i];
                switch (c)
                {
                    case ')': depthParen++; continue;
                    case '(': depthParen--; continue;
                    case ']': depthSquare++; continue;
                    case '[': depthSquare--; continue;
                    case '}': depthBrace++; continue;
                    case '{': depthBrace--; continue;
                }

                if (depthParen != 0 || depthSquare != 0 || depthBrace != 0)
                    continue;

                if (c != ':') continue;

                // `::` is the `as`-style path separator (e.g. namespace::name)
                // - never a format spec.
                if (i + 1 < body.Length && body[i + 1] == ':') continue;
                if (i > 0 && body[i - 1] == ':') continue;

                if (IsValidFormatSpec(body, i + 1, body.Length))
                    return i;

                // First top-level `:` that does not lead a valid spec — stop;
                // anything further left would be inside an outer expression
                // that the parser already owns.
                return -1;
            }

            return -1;
        }

        // Grammar (single pass):
        //   spec   := flag? precision? type?
        //   flag   := '#'
        //   precision := '.' digit+
        //   type   := f|F|x|X|b|B|d|D|o|O|e|E|g|G|%
        // At least one of {flag, precision, type} must be present.
        private static bool IsValidFormatSpec(string s, int start, int end)
        {
            int i = start;
            bool any = false;

            if (i < end && s[i] == '#')
            {
                any = true;
                i++;
            }

            if (i < end && s[i] == '.')
            {
                i++;
                int digitStart = i;
                while (i < end && s[i] >= '0' && s[i] <= '9') i++;
                if (i == digitStart) return false;
                any = true;
            }

            if (i < end)
            {
                char t = s[i];
                if (IsFormatTypeChar(t))
                {
                    any = true;
                    i++;
                }
                else
                {
                    return false;
                }
            }

            return any && i == end;
        }

        private static bool IsFormatTypeChar(char c)
        {
            switch (c)
            {
                case 'f': case 'F':
                case 'x': case 'X':
                case 'b': case 'B':
                case 'd': case 'D':
                case 'o': case 'O':
                case 'e': case 'E':
                case 'g': case 'G':
                case '%':
                    return true;
                default:
                    return false;
            }
        }

        private static readonly Dictionary<string, Keyword> s_keywords = CreateKeywordTable();

        // Span-based alternate lookup avoids allocating a string per identifier just to
        // ask "is this a keyword?". Falls back to the regular dictionary if the alternate
        // lookup is not supported (it always is for StringComparer.Ordinal on .NET 9+).
        private static readonly Dictionary<string, Keyword>.AlternateLookup<ReadOnlySpan<char>> s_keywordsSpan
            = s_keywords.GetAlternateLookup<ReadOnlySpan<char>>();

        private static Dictionary<string, Keyword> CreateKeywordTable()
        {
            return new Dictionary<string, Keyword>(StringComparer.Ordinal)
            {
                { "var", Keyword.Var },
                { "and", Keyword.And },
                { "or", Keyword.Or },
                { "not", Keyword.Not },
                { "if", Keyword.If },
                { "elif", Keyword.Elif },
                { "else", Keyword.Else },
                { "for", Keyword.For },
                { "to", Keyword.To },
                { "step", Keyword.Step },
                { "while", Keyword.While },
                { "fn", Keyword.Fn },
                { "ret", Keyword.Ret },
                { "is", Keyword.Is },
                { "continue", Keyword.Continue },
                { "break", Keyword.Break },
                { "pass", Keyword.Pass },
                { "const", Keyword.Const },
                { "final", Keyword.Final },
                { "del", Keyword.Del },
                { "do", Keyword.Do },
                { "typeof", Keyword.TypeOf },
                { "nameof", Keyword.NameOf },
                { "null", Keyword.Null },
                { "true", Keyword.True },
                { "false", Keyword.False },
                { "in", Keyword.In },
                { "switch", Keyword.Switch },
                { "case", Keyword.Case },
                { "match", Keyword.Match },
                { "default", Keyword.Default },
                { "yield", Keyword.Yield },
                { "goto", Keyword.Goto },
                { "let", Keyword.Let },
                { "auto", Keyword.Auto },
                { "as", Keyword.As },
                { "try", Keyword.Try },
                { "catch", Keyword.Catch },
                { "finally", Keyword.Finally },
                { "retry", Keyword.Retry },
                { "times", Keyword.Times },
                { "delay", Keyword.Delay },
                { "enum", Keyword.Enum },
                { "struct", Keyword.Struct },
                { "pub", Keyword.Pub },
                { "self", Keyword.Self },
                { "class", Keyword.Class },
                { "super", Keyword.Super },
                { "override", Keyword.Override },
                { "interface", Keyword.Interface },
                { "impl", Keyword.Impl },
                { "trait", Keyword.Trait },
                { "with", Keyword.With },
                { "abstract", Keyword.Abstract },
                { "static", Keyword.Static },
                { "extend", Keyword.Extend },
                { "import", Keyword.Import },
                { "from", Keyword.From },
                { "operator", Keyword.Operator },
                { "ref", Keyword.Ref },
                { "where", Keyword.Where },
                { "annotation", Keyword.Annotation },
                { "async", Keyword.Async },
                { "await", Keyword.Await },
                { "spawn", Keyword.Spawn },
                { "emit", Keyword.Emit },
                { "namespace", Keyword.Namespace },
                { "using", Keyword.Using },
                { "asm", Keyword.Asm },
                { "mut", Keyword.Mut },
                { "move", Keyword.Move },
                { "throw", Keyword.Throw },
                { "record", Keyword.Record }
            };
        }

        private void ProcessIdentifier(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            int startIdx = _idx;

            while (_idx < span.Length && (s_isLetterOrDigit[span[_idx]]))
            {
                Advance(span[_idx]);
            }

            var idSpan = span.Slice(startIdx, _idx - startIdx);

            if (idSpan.Length == 2 && idSpan.SequenceEqual("is"))
            {
                int peekIdx = _idx;
                while (peekIdx < span.Length && (span[peekIdx] == ' ' || span[peekIdx] == '\t')) peekIdx++;

                if (peekIdx + 2 < span.Length && span.Slice(peekIdx, 3).SequenceEqual("not"))
                {
                    int afterNot = peekIdx + 3;
                    while (afterNot < span.Length && (span[afterNot] == ' ' || span[afterNot] == '\t')) afterNot++;
                    if (afterNot + 1 < span.Length && span.Slice(afterNot, 2).SequenceEqual("in"))
                    {
                        AdvanceMultiple(afterNot + 2 - _idx, span);
                        tokens.Add(new Token(TokenType.KEYWORD, Keyword.NotIn, posStart, GetPos()));
                        return;
                    }
                    AdvanceMultiple(peekIdx + 3 - _idx, span);
                    tokens.Add(new Token(TokenType.NE, null, posStart, GetPos()));
                    return;
                }

                if (peekIdx + 1 < span.Length && span.Slice(peekIdx, 2).SequenceEqual("in"))
                {
                    AdvanceMultiple(peekIdx + 2 - _idx, span);
                    tokens.Add(new Token(TokenType.KEYWORD, Keyword.In, posStart, GetPos()));
                    return;
                }

                tokens.Add(new Token(TokenType.EE, null, posStart, GetPos()));
                return;
            }

            if (idSpan.Length == 3 && idSpan.SequenceEqual("not"))
            {
                int peekIdx = _idx;
                while (peekIdx < span.Length && (span[peekIdx] == ' ' || span[peekIdx] == '\t')) peekIdx++;

                if (peekIdx + 1 < span.Length && span.Slice(peekIdx, 2).SequenceEqual("in"))
                {
                    AdvanceMultiple(peekIdx + 2 - _idx, span);
                    tokens.Add(new Token(TokenType.KEYWORD, Keyword.NotIn, posStart, GetPos()));
                    return;
                }
            }

            // Keyword fast-path: zero-allocation lookup using the span alternate key.
            if (s_keywordsSpan.TryGetValue(idSpan, out Keyword keyword))
            {
                tokens.Add(new Token(TokenType.KEYWORD, keyword, posStart, GetPos()));

                if (keyword == Keyword.Asm)
                {
                    _asmHeaderPending = true;
                }
            }
            else
            {
                // Only materialise the identifier string when it is not a keyword.
                tokens.Add(new Token(TokenType.IDENTIFIER, idSpan.ToString(), posStart, GetPos()));
            }
        }

        private static int FindTopLevelColon(string s)
        {
            int depth = 0;
            int parens = 0;
            int squares = 0;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                char c = s[i];
                if (c == ')') parens++;
                else if (c == '(') parens--;
                else if (c == ']') squares++;
                else if (c == '[') squares--;
                else if (c == '}') depth++;
                else if (c == '{') depth--;
                else if (c == ':' && depth == 0 && parens == 0 && squares == 0)
                {
                    if (i > 0 && s[i - 1] == ':') return -1;
                    if (i + 1 < s.Length && s[i + 1] == ':') return -1;
                    return i;
                }
            }
            return -1;
        }

        private void ProcessAsmBlock(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var segStartPos = GetPos();
            var sb = new StringBuilder();

            while (_idx < span.Length)
            {
                char c = span[_idx];

                if (c == '}')
                {
                    tokens.Add(new Token(TokenType.ASM_TEXT, sb.ToString(), segStartPos, GetPos()));
                    var rbracePos = GetPos();
                    Advance(c);
                    tokens.Add(new Token(TokenType.RBRACKET, null, rbracePos, GetPos()));
                    return;
                }

                if (c == '%' && _idx + 1 < span.Length && span[_idx + 1] == '%')
                {
                    sb.Append('%');
                    AdvanceMultiple(2, span);
                    continue;
                }

                if (c == '%' && _idx + 1 < span.Length && span[_idx + 1] == '{')
                {
                    tokens.Add(new Token(TokenType.ASM_TEXT, sb.ToString(), segStartPos, GetPos()));
                    sb.Clear();

                    var interpStartPos = GetPos();
                    AdvanceMultiple(2, span);
                    tokens.Add(new Token(TokenType.INTERP_START, null, interpStartPos, GetPos()));

                    int innerStartIdx = _idx;
                    int braceCount = 1;
                    while (_idx < span.Length && braceCount > 0)
                    {
                        if (span[_idx] == '{') braceCount++;
                        else if (span[_idx] == '}') braceCount--;
                        if (braceCount == 0) break;
                        Advance(span[_idx]);
                    }

                    if (_idx >= span.Length)
                    {
                        _diagnostics.AddError(
                            title: "unterminated %{...} interpolation in asm block",
                            code: DiagnosticCode.LexerUnterminatedAsmInterp,
                            positionStart: interpStartPos,
                            positionEnd: GetPos(),
                            phase: DiagnosticPhase.Lexing,
                            primaryLabel: "interpolation never closed",
                            help: "close the asm interpolation with '}'");
                        return;
                    }

                    string innerText = _text.Substring(innerStartIdx, _idx - innerStartIdx);

                    string exprText = innerText;
                    string? typeHint = null;
                    int colonAt = FindTopLevelColon(innerText);
                    if (colonAt > 0)
                    {
                        exprText = innerText.Substring(0, colonAt).TrimEnd();
                        typeHint = innerText.Substring(colonAt + 1).Trim();
                    }

                    var innerLexer = new Lexer(_fn, exprText);
                    var (innerTokens, innerDiagnostics) = innerLexer.MakeTokens();
                    _diagnostics.AddRange(innerDiagnostics);
                    foreach (var t in innerTokens)
                    {
                        if (t.Type != TokenType.EOF) tokens.Add(t);
                    }

                    var interpEndPos = GetPos();
                    Advance(span[_idx]);
                    tokens.Add(new Token(TokenType.INTERP_END, typeHint, interpEndPos, GetPos()));

                    segStartPos = GetPos();
                    continue;
                }

                sb.Append(c);
                Advance(c);
            }

            _diagnostics.AddError(
                title: "unterminated asm block",
                code: DiagnosticCode.LexerUnterminatedAsmBlock,
                positionStart: segStartPos,
                positionEnd: GetPos(),
                phase: DiagnosticPhase.Lexing,
                primaryLabel: "asm block opened here is never closed",
                help: "add a matching '}' to close the asm { ... } block");
        }

        #endregion
    }
}
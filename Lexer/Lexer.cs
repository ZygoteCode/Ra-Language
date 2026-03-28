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

        public (List<Token> Tokens, Error? Error) MakeTokens()
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
                    case '\'':
                    case '`':
                        var errStr = ProcessString(span, c, false, tokens);
                        if (errStr != null) return (new List<Token>(), errStr);
                        break;

                    case '$':
                        if (_idx + 1 < span.Length && (span[_idx + 1] == '"' || span[_idx + 1] == '\'' || span[_idx + 1] == '`'))
                        {
                            char quoteChar = span[_idx + 1];
                            Advance(c);
                            var errInterp = ProcessString(span, quoteChar, true, tokens);
                            if (errInterp != null) return (new List<Token>(), errInterp);
                        }
                        else
                        {
                            Advance(c);
                            return (new List<Token>(), new IllegalCharacterError(GetPos(), GetPos(), "$"));
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
                    case '<':
                        var errLt = ProcessLessThan(span, tokens);
                        if (errLt != null) return (new List<Token>(), errLt);
                        break;
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
                    case '{': tokens.Add(new Token(TokenType.LBRACKET, null, GetPos())); Advance(c); break;
                    case '}': tokens.Add(new Token(TokenType.RBRACKET, null, GetPos())); Advance(c); break;
                    case '~': tokens.Add(new Token(TokenType.BITWISE_NOT, null, GetPos())); Advance(c); break;
                    case ',': tokens.Add(new Token(TokenType.COMMA, null, GetPos())); Advance(c); break;

                    default:
                        if (c < 128 && s_isDigit[c])
                        {
                            var errNum = ProcessNumber(span, tokens);
                            if (errNum != null) return (new List<Token>(), errNum);
                        }
                        else if (c < 128 && s_isLetterOrDigit[c])
                        {
                            ProcessIdentifier(span, tokens);
                        }
                        else
                        {
                            var posStart = GetPos();
                            Advance(c);
                            return (new List<Token>(), new IllegalCharacterError(posStart, GetPos(), $"'{c}'"));
                        }
                        break;
                }
            }

            tokens.Add(new Token(TokenType.EOF, null, GetPos()));
            return (tokens, null);
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
        private Error? ProcessLessThan(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length)
            {
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.LTE, null, posStart, GetPos())); return null; }
                if (span[_idx] == '<')
                {
                    Advance(span[_idx]);
                    if (_idx < span.Length && span[_idx] == '=')
                    {
                        Advance(span[_idx]);
                        tokens.Add(new Token(TokenType.BITWISE_LEFT_SHIFT_EQ, null, posStart, GetPos()));
                        return null;
                    }
                    tokens.Add(new Token(TokenType.BITWISE_LEFT_SHIFT, null, posStart, GetPos()));
                    return null;
                }
                if (span[_idx] == '!')
                {
                    Advance(span[_idx]);
                    if (_idx >= span.Length || span[_idx] != '-') return new ExpectedCharacterError(posStart, GetPos(), "Expected '-' character.");
                    Advance(span[_idx]);
                    if (_idx >= span.Length || span[_idx] != '-') return new ExpectedCharacterError(posStart, GetPos(), "Expected '-' character.");
                    Advance(span[_idx]);

                    int cdataEnd = span.Slice(_idx).IndexOf("-->");
                    if (cdataEnd == -1) AdvanceMultiple(span.Length - _idx, span);
                    else AdvanceMultiple(cdataEnd + 3, span);

                    return null;
                }
            }
            tokens.Add(new Token(TokenType.LT, null, posStart, GetPos()));
            return null;
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
                if (span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.BITWISE_OR_EQ, null, posStart, GetPos())); return; }
            }
            tokens.Add(new Token(TokenType.BITWISE_OR, null, posStart, GetPos()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessColon(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == ':') { Advance(span[_idx]); tokens.Add(new Token(TokenType.DOUBLE_COLON, null, posStart, GetPos())); return; }
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

        private Error? ProcessNumber(ReadOnlySpan<char> span, List<Token> tokens)
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
                        return new InvalidSyntaxError(posStart, GetPos(), "Invalid prefixed integer literal");

                    TryReadNumberSuffix(span, ref suffix, ref isFloat);

                    string numValStr = BuildStringNoUnderscores(_text.Substring(startIdx, _idx - startIdx));
                    tokens.Add(new Token(isFloat ? TokenType.FLOAT : TokenType.INT, numValStr, posStart, GetPos()));
                    return null;
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
                        return new InvalidSyntaxError(posStart, GetPos(), "Expected digits after exponent");

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
            return null;
        }

        private Error? ProcessString(ReadOnlySpan<char> span, char stringChar, bool allowInterpolation, List<Token> tokens)
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

                    if (braceCount != 0) return new InvalidSyntaxError(posStart, GetPos(), "Unterminated interpolation in string literal");

                    string innerText = _text.Substring(innerStartIdx, _idx - 1 - innerStartIdx);
                    var innerLexer = new Lexer(_fn, innerText);
                    var (innerTokens, innerErr) = innerLexer.MakeTokens();
                    if (innerErr != null) return new InvalidSyntaxError(posStart, GetPos(), innerErr.Details);

                    foreach (var t in innerTokens)
                    {
                        if (t.Type != TokenType.EOF) tokens.Add(t);
                    }

                    var interpEndPos = GetPos();
                    tokens.Add(new Token(TokenType.INTERP_END, null, interpEndPos, interpEndPos));

                    segStartIdx = _idx;
                    segStartPos = GetPos();
                    continue;
                }

                Advance(c);
            }

            if (_idx >= span.Length || span[_idx] != stringChar)
            {
                return new InvalidSyntaxError(posStart, GetPos(), "Unterminated string literal");
            }

            string finalTextSeg = sb != null
                ? sb.Append(span.Slice(segStartIdx, _idx - segStartIdx)).ToString()
                : span.Slice(segStartIdx, _idx - segStartIdx).ToString();

            tokens.Add(new Token(TokenType.STRING_TEXT, finalTextSeg, segStartPos, GetPos()));
            Advance(span[_idx]);

            return null;
        }

        private static readonly Dictionary<string, Keyword> s_keywords = CreateKeywordTable();

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
                { "override", Keyword.Override }
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

            string idStr = idSpan.ToString();
            if (s_keywords.TryGetValue(idStr, out Keyword keyword))
            {
                tokens.Add(new Token(TokenType.KEYWORD, keyword, posStart, GetPos()));
            }
            else
            {
                tokens.Add(new Token(TokenType.IDENTIFIER, idStr, posStart, GetPos()));
            }
        }

        #endregion
    }
}
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
        private readonly string _text;
        private readonly string _fn;
        private int _idx;
        private int _ln;
        private int _col;

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AdvanceMultiple(int count, ReadOnlySpan<char> span)
        {
            int len = span.Length;
            int end = Math.Min(_idx + count, len);
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
            var tokens = new List<Token>(Math.Min(_text.Length / 4, 2048));
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
                        var posStartDollar = GetPos();
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
                            return (new List<Token>(), new IllegalCharacterError(posStartDollar, GetPos(), "$"));
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
                        if (char.IsAsciiDigit(c))
                        {
                            var errNum = ProcessNumber(span, tokens);
                            if (errNum != null) return (new List<Token>(), errNum);
                        }
                        else if (char.IsAsciiLetter(c) || c == '_')
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SkipComment(ReadOnlySpan<char> span)
        {
            int newLineIdx = span.Slice(_idx).IndexOf('\n');
            if (newLineIdx == -1)
            {
                AdvanceMultiple(span.Length - _idx, span);
            }
            else
            {
                AdvanceMultiple(newLineIdx + 1, span);
            }
        }

        #region Operators Processing

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

        private void ProcessModulo(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.MODULO_EQ, null, posStart, GetPos())); return; }
            tokens.Add(new Token(TokenType.MODULO, null, posStart, GetPos()));
        }

        private void ProcessPow(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == '=') { Advance(span[_idx]); tokens.Add(new Token(TokenType.POW_EQ, null, posStart, GetPos())); return; }
            tokens.Add(new Token(TokenType.POW, null, posStart, GetPos()));
        }

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

        private void ProcessColon(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            Advance(span[_idx]);
            if (_idx < span.Length && span[_idx] == ':') { Advance(span[_idx]); tokens.Add(new Token(TokenType.DOUBLE_COLON, null, posStart, GetPos())); return; }
            tokens.Add(new Token(TokenType.COLON, null, posStart, GetPos()));
        }

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
            if (!span.Contains('_')) return span.ToString();
            var sb = new StringBuilder(span.Length);
            foreach (var ch in span)
                if (ch != '_') sb.Append(ch);
            return sb.ToString();
        }

        private bool TryReadNumberSuffix(ReadOnlySpan<char> span, ref string? suffix, ref bool isFloat)
        {
            int remaining = span.Length - _idx;
            if (remaining <= 0)
                return false;

            if (remaining >= 2)
            {
                char c0 = span[_idx];
                char c1 = span[_idx + 1];

                if ((c0 == 'u' || c0 == 'U') && (c1 == 'i' || c1 == 'I'))
                {
                    suffix = "ui";
                    AdvanceMultiple(2, span);
                    return true;
                }

                if ((c0 == 'u' || c0 == 'U') && (c1 == 'l' || c1 == 'L'))
                {
                    suffix = "ul";
                    AdvanceMultiple(2, span);
                    return true;
                }

                if ((c0 == 'u' || c0 == 'U') && (c1 == 's' || c1 == 'S'))
                {
                    suffix = "us";
                    AdvanceMultiple(2, span);
                    return true;
                }
            }

            char c = span[_idx];

            if (c == 'i' || c == 'I')
            {
                suffix = "i";
                Advance(c);
                return true;
            }

            if (c == 'l' || c == 'L')
            {
                suffix = "l";
                Advance(c);
                return true;
            }

            if (c == 'd' || c == 'D')
            {
                suffix = "d";
                isFloat = true;
                Advance(c);
                return true;
            }

            if (c == 'f' || c == 'F')
            {
                suffix = "f";
                isFloat = true;
                Advance(c);
                return true;
            }

            if (c == 'm' || c == 'M')
            {
                suffix = "m";
                isFloat = true;
                Advance(c);
                return true;
            }

            if (c == 's' || c == 'S')
            {
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
                char p = char.ToLowerInvariant(span[_idx + 1]);
                if (p == 'x' || p == 'b' || p == 'o')
                {
                    AdvanceMultiple(2, span);
                    bool anyDigit = false;

                    while (_idx < span.Length)
                    {
                        char c = span[_idx];
                        if (c == '_') { Advance(c); continue; }

                        bool isValid = p == 'x' ? Utils.IsHexDigit(c) :
                                       p == 'b' ? Utils.IsBinaryDigit(c) :
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

                if (char.IsAsciiDigit(c))
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
                else if (c == 'e' || c == 'E')
                {
                    isFloat = true;
                    Advance(c);

                    if (_idx < span.Length && (span[_idx] == '+' || span[_idx] == '-'))
                        Advance(span[_idx]);

                    if (_idx >= span.Length || !char.IsAsciiDigit(span[_idx]))
                        return new InvalidSyntaxError(posStart, GetPos(), "Expected digits after exponent");

                    while (_idx < span.Length && char.IsAsciiDigit(span[_idx]))
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

        private void ProcessIdentifier(ReadOnlySpan<char> span, List<Token> tokens)
        {
            var posStart = GetPos();
            int startIdx = _idx;

            while (_idx < span.Length && (char.IsAsciiLetterOrDigit(span[_idx]) || span[_idx] == '_'))
            {
                Advance(span[_idx]);
            }

            var idSpan = span.Slice(startIdx, _idx - startIdx);

            if (idSpan.SequenceEqual("is"))
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

            if (idSpan.SequenceEqual("not"))
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

            Keyword? keyword = GetKeyword(idSpan);
            if (keyword.HasValue)
            {
                tokens.Add(new Token(TokenType.KEYWORD, keyword.Value, posStart, GetPos()));
            }
            else
            {
                tokens.Add(new Token(TokenType.IDENTIFIER, idSpan.ToString(), posStart, GetPos()));
            }
        }

        private static Keyword? GetKeyword(ReadOnlySpan<char> ident)
        {
            switch (ident)
            {
                case "var": return Keyword.Var;
                case "and": return Keyword.And;
                case "or": return Keyword.Or;
                case "not": return Keyword.Not;
                case "if": return Keyword.If;
                case "elif": return Keyword.Elif;
                case "else": return Keyword.Else;
                case "for": return Keyword.For;
                case "to": return Keyword.To;
                case "step": return Keyword.Step;
                case "while": return Keyword.While;
                case "fn": return Keyword.Fn;
                case "ret": return Keyword.Ret;
                case "is": return Keyword.Is;
                case "continue": return Keyword.Continue;
                case "break": return Keyword.Break;
                case "pass": return Keyword.Pass;
                case "const": return Keyword.Const;
                case "final": return Keyword.Final;
                case "del": return Keyword.Del;
                case "do": return Keyword.Do;
                case "typeof": return Keyword.TypeOf;
                case "nameof": return Keyword.NameOf;
                case "null": return Keyword.Null;
                case "true": return Keyword.True;
                case "false": return Keyword.False;
                case "in": return Keyword.In;
                case "switch": return Keyword.Switch;
                case "case": return Keyword.Case;
                case "default": return Keyword.Default;
                case "yield": return Keyword.Yield;
                case "goto": return Keyword.Goto;
                case "let": return Keyword.Let;
                case "auto": return Keyword.Auto;
                case "as": return Keyword.As;
                case "try": return Keyword.Try;
                case "catch": return Keyword.Catch;
                case "finally": return Keyword.Finally;
                case "retry": return Keyword.Retry;
                case "times": return Keyword.Times;
                case "delay": return Keyword.Delay;
                case "enum": return Keyword.Enum;
                case "struct": return Keyword.Struct;
                case "pub": return Keyword.Pub;
                case "self": return Keyword.Self;
                default: return null;
            }
        }

        #endregion
    }
}
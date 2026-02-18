using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Utilities;
using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RaLanguage.Lexer
{
    public sealed class Lexer
    {
        private readonly string _fn;
        private readonly string _text;

        private Position _position;
        private char? _currentCharacter;

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "VAR", "AND", "OR", "NOT", "IF", "ELIF", "ELSE", "FOR",
            "TO", "STEP", "WHILE", "FUN", "THEN", "END", "RETURN", "CONTINUE", "BREAK"
        };

        public Lexer(string fn, string text)
        {
            _fn = fn;
            _text = text ?? string.Empty;
            _position = new Position(-1, 0, -1, fn, text);
            Advance();
        }

        private void Advance()
        {
            _position.Advance(_currentCharacter);
            _currentCharacter = _position.Idx < _text.Length ? _text[_position.Idx] : null;
        }

        public (List<Token> Tokens, Error? Error) MakeTokens()
        {
            var tokens = new List<Token>();

            while (_currentCharacter != null)
            {
                switch (_currentCharacter)
                {
                    case ' ':
                    case '\t':
                    case '\r':
                        SkipWhitespaceSimd();
                        break;
                    case '#':
                        SkipComment();
                        break;
                    case ';':
                    case '\n':
                        tokens.Add(new Token(TokenType.NEWLINE, null, _position));
                        Advance();
                        break;
                    case '"':
                        tokens.Add(MakeString());
                        break;
                    case '+':
                        tokens.Add(new Token(TokenType.PLUS, null, _position));
                        Advance();
                        break;
                    case '-':
                        tokens.Add(MakeMinusOrArrow());
                        break;
                    case '*':
                        tokens.Add(new Token(TokenType.MUL, null, _position));
                        Advance();
                        break;
                    case '/':
                        tokens.Add(new Token(TokenType.DIV, null, _position));
                        Advance();
                        break;
                    case '^':
                        tokens.Add(new Token(TokenType.POW, null, _position));
                        Advance();
                        break;
                    case '(':
                        tokens.Add(new Token(TokenType.LPAREN, null, _position));
                        Advance();
                        break;
                    case ')':
                        tokens.Add(new Token(TokenType.RPAREN, null, _position));
                        Advance();
                        break;
                    case '[':
                        tokens.Add(new Token(TokenType.LSQUARE, null, _position));
                        Advance();
                        break;
                    case ']':
                        tokens.Add(new Token(TokenType.RSQUARE, null, _position));
                        Advance();
                        break;
                    case '!':
                        var (token, error) = MakeNotEquals();
                        if (error != null) return (new List<Token>(), error);
                        tokens.Add(token!);
                        break;
                    case '=':
                        tokens.Add(MakeEquals());
                        break;
                    case '<':
                        tokens.Add(MakeLessThan());
                        break;
                    case '>':
                        tokens.Add(MakeGreaterThan());
                        break;
                    case ',':
                        tokens.Add(new Token(TokenType.COMMA, null, _position));
                        Advance();
                        break;
                    default:
                        char c = _currentCharacter.Value;

                        if (Constants.DIGITS.Contains(c))
                        {
                            tokens.Add(MakeNumber());
                        }
                        else if (Constants.LETTERS.Contains(c))
                        {
                            tokens.Add(MakeIdentifier());
                        }
                        else
                        {
                            var positionStart = _position.Copy();
                            Advance();
                            return (new List<Token>(), new IllegalCharacterError(positionStart, _position, $"'{c}'"));
                        }

                        break;
                }
            }

            tokens.Add(new Token(TokenType.EOF, null, _position));
            return (tokens, null);
        }


        private void SkipWhitespaceSimd()
        {
            if (!Avx2.IsSupported)
            {
                while (_currentCharacter != null &&
                      (_currentCharacter == ' ' || _currentCharacter == '\t' || _currentCharacter == '\r'))
                {
                    Advance();
                }
                return;
            }

            ReadOnlySpan<char> span = _text.AsSpan();
            int idx = _position.Idx;

            var vSpace = Vector256.Create((ushort)' ');
            var vTab = Vector256.Create((ushort)'\t');
            var vCr = Vector256.Create((ushort)'\r');

            while (idx + 16 <= span.Length)
            {
                ref char r = ref MemoryMarshal.GetReference(span.Slice(idx));
                ref ushort ur = ref Unsafe.As<char, ushort>(ref r);

                var vec = Vector256.LoadUnsafe(ref ur);

                var isSpace = Avx2.CompareEqual(vec, vSpace);
                var isTab = Avx2.CompareEqual(vec, vTab);
                var isCr = Avx2.CompareEqual(vec, vCr);

                var combined = Avx2.Or(Avx2.Or(isSpace, isTab), isCr);

                int mask = Avx2.MoveMask(combined.AsByte());

                if (mask == -1)
                {
                    idx += 16;
                    continue;
                }

                int inv = ~mask;
                if (inv == 0)
                {
                    break;
                }

                int firstNonWhitespace = BitOperations.TrailingZeroCount((uint)inv) / 2;
                idx += firstNonWhitespace;
                goto set_position_and_advance;
            }

            while (idx < span.Length && (span[idx] == ' ' || span[idx] == '\t' || span[idx] == '\r'))
                idx++;

            set_position_and_advance:
            _position.Idx = idx - 1;
            Advance();
        }

        private void SkipComment()
        {
            Advance();

            if (!Avx2.IsSupported)
            {
                while (_currentCharacter != null && _currentCharacter != '\n')
                    Advance();
                Advance();
                return;
            }

            ReadOnlySpan<char> span = _text.AsSpan();
            int idx = _position.Idx;

            var vNewline = Vector256.Create((ushort)'\n');

            while (idx + 16 <= span.Length)
            {
                ref char r = ref MemoryMarshal.GetReference(span.Slice(idx));
                ref ushort ur = ref Unsafe.As<char, ushort>(ref r);

                var vec = Vector256.LoadUnsafe(ref ur);
                var cmp = Avx2.CompareEqual(vec, vNewline);
                int mask = Avx2.MoveMask(cmp.AsByte());

                if (mask != 0)
                {
                    int firstMatch = BitOperations.TrailingZeroCount((uint)mask) / 2;
                    idx += firstMatch;
                    goto finalize_comment;
                }

                idx += 16;
            }

            while (idx < span.Length && span[idx] != '\n')
                idx++;

            finalize_comment:
            _position.Idx = idx - 1;
            Advance();
        }

        private Token MakeNumber()
        {
            int startIdx = _position.Idx;
            int dotCount = 0;

            while (_currentCharacter != null && (Constants.DIGITS.Contains(_currentCharacter.Value) || _currentCharacter == '.'))
            {
                if (_currentCharacter == '.')
                {
                    if (dotCount == 1) break;
                    dotCount++;
                }
                Advance();
            }

            int length = _position.Idx - startIdx;
            if (length <= 0)
            {
                return new Token(TokenType.INT, 0, _position.Copy(), _position);
            }

            ReadOnlySpan<char> slice = _text.AsSpan(startIdx, length);

            if (dotCount == 0)
            {
                if (int.TryParse(slice, NumberStyles.None, CultureInfo.InvariantCulture, out int intVal))
                    return new Token(TokenType.INT, intVal, new Position(startIdx, 0, startIdx, _fn, _text), _position);
                if (long.TryParse(slice, NumberStyles.None, CultureInfo.InvariantCulture, out long longVal))
                    return new Token(TokenType.INT, (int)longVal, new Position(startIdx, 0, startIdx, _fn, _text), _position);
                double.TryParse(slice, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double d19);
                return new Token(TokenType.INT, (int)d19, new Position(startIdx, 0, startIdx, _fn, _text), _position);
            }
            else
            {
                if (double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out double dblVal))
                    return new Token(TokenType.FLOAT, dblVal, new Position(startIdx, 0, startIdx, _fn, _text), _position);
                return new Token(TokenType.FLOAT, 0.0, new Position(startIdx, 0, startIdx, _fn, _text), _position);
            }
        }

        private Token MakeString()
        {
            var positionStart = _position.Copy();
            Advance();

            char[]? rented = null;
            int rentedPos = 0;
            bool usedRented = false;

            var sbFallback = new ValueStringBuilder(stackalloc char[128]);

            bool escape = false;

            while (_currentCharacter != null && (_currentCharacter != '"' || escape))
            {
                char c = _currentCharacter.Value;
                if (escape)
                {
                    char mapped = c switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        'r' => '\r',
                        _ => c
                    };

                    sbFallback.Append(mapped);
                    escape = false;
                }
                else
                {
                    if (c == '\\')
                    {
                        escape = true;
                    }
                    else
                    {
                        sbFallback.Append(c);
                    }
                }
                Advance();
            }

            Advance();
            string final = sbFallback.ToString();
            return new Token(TokenType.STRING, final, positionStart, _position);
        }

        private Token MakeIdentifier()
        {
            int startIdx = _position.Idx;

            while (_currentCharacter != null && (Constants.LETTERS_DIGITS.Contains(_currentCharacter.Value) || _currentCharacter == '_'))
                Advance();

            int length = _position.Idx - startIdx;
            if (length <= 0) return new Token(TokenType.IDENTIFIER, string.Empty, _position.Copy(), _position);

            string idString = _text.Substring(startIdx, length);
            TokenType type = Keywords.Contains(idString) ? TokenType.KEYWORD : TokenType.IDENTIFIER;
            return new Token(type, idString, new Position(startIdx, 0, startIdx, _fn, _text), _position);
        }

        private Token MakeMinusOrArrow()
        {
            TokenType type = TokenType.MINUS;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '>')
            {
                Advance();
                type = TokenType.ARROW;
            }

            return new Token(type, null, positionStart, _position);
        }

        private (Token?, Error?) MakeNotEquals()
        {
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '=')
            {
                Advance();
                return (new Token(TokenType.NE, null, positionStart, _position), null);
            }

            Advance();
            return (null, new ExpectedCharacterError(positionStart, _position, "'=' (after '!')"));
        }

        private Token MakeEquals()
        {
            TokenType type = TokenType.EQ;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.EE;
            }
            return new Token(type, null, positionStart, _position);
        }

        private Token MakeLessThan()
        {
            TokenType type = TokenType.LT;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.LTE;
            }
            return new Token(type, null, positionStart, _position);
        }

        private Token MakeGreaterThan()
        {
            TokenType type = TokenType.GT;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.GTE;
            }
            return new Token(type, null, positionStart, _position);
        }
    }

    internal ref struct ValueStringBuilder
    {
        private char[]? _arrayFromPool;
        private Span<char> _chars;
        private int _pos;

        public ValueStringBuilder(Span<char> initialBuffer)
        {
            _arrayFromPool = null;
            _chars = initialBuffer;
            _pos = 0;
        }

        public int Length => _pos;

        public void Append(char c)
        {
            if (_pos >= _chars.Length)
            {
                Grow(1);
            }

            _chars[_pos++] = c;
        }

        private void Grow(int additional)
        {
            int newSize = Math.Max(_chars.Length * 2, _chars.Length + additional);
            char[] poolArr = ArrayPool<char>.Shared.Rent(newSize);
            _chars.CopyTo(poolArr);
            if (_arrayFromPool != null) ArrayPool<char>.Shared.Return(_arrayFromPool);
            _chars = poolArr;
            _arrayFromPool = poolArr;
        }

        public override string ToString()
        {
            var s = new string(_chars.Slice(0, _pos));

            if (_arrayFromPool != null)
            {
                ArrayPool<char>.Shared.Return(_arrayFromPool);
                _arrayFromPool = null;
            }

            return s;
        }
    }
}
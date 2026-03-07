using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Utilities;
using System.Text;

namespace RaLanguage.Lexer
{
    public class Lexer
    {
        private readonly string _text;
        private Position _position;
        private char? _currentCharacter;

        private static readonly Dictionary<string, Keyword> KeywordMap = new(StringComparer.Ordinal)
        {
            ["var"] = Keyword.Var,
            ["and"] = Keyword.And,
            ["or"] = Keyword.Or,
            ["not"] = Keyword.Not,
            ["if"] = Keyword.If,
            ["elif"] = Keyword.Elif,
            ["else"] = Keyword.Else,
            ["for"] = Keyword.For,
            ["to"] = Keyword.To,
            ["step"] = Keyword.Step,
            ["while"] = Keyword.While,
            ["fn"] = Keyword.Fn,
            ["ret"] = Keyword.Ret,
            ["is"] = Keyword.Is,
            ["continue"] = Keyword.Continue,
            ["break"] = Keyword.Break,
            ["pass"] = Keyword.Pass,
            ["const"] = Keyword.Const,
            ["final"] = Keyword.Final,
            ["del"] = Keyword.Del,
            ["do"] = Keyword.Do,
            ["typeof"] = Keyword.TypeOf,
            ["nameof"] = Keyword.NameOf,
            ["null"] = Keyword.Null,
            ["true"] = Keyword.True,
            ["false"] = Keyword.False,
            ["in"] = Keyword.In,
            ["not in"] = Keyword.NotIn,
            ["switch"] = Keyword.Switch,
            ["case"] = Keyword.Case,
            ["default"] = Keyword.Default,
            ["yield"] = Keyword.Yield,
            ["goto"] = Keyword.Goto,
        };

        public Lexer(string fn, string text)
        {
            _text = text;
            _position = new Position(-1, 0, -1, fn, text);
            Advance();
        }

        private void Advance(int times = 1)
        {
            for (int i = 0; i < times; i++)
            {
                _position.Advance(_currentCharacter);
                _currentCharacter = _position.Idx < _text.Length ? _text[_position.Idx] : null;
            }
        }

        public (List<Token> Tokens, Error? Error) MakeTokens()
        {
            var tokens = new List<Token>();

            while (_currentCharacter != null)
            {
                switch (_currentCharacter.Value)
                {
                    case ' ':
                    case '\r':
                    case '\t':
                        Advance();
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
                    case '\'':
                    case '`':
                        var positionStart2 = _position.Copy();

                        try
                        {
                            (List<Token>?, Error?) result2 = MakeString(_currentCharacter!.Value, false);
                            if (result2.Item2 != null) return (new List<Token>(), result2.Item2!);
                            tokens.AddRange(result2.Item1!);
                        }
                        catch
                        {
                            return (new List<Token>(), new InvalidSyntaxError(positionStart2, _position.Copy(), "Invalid string format"));
                        }

                        break;
                    case '$':
                        var positionStart1 = _position.Copy();

                        if (_position.Idx + 1 < _text.Length &&
                            (_text[_position.Idx + 1] == '"' || _text[_position.Idx + 1] == '\'' || _text[_position.Idx + 1] == '`'))
                        {
                            Advance();

                            try
                            {
                                (List<Token>?, Error?) result1 = MakeString(_currentCharacter!.Value, true);
                                if (result1.Item2 != null) return (new List<Token>(), result1.Item2!);
                                tokens.AddRange(result1.Item1!);
                            }
                            catch (Exception ex)
                            {
                                return (new List<Token>(), new InvalidSyntaxError(positionStart1, _position.Copy(), ex.Message));
                            }
                        }
                        else
                        {
                            Advance();
                            return (new List<Token>(), new IllegalCharacterError(positionStart1, _position, "$"));
                        }
                        break;
                    case '+':
                        tokens.Add(MakePlus());
                        break;
                    case '-':
                        Token? tok = MakeMinus();
                        if (tok != null) tokens.Add(tok);
                        break;
                    case '*':
                        tokens.Add(MakeMul());
                        break;
                    case '/':
                        Token? tok1 = MakeDiv();
                        if (tok1 != null) tokens.Add(tok1);
                        break;
                    case '%':
                        tokens.Add(MakeModulo());
                        break;
                    case '^':
                        tokens.Add(MakePow());
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
                    case ':':
                        tokens.Add(MakeColon());
                        break;
                    case '.':
                        tokens.Add(MakeDot());
                        break;
                    case '?':
                        tokens.Add(MakeQuestionMark());
                        break;
                    case '{':
                        tokens.Add(new Token(TokenType.LBRACKET, null, _position));
                        Advance();
                        break;
                    case '}':
                        tokens.Add(new Token(TokenType.RBRACKET, null, _position));
                        Advance();
                        break;
                    case '!':
                        tokens.Add(MakeNot());
                        break;
                    case '~':
                        tokens.Add(new Token(TokenType.BITWISE_NOT, null, _position));
                        Advance();
                        break;
                    case '=':
                        tokens.Add(MakeEquals());
                        break;
                    case '<':
                        (Token?, Error?) result = MakeLessThan();
                        if (result.Item2 != null) return (new List<Token>(), result.Item2);
                        if (result.Item1 != null) tokens.Add(result.Item1);
                        break;
                    case '>':
                        tokens.Add(MakeGreaterThan());
                        break;
                    case ',':
                        tokens.Add(new Token(TokenType.COMMA, null, _position));
                        Advance();
                        break;
                    case '&':
                        tokens.Add(MakeAnd());
                        break;
                    case '|':
                        tokens.Add(MakeOr());
                        break;
                    default:
                        if (Constants.DIGITS.Contains(_currentCharacter.Value))
                        {
                            (Token?, Error?) result1 = MakeNumber();
                            if (result1.Item2 != null) return (new List<Token>(), result1.Item2);
                            if (result1.Item1 == null) return (new List<Token>(), new InvalidSyntaxError(_position.Copy(), _position, "Invalid number format"));
                            tokens.Add(result1.Item1!);
                        }
                        else if (Constants.LETTERS.Contains(_currentCharacter.Value))
                        {
                            tokens.Add(MakeIdentifier());
                        }
                        else
                        {
                            var positionStart = _position.Copy();
                            char charErr = _currentCharacter.Value;
                            Advance();
                            return (new List<Token>(), new IllegalCharacterError(positionStart, _position, $"'{charErr}'"));
                        }

                        break;
                }
            }

            tokens.Add(new Token(TokenType.EOF, null, _position));
            return (tokens, null);
        }

        private Token MakeQuestionMark()
        {
            TokenType type = TokenType.QUESTION_MARK;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '?')
            {
                Advance();
                type = TokenType.NULL_COALESCE;

                if (_currentCharacter == '=')
                {
                    Advance();
                    type = TokenType.NULL_COALESCE_EQ;
                }
            }

            return new Token(type, null, positionStart, _position);
        }

        private Token MakeColon()
        {
            TokenType type = TokenType.COLON;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == ':')
            {
                Advance();
                type = TokenType.DOUBLE_COLON;
            }

            return new Token(type, null, positionStart, _position);
        }

        private Token MakeDot()
        {
            TokenType type = TokenType.DOT;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '.')
            {
                Advance();
                type = TokenType.DOUBLE_DOT;

                if (_currentCharacter == '=')
                {
                    Advance();
                    type = TokenType.DOUBLE_DOT_EQ;
                }
                else if (_currentCharacter == '.')
                {
                    Advance();
                    type = TokenType.SPREAD;
                }
            }

            return new Token(type, null, positionStart, _position);
        }

        private Token MakePlus()
        {
            TokenType type = TokenType.PLUS;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.PLUS_EQ;
            }
            else if (_currentCharacter == '+')
            {
                Advance();
                type = TokenType.DOUBLE_PLUS;
            }

            return new Token(type, null, positionStart, _position);
        }

        private Token MakePow()
        {
            TokenType type = TokenType.POW;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.POW_EQ;
            }

            return new Token(type, null, positionStart, _position);
        }

        private Token MakeModulo()
        {
            TokenType type = TokenType.MODULO;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.MODULO_EQ;
            }

            return new Token(type, null, positionStart, _position);
        }

        private Token MakeMul()
        {
            TokenType type = TokenType.MUL;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '*')
            {
                Advance();
                type = TokenType.POW;

                if (_currentCharacter == '=')
                {
                    Advance();
                    type = TokenType.POW_EQ;
                }
            }
            else if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.MUL_EQ;
            }

            return new Token(type, null, positionStart, _position);
        }

        private Token? MakeDiv()
        {
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '/')
            {
                Advance();

                while (_currentCharacter != null && _currentCharacter != '\n')
                {
                    Advance();
                }

                Advance();
                return null;
            }
            else if (_currentCharacter == '*')
            {
                Advance();

                while (_currentCharacter != null && _text[_position.Idx] != '/' && _text[_position.Idx + 1] != '*')
                {
                    Advance();
                }

                Advance(3);
                return null;
            }
            else if (_currentCharacter == '=')
            {
                Advance();
                return new Token(TokenType.DIV_EQ, null, positionStart, _position);
            }
            else
            {
                return new Token(TokenType.DIV, null, positionStart, _position);
            }
        }

        private (Token?, Error?) MakeNumber()
        {
            var numStr = new StringBuilder();
            int dotCount = 0;
            var positionStart = _position.Copy();

            if (_currentCharacter == '0' && _text[_position.Idx + 1] is char p && (p == 'x' || p == 'X' || p == 'b' || p == 'B' || p == 'o' || p == 'O'))
            {
                numStr.Append(_currentCharacter);
                Advance();

                numStr.Append(_currentCharacter);
                Advance();

                bool anyDigit = false;
                char prefix = char.ToLower(numStr[1]);
                while (_currentCharacter != null)
                {
                    if (_currentCharacter == '_') { Advance(); continue; }

                    if (prefix == 'x' && Utils.IsHexDigit(_currentCharacter.Value))
                    {
                        numStr.Append(_currentCharacter);
                        anyDigit = true;
                    }
                    else if (prefix == 'b' && Utils.IsBinaryDigit(_currentCharacter.Value))
                    {
                        numStr.Append(_currentCharacter);
                        anyDigit = true;
                    }
                    else if (prefix == 'o' && Utils.IsOctalDigit(_currentCharacter.Value))
                    {
                        numStr.Append(_currentCharacter);
                        anyDigit = true;
                    }
                    else break;

                    Advance();
                }

                if (!anyDigit) return (null, new InvalidSyntaxError(positionStart, _position, "Invalid prefixed integer literal"));

                return (new Token(TokenType.INT, numStr.ToString(), positionStart, _position), null);
            }

            while (_currentCharacter != null && (Constants.DIGITS.Contains(_currentCharacter.Value) || _currentCharacter == '.' || _currentCharacter == '_' || _currentCharacter == 'e' || _currentCharacter == 'E'))
            {
                if (_currentCharacter == '_') { Advance(); continue; }

                if (_currentCharacter == '.')
                {
                    if (_position.Idx + 1 < _text.Length && _text[_position.Idx + 1] == '.')
                    {
                        break;
                    }

                    if (dotCount == 1) break;
                    dotCount++;
                    numStr.Append('.');
                    Advance();
                    continue;
                }

                if (_currentCharacter == 'e' || _currentCharacter == 'E')
                {
                    numStr.Append(_currentCharacter);
                    Advance();

                    if (_currentCharacter == '+' || _currentCharacter == '-')
                    {
                        numStr.Append(_currentCharacter);
                        Advance();
                    }

                    if (_currentCharacter == null || !Constants.DIGITS.Contains(_currentCharacter.Value))
                        return (null, new InvalidSyntaxError(positionStart, _position, "Expected digits after exponent"));

                    while (_currentCharacter != null && Constants.DIGITS.Contains(_currentCharacter.Value))
                    {
                        numStr.Append(_currentCharacter);
                        Advance();
                    }
                    break;
                }

                numStr.Append(_currentCharacter);
                Advance();
            }

            string finalNum = numStr.ToString();
            if (dotCount == 0 && !finalNum.Contains('e') && !finalNum.Contains('E'))
                return (new Token(TokenType.INT, finalNum, positionStart, _position), null);
            else
                return (new Token(TokenType.FLOAT, finalNum, positionStart, _position), null);
        }

        private (List<Token>?, Error?) MakeString(char stringCharacter, bool allowInterpolation)
        {
            var tokens = new List<Token>();
            var sb = new StringBuilder();
            var positionStart = _position.Copy();
            bool escape = false;

            Advance();
            var segStartPos = _position.Copy();

            void FlushTextBuffer(Position startPos, Position endPos)
            {
                tokens.Add(new Token(TokenType.STRING_TEXT, sb.ToString(), startPos, endPos));
                sb.Clear();
            }

            while (_currentCharacter != null && (_currentCharacter != stringCharacter || escape))
            {
                if (escape)
                {
                    switch (_currentCharacter)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        case '`': sb.Append('`'); break;
                        default: sb.Append(_currentCharacter); break;
                    }
                    escape = false;
                    Advance();
                    continue;
                }

                if (_currentCharacter == '\\')
                {
                    escape = true;
                    Advance();
                    continue;
                }

                if (allowInterpolation && _currentCharacter == '$' && _position.Idx + 1 < _text.Length && _text[_position.Idx + 1] == '{')
                {
                    var segEndPos = _position.Copy();
                    FlushTextBuffer(segStartPos, segEndPos);

                    var interpStartPos = _position.Copy();
                    Advance(2);

                    tokens.Add(new Token(TokenType.INTERP_START, null, interpStartPos, _position.Copy()));

                    int innerStartIdx = _position.Idx;
                    int scanIdx = innerStartIdx;
                    int braceCount = 1;

                    while (scanIdx < _text.Length && braceCount > 0)
                    {
                        char c = _text[scanIdx];
                        if (c == '{') braceCount++;
                        else if (c == '}') braceCount--;
                        scanIdx++;
                    }

                    if (braceCount != 0)
                        return (null, new InvalidSyntaxError(positionStart, _position, "Unterminated interpolation in string literal"));

                    string innerText = _text.Substring(innerStartIdx, scanIdx - 1 - innerStartIdx);
                    var innerLexer = new Lexer(positionStart.Fn ?? "", innerText);
                    var (innerTokens, innerErr) = innerLexer.MakeTokens();
                    if (innerErr != null)
                        return (null, new InvalidSyntaxError(positionStart, _position, innerErr.Details));

                    foreach (var t in innerTokens)
                    {
                        if (t.Type != TokenType.EOF) tokens.Add(t);
                    }

                    Advance(scanIdx - _position.Idx);

                    var interpEndPos = _position.Copy();
                    tokens.Add(new Token(TokenType.INTERP_END, null, interpEndPos, interpEndPos));

                    segStartPos = _position.Copy();
                    continue;
                }

                sb.Append(_currentCharacter);
                Advance();
            }

            var finalSegEndPos = _position.Copy();
            FlushTextBuffer(segStartPos, finalSegEndPos);

            if (_currentCharacter == stringCharacter)
                Advance();
            else
                return (null, new InvalidSyntaxError(positionStart, _position, "Unterminated string literal"));

            return (tokens, null);
        }

        private Token MakeIdentifier()
        {
            var idStr = new StringBuilder();
            var positionStart = _position.Copy();

            while (_currentCharacter != null && (Constants.LETTERS_DIGITS.Contains(_currentCharacter.Value) || _currentCharacter == '_'))
            {
                idStr.Append(_currentCharacter);
                Advance();
            }

            string idString = idStr.ToString();

            if (idString == "is")
            {
                while (_currentCharacter == ' ' || _currentCharacter == '\t')
                {
                    Advance();
                }

                if (_currentCharacter == 'n' && _text[_position.Idx + 1] == 'o' && _text[_position.Idx + 2] == 't')
                {
                    Advance(3);

                    while (_currentCharacter == ' ' || _currentCharacter == '\t')
                    {
                        Advance();
                    }

                    if (_currentCharacter == 'i' && _text[_position.Idx + 1] == 'n')
                    {
                        Advance(2);
                        return new Token(TokenType.KEYWORD, Keyword.NotIn, positionStart, _position);
                    }

                    return new Token(TokenType.NE, null, positionStart, _position);
                }
                else if (_currentCharacter == 'i' && _text[_position.Idx + 1] == 'n')
                {
                    Advance(2);
                    return new Token(TokenType.KEYWORD, Keyword.In, positionStart, _position);
                }

                return new Token(TokenType.EE, null, positionStart, _position);
            }
            else if (idString == "not")
            {
                while (_currentCharacter == ' ' || _currentCharacter == '\t')
                {
                    Advance();
                }

                if (_currentCharacter == 'i' && _text[_position.Idx + 1] == 'n')
                {
                    Advance(2);
                    return new Token(TokenType.KEYWORD, Keyword.NotIn, positionStart, _position);
                }
            }

            if (KeywordMap.ContainsKey(idString))
            {
                return new Token(TokenType.KEYWORD, KeywordMap[idString], positionStart, _position);
            }

            return new Token(TokenType.IDENTIFIER, idString, positionStart, _position);
        }

        private Token? MakeMinus()
        {
            TokenType type = TokenType.MINUS;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '-' && _text[_position.Idx + 1] == '-')
            {
                Advance(2);

                while (_currentCharacter != null && _currentCharacter != '\n')
                {
                    Advance();
                }

                Advance();
                return null;
            }
            else if (_currentCharacter == '-')
            {
                Advance();
                type = TokenType.DOUBLE_MINUS;
            }
            else if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.MINUS_EQ;
            }
            else if (_currentCharacter == '>')
            {
                Advance();
                type = TokenType.ARROW_RIGHT;
            }

            return new Token(type, null, positionStart, _position);
        }

        private Token MakeNot()
        {
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '=')
            {
                Advance();

                if (_currentCharacter == '=')
                {
                    Advance();
                    return new Token(TokenType.STRICT_NE, null, positionStart, _position);
                }

                return new Token(TokenType.NE, null, positionStart, _position);
            }

            return new Token(TokenType.KEYWORD, Keyword.Not, positionStart, _position);
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

                if (_currentCharacter == '=')
                {
                    Advance();
                    type = TokenType.STRICT_EE;
                }
            }
            else if (_currentCharacter == '>')
            {
                Advance();
                type = TokenType.ARROW;
            }

            return new Token(type, null, positionStart, _position);
        }

        private Token MakeAnd()
        {
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '&')
            {
                Advance();

                if (_currentCharacter == '=')
                {
                    Advance();
                    return new Token(TokenType.AND_EQ, null, positionStart, _position);
                }

                return new Token(TokenType.KEYWORD, Keyword.And, positionStart, _position);
            }
            else if (_currentCharacter == '=')
            {
                Advance();
                return new Token(TokenType.BITWISE_AND_EQ, null, positionStart, _position);
            }

            return new Token(TokenType.BITWISE_AND, null, positionStart, _position);
        }

        private Token MakeOr()
        {
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '|')
            {
                Advance();

                if (_currentCharacter == '=')
                {
                    Advance();
                    return new Token(TokenType.OR_EQ, null, positionStart, _position);
                }

                return new Token(TokenType.KEYWORD, Keyword.Or, positionStart, _position);
            }
            else if (_currentCharacter == '=')
            {
                Advance();
                return new Token(TokenType.BITWISE_OR_EQ, null, positionStart, _position);
            }

            return new Token(TokenType.BITWISE_OR, null, positionStart, _position);
        }

        private (Token?, Error?) MakeLessThan()
        {
            TokenType type = TokenType.LT;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.LTE;
            }
            else if (_currentCharacter == '<')
            {
                Advance();
                type = TokenType.BITWISE_LEFT_SHIFT;

                if (_currentCharacter == '=')
                {
                    Advance();
                    type = TokenType.BITWISE_LEFT_SHIFT_EQ;
                }
            }
            else if (_currentCharacter == '!')
            {
                Advance();

                if (_currentCharacter != '-')
                {
                    return (null, new ExpectedCharacterError(positionStart, _position, "Expected '-' character."));
                }

                Advance();

                if (_currentCharacter != '-')
                {
                    return (null, new ExpectedCharacterError(positionStart, _position, "Expected '-' character."));
                }

                Advance();

                while (_currentCharacter != null && !(_text[_position.Idx] == '-' && _text[_position.Idx + 1] == '-' && _text[_position.Idx + 2] == '>'))
                {
                    Advance();
                }

                Advance(3);
                return (null, null);
            }

            return (new Token(type, null, positionStart, _position), null);
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
            else if (_currentCharacter == '>')
            {
                Advance();
                type = TokenType.BITWISE_RIGHT_SHIFT;

                if (_currentCharacter == '=')
                {
                    Advance();
                    type = TokenType.BITWISE_RIGHT_SHIFT_EQ;
                }
            }

            return new Token(type, null, positionStart, _position);
        }

        private void SkipComment()
        {
            Advance();
            while (_currentCharacter != null && _currentCharacter != '\n')
            {
                Advance();
            }
            Advance();
        }
    }
}
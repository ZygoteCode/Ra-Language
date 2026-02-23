using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Utilities;
using System.Globalization;
using System.Text;

namespace RaLanguage.Lexer
{
    public class Lexer
    {
        private readonly string _text;
        private Position _position;
        private char? _currentCharacter;

        private static readonly HashSet<string> Keywords = new()
        {
            "var", "and", "or", "not", "if", "elif", "else",
            "for", "to", "step", "while", "fn", "ret", "is",
            "continue", "break", "pass",
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
                if (" \t\r".Contains(_currentCharacter.Value))
                {
                    Advance();
                }
                else if (_currentCharacter == '#')
                {
                    SkipComment();
                }
                else if (";\n".Contains(_currentCharacter.Value))
                {
                    tokens.Add(new Token(TokenType.NEWLINE, null, _position));
                    Advance();
                }
                else if (Constants.DIGITS.Contains(_currentCharacter.Value))
                {
                    Token? tok = MakeNumber();
                    if (tok == null) return new(new List<Token>(), new InvalidSyntaxError(_position.Copy(), _position, "Invalid number format"));
                    tokens.Add(tok);
                }
                else if (Constants.LETTERS.Contains(_currentCharacter.Value))
                {
                    tokens.Add(MakeIdentifier());
                }
                else if (_currentCharacter == '"')
                {
                    tokens.Add(MakeString('"'));
                }
                else if (_currentCharacter == '\'')
                {
                    tokens.Add(MakeString('\''));
                }
                else if (_currentCharacter == '+')
                {
                    tokens.Add(MakePlus());
                }
                else if (_currentCharacter == '-')
                {
                    Token? tok = MakeMinus();
                    if (tok != null) tokens.Add(tok);
                }
                else if (_currentCharacter == '*')
                {
                    tokens.Add(MakeMul());
                }
                else if (_currentCharacter == '/')
                {
                    Token? tok = MakeDiv();
                    if (tok != null) tokens.Add(tok);
                }
                else if (_currentCharacter == '%')
                {
                    tokens.Add(MakeModulo());
                }
                else if (_currentCharacter == '^')
                {
                    tokens.Add(MakePow());
                }
                else if (_currentCharacter == '(')
                {
                    tokens.Add(new Token(TokenType.LPAREN, null, _position));
                    Advance();
                }
                else if (_currentCharacter == ')')
                {
                    tokens.Add(new Token(TokenType.RPAREN, null, _position));
                    Advance();
                }
                else if (_currentCharacter == '[')
                {
                    tokens.Add(new Token(TokenType.LSQUARE, null, _position));
                    Advance();
                }
                else if (_currentCharacter == ']')
                {
                    tokens.Add(new Token(TokenType.RSQUARE, null, _position));
                    Advance();
                }
                else if (_currentCharacter == ':')
                {
                    tokens.Add(new Token(TokenType.COLON, null, _position));
                    Advance();
                }
                else if (_currentCharacter == '{')
                {
                    tokens.Add(new Token(TokenType.LBRACKET, null, _position));
                    Advance();
                }
                else if (_currentCharacter == '}')
                {
                    tokens.Add(new Token(TokenType.RBRACKET, null, _position));
                    Advance();
                }
                else if (_currentCharacter == '!')
                {
                    tokens.Add(MakeNot());
                }
                else if (_currentCharacter == '~')
                {
                    tokens.Add(new Token(TokenType.BITWISE_NOT, null, _position));
                    Advance();
                }
                else if (_currentCharacter == '=')
                {
                    tokens.Add(MakeEquals());
                }
                else if (_currentCharacter == '<')
                {
                    (Token?, Error?) result = MakeLessThan();

                    if (result.Item2 != null)
                    {
                        return (new List<Token>(), result.Item2);
                    }

                    if (result.Item1 != null)
                    {
                        tokens.Add(result.Item1);
                    }
                }
                else if (_currentCharacter == '>')
                {
                    tokens.Add(MakeGreaterThan());
                }
                else if (_currentCharacter == ',')
                {
                    tokens.Add(new Token(TokenType.COMMA, null, _position));
                    Advance();
                }
                else if (_currentCharacter == '&')
                {
                    tokens.Add(MakeAnd());
                }
                else if (_currentCharacter == '|')
                {
                    tokens.Add(MakeOr());
                }
                else
                {
                    var positionStart = _position.Copy();
                    char charErr = _currentCharacter.Value;
                    Advance();
                    return (new List<Token>(), new IllegalCharacterError(positionStart, _position, $"'{charErr}'"));
                }
            }

            tokens.Add(new Token(TokenType.EOF, null, _position));
            return (tokens, null);
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

        private Token? MakeNumber()
        {
            var numStr = new StringBuilder();
            int dotCount = 0;
            var positionStart = _position.Copy();

            while (_currentCharacter != null &&
                   (Constants.DIGITS.Contains(_currentCharacter.Value) || _currentCharacter == '.' || _currentCharacter == '_' || _currentCharacter == 'e' || _currentCharacter == 'E'))
            {
                if (_currentCharacter == '_')
                {
                    Advance();
                    continue;
                }

                if (_currentCharacter == '.')
                {
                    if (dotCount == 1) break;
                    dotCount++;
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
                        return null;

                    while (_currentCharacter != null && Constants.DIGITS.Contains(_currentCharacter.Value))
                    {
                        numStr.Append(_currentCharacter);
                        Advance();
                    }
                    break;
                }
                else
                {
                    numStr.Append(_currentCharacter);
                    Advance();
                }
            }

            string finalNum = numStr.ToString();

            if (dotCount == 0 && !finalNum.Contains('e') && !finalNum.Contains('E'))
                return new Token(TokenType.INT, finalNum, positionStart, _position);
            else
                return new Token(TokenType.FLOAT, finalNum, positionStart, _position);
        }

        private Token MakeString(char stringCharacter)
        {
            var str = new StringBuilder();
            var positionStart = _position.Copy();
            bool escapeChar = false;
            Advance();

            var escapeCharacters = new Dictionary<char, char> { { 'n', '\n' }, { 't', '\t' } };

            while (_currentCharacter != null && (_currentCharacter != stringCharacter || escapeChar))
            {
                if (escapeChar)
                {
                    str.Append(escapeCharacters.ContainsKey(_currentCharacter.Value) ? escapeCharacters[_currentCharacter.Value] : _currentCharacter.Value);
                    escapeChar = false;
                }
                else
                {
                    if (_currentCharacter == '\\')
                    {
                        escapeChar = true;
                    }
                    else
                    {
                        str.Append(_currentCharacter);
                    }
                }
                Advance();
            }

            Advance();
            return new Token(TokenType.STRING, str.ToString(), positionStart, _position);
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
                if (_text[_position.Idx] == ' ' && _text[_position.Idx + 1] == 'n' && _text[_position.Idx + 2] == 'o' && _text[_position.Idx + 3] == 't')
                {
                    Advance(4);
                    return new Token(TokenType.NE, null, positionStart, _position);
                }

                return new Token(TokenType.EE, null, positionStart, _position);
            }

            TokenType type = Keywords.Contains(idString) ? TokenType.KEYWORD : TokenType.IDENTIFIER;
            return new Token(type, idString, positionStart, _position);
        }

        private Token? MakeMinus()
        {
            TokenType type = TokenType.MINUS;
            var positionStart = _position.Copy();
            Advance();

            if (_currentCharacter == '-')
            {
                Advance();

                while (_currentCharacter != null && _currentCharacter != '\n')
                {
                    Advance();
                }

                Advance();
                return null;
            }
            else if (_currentCharacter == '=')
            {
                Advance();
                type = TokenType.MINUS_EQ;
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
                return new Token(TokenType.NE, null, positionStart, _position);
            }

            return new Token(TokenType.KEYWORD, "not", positionStart, _position);
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

                return new Token(TokenType.KEYWORD, "and", positionStart, _position);
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

                return new Token(TokenType.KEYWORD, "or", positionStart, _position);
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
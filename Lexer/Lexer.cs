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
        private readonly string _fn;
        private readonly string _text;
        private Position _position;
        private char? _currentCharacter;

        private static readonly HashSet<string> Keywords = new()
        {
            "VAR", "AND", "OR", "NOT", "IF", "ELIF", "ELSE", "FOR",
            "TO", "STEP", "WHILE", "FUN", "THEN", "END", "RETURN", "CONTINUE", "BREAK"
        };

        public Lexer(string fn, string text)
        {
            _fn = fn;
            _text = text;
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
                    tokens.Add(MakeNumber());
                }
                else if (Constants.LETTERS.Contains(_currentCharacter.Value))
                {
                    tokens.Add(MakeIdentifier());
                }
                else if (_currentCharacter == '"')
                {
                    tokens.Add(MakeString());
                }
                else if (_currentCharacter == '+')
                {
                    tokens.Add(new Token(TokenType.PLUS, null, _position));
                    Advance();
                }
                else if (_currentCharacter == '-')
                {
                    tokens.Add(MakeMinusOrArrow());
                }
                else if (_currentCharacter == '*')
                {
                    tokens.Add(new Token(TokenType.MUL, null, _position));
                    Advance();
                }
                else if (_currentCharacter == '/')
                {
                    tokens.Add(new Token(TokenType.DIV, null, _position));
                    Advance();
                }
                else if (_currentCharacter == '^')
                {
                    tokens.Add(new Token(TokenType.POW, null, _position));
                    Advance();
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
                else if (_currentCharacter == '!')
                {
                    var (token, error) = MakeNotEquals();
                    if (error != null) return (new List<Token>(), error);
                    tokens.Add(token!);
                }
                else if (_currentCharacter == '=')
                {
                    tokens.Add(MakeEquals());
                }
                else if (_currentCharacter == '<')
                {
                    tokens.Add(MakeLessThan());
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

        private Token MakeNumber()
        {
            var numStr = new StringBuilder();
            int dotCount = 0;
            var positionStart = _position.Copy();

            while (_currentCharacter != null && (Constants.DIGITS.Contains(_currentCharacter.Value) || _currentCharacter == '.'))
            {
                if (_currentCharacter == '.')
                {
                    if (dotCount == 1) break;
                    dotCount++;
                }
                numStr.Append(_currentCharacter);
                Advance();
            }

            if (dotCount == 0)
                return new Token(TokenType.INT, int.Parse(numStr.ToString()), positionStart, _position);
            else
                return new Token(TokenType.FLOAT, double.Parse(numStr.ToString(), CultureInfo.InvariantCulture), positionStart, _position);
        }

        private Token MakeString()
        {
            var str = new StringBuilder();
            var positionStart = _position.Copy();
            bool escapeChar = false;
            Advance();

            var escapeCharacters = new Dictionary<char, char> { { 'n', '\n' }, { 't', '\t' } };

            while (_currentCharacter != null && (_currentCharacter != '"' || escapeChar))
            {
                if (escapeChar)
                {
                    str.Append(escapeCharacters.ContainsKey(_currentCharacter.Value) ? escapeCharacters[_currentCharacter.Value] : _currentCharacter.Value);
                    escapeChar = false;
                }
                else
                {
                    if (_currentCharacter == '\\')
                        escapeChar = true;
                    else
                        str.Append(_currentCharacter);
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
            TokenType type = Keywords.Contains(idString) ? TokenType.KEYWORD : TokenType.IDENTIFIER;
            return new Token(type, idString, positionStart, _position);
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
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
        private Position _pos;
        private char? _currentChar;

        private static readonly HashSet<string> Keywords = new()
        {
            "VAR", "AND", "OR", "NOT", "IF", "ELIF", "ELSE", "FOR",
            "TO", "STEP", "WHILE", "FUN", "THEN", "END", "RETURN", "CONTINUE", "BREAK"
        };

        public Lexer(string fn, string text)
        {
            _fn = fn;
            _text = text;
            _pos = new Position(-1, 0, -1, fn, text);
            Advance();
        }

        private void Advance()
        {
            _pos.Advance(_currentChar);
            _currentChar = _pos.Idx < _text.Length ? _text[_pos.Idx] : null;
        }

        public (List<Token> Tokens, Error? Error) MakeTokens()
        {
            var tokens = new List<Token>();

            while (_currentChar != null)
            {
                if (" \t".Contains(_currentChar.Value))
                {
                    Advance();
                }
                else if (_currentChar == '#')
                {
                    SkipComment();
                }
                else if (";\n".Contains(_currentChar.Value))
                {
                    tokens.Add(new Token(TokenType.NEWLINE, null, _pos));
                    Advance();
                }
                else if (Constants.DIGITS.Contains(_currentChar.Value))
                {
                    tokens.Add(MakeNumber());
                }
                else if (Constants.LETTERS.Contains(_currentChar.Value))
                {
                    tokens.Add(MakeIdentifier());
                }
                else if (_currentChar == '"')
                {
                    tokens.Add(MakeString());
                }
                else if (_currentChar == '+')
                {
                    tokens.Add(new Token(TokenType.PLUS, null, _pos));
                    Advance();
                }
                else if (_currentChar == '-')
                {
                    tokens.Add(MakeMinusOrArrow());
                }
                else if (_currentChar == '*')
                {
                    tokens.Add(new Token(TokenType.MUL, null, _pos));
                    Advance();
                }
                else if (_currentChar == '/')
                {
                    tokens.Add(new Token(TokenType.DIV, null, _pos));
                    Advance();
                }
                else if (_currentChar == '^')
                {
                    tokens.Add(new Token(TokenType.POW, null, _pos));
                    Advance();
                }
                else if (_currentChar == '(')
                {
                    tokens.Add(new Token(TokenType.LPAREN, null, _pos));
                    Advance();
                }
                else if (_currentChar == ')')
                {
                    tokens.Add(new Token(TokenType.RPAREN, null, _pos));
                    Advance();
                }
                else if (_currentChar == '[')
                {
                    tokens.Add(new Token(TokenType.LSQUARE, null, _pos));
                    Advance();
                }
                else if (_currentChar == ']')
                {
                    tokens.Add(new Token(TokenType.RSQUARE, null, _pos));
                    Advance();
                }
                else if (_currentChar == '!')
                {
                    var (token, error) = MakeNotEquals();
                    if (error != null) return (new List<Token>(), error);
                    tokens.Add(token!);
                }
                else if (_currentChar == '=')
                {
                    tokens.Add(MakeEquals());
                }
                else if (_currentChar == '<')
                {
                    tokens.Add(MakeLessThan());
                }
                else if (_currentChar == '>')
                {
                    tokens.Add(MakeGreaterThan());
                }
                else if (_currentChar == ',')
                {
                    tokens.Add(new Token(TokenType.COMMA, null, _pos));
                    Advance();
                }
                else
                {
                    var positionStart = _pos.Copy();
                    char charErr = _currentChar.Value;
                    Advance();
                    return (new List<Token>(), new IllegalCharacterError(positionStart, _pos, $"'{charErr}'"));
                }
            }

            tokens.Add(new Token(TokenType.EOF, null, _pos));
            return (tokens, null);
        }

        private Token MakeNumber()
        {
            var numStr = new StringBuilder();
            int dotCount = 0;
            var positionStart = _pos.Copy();

            while (_currentChar != null && (Constants.DIGITS.Contains(_currentChar.Value) || _currentChar == '.'))
            {
                if (_currentChar == '.')
                {
                    if (dotCount == 1) break;
                    dotCount++;
                }
                numStr.Append(_currentChar);
                Advance();
            }

            if (dotCount == 0)
                return new Token(TokenType.INT, int.Parse(numStr.ToString()), positionStart, _pos);
            else
                return new Token(TokenType.FLOAT, double.Parse(numStr.ToString(), CultureInfo.InvariantCulture), positionStart, _pos);
        }

        private Token MakeString()
        {
            var str = new StringBuilder();
            var positionStart = _pos.Copy();
            bool escapeChar = false;
            Advance();

            var escapeCharacters = new Dictionary<char, char> { { 'n', '\n' }, { 't', '\t' } };

            while (_currentChar != null && (_currentChar != '"' || escapeChar))
            {
                if (escapeChar)
                {
                    str.Append(escapeCharacters.ContainsKey(_currentChar.Value) ? escapeCharacters[_currentChar.Value] : _currentChar.Value);
                    escapeChar = false;
                }
                else
                {
                    if (_currentChar == '\\')
                        escapeChar = true;
                    else
                        str.Append(_currentChar);
                }
                Advance();
            }
            Advance();
            return new Token(TokenType.STRING, str.ToString(), positionStart, _pos);
        }

        private Token MakeIdentifier()
        {
            var idStr = new StringBuilder();
            var positionStart = _pos.Copy();

            while (_currentChar != null && (Constants.LETTERS_DIGITS.Contains(_currentChar.Value) || _currentChar == '_'))
            {
                idStr.Append(_currentChar);
                Advance();
            }

            string idString = idStr.ToString();
            TokenType type = Keywords.Contains(idString) ? TokenType.KEYWORD : TokenType.IDENTIFIER;
            return new Token(type, idString, positionStart, _pos);
        }

        private Token MakeMinusOrArrow()
        {
            TokenType type = TokenType.MINUS;
            var positionStart = _pos.Copy();
            Advance();

            if (_currentChar == '>')
            {
                Advance();
                type = TokenType.ARROW;
            }

            return new Token(type, null, positionStart, _pos);
        }

        private (Token?, Error?) MakeNotEquals()
        {
            var positionStart = _pos.Copy();
            Advance();

            if (_currentChar == '=')
            {
                Advance();
                return (new Token(TokenType.NE, null, positionStart, _pos), null);
            }

            Advance();
            return (null, new ExpectedCharacterError(positionStart, _pos, "'=' (after '!')"));
        }

        private Token MakeEquals()
        {
            TokenType type = TokenType.EQ;
            var positionStart = _pos.Copy();
            Advance();

            if (_currentChar == '=')
            {
                Advance();
                type = TokenType.EE;
            }
            return new Token(type, null, positionStart, _pos);
        }

        private Token MakeLessThan()
        {
            TokenType type = TokenType.LT;
            var positionStart = _pos.Copy();
            Advance();

            if (_currentChar == '=')
            {
                Advance();
                type = TokenType.LTE;
            }
            return new Token(type, null, positionStart, _pos);
        }

        private Token MakeGreaterThan()
        {
            TokenType type = TokenType.GT;
            var positionStart = _pos.Copy();
            Advance();

            if (_currentChar == '=')
            {
                Advance();
                type = TokenType.GTE;
            }
            return new Token(type, null, positionStart, _pos);
        }

        private void SkipComment()
        {
            Advance();
            while (_currentChar != null && _currentChar != '\n')
            {
                Advance();
            }
            Advance();
        }
    }
}
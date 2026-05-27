using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Types;

namespace RaLanguage.Parser
{
    public partial class Parser
    {
        private readonly List<Token> _tokens;
        private int _tokenIndex;
        private Token _currentToken;

        private readonly Stack<HashSet<string>> _genericScopes = new Stack<HashSet<string>>();

        private void PushGenericScope(IEnumerable<string> names)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in names) if (!string.IsNullOrEmpty(n)) set.Add(n);
            _genericScopes.Push(set);
        }

        private void PopGenericScope()
        {
            if (_genericScopes.Count > 0) _genericScopes.Pop();
        }

        private bool IsActiveGenericParam(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var scope in _genericScopes)
                if (scope.Contains(name)) return true;
            return false;
        }

        private static readonly HashSet<TokenType> AssignmentTokens = new()
        {
            TokenType.EQ,
            TokenType.PLUS_EQ,
            TokenType.MINUS_EQ,
            TokenType.MUL_EQ,
            TokenType.DIV_EQ,
            TokenType.MODULO_EQ,
            TokenType.BITWISE_AND_EQ,
            TokenType.BITWISE_OR_EQ,
            TokenType.BITWISE_LEFT_SHIFT_EQ,
            TokenType.BITWISE_RIGHT_SHIFT_EQ,
            TokenType.BITWISE_LOGICAL_LEFT_SHIFT_EQ,
            TokenType.BITWISE_LOGICAL_RIGHT_SHIFT_EQ,
            TokenType.BITWISE_ROTATE_LEFT_EQ,
            TokenType.BITWISE_ROTATE_RIGHT_EQ,
            TokenType.POW_EQ,
            TokenType.AND_EQ,
            TokenType.OR_EQ,
            TokenType.NULL_COALESCE_EQ,
        };

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
            _tokenIndex = -1;
            Advance();
        }

        private Token Advance()
        {
            _tokenIndex++;
            UpdateCurrentToken();
            return _currentToken;
        }

        private Token Reverse(int amount = 1)
        {
            _tokenIndex -= amount;
            UpdateCurrentToken();
            return _currentToken;
        }

        private void UpdateCurrentToken()
        {
            if (_tokenIndex >= 0 && _tokenIndex < _tokens.Count)
                _currentToken = _tokens[_tokenIndex];
        }

        public ParseResult Parse()
        {
            var res = ParseStatements();
            if (res.Error == null && _currentToken.Type != TokenType.EOF)
            {
                res.Failure(ParserDiagnostics.TrailingToken(_currentToken));
            }
            return new ParseResult(res.Node, res.Diagnostics);
        }

        internal static string DescribeToken(Token token)
        {
            switch (token.Type)
            {
                case TokenType.EOF: return "end of input";
                case TokenType.NEWLINE: return "newline";
                case TokenType.IDENTIFIER:
                    return token.Value != null ? $"identifier '{token.Value}'" : "identifier";
                case TokenType.INT:
                case TokenType.FLOAT:
                    return token.Value != null ? $"number '{token.Value}'" : "number literal";
                case TokenType.STRING_TEXT:
                    return "string literal";
                case TokenType.KEYWORD:
                    return token.Value != null ? $"keyword '{token.Value.ToString()!.ToLowerInvariant()}'" : "keyword";
                default:
                    return token.Value != null ? $"'{token.Value}'" : $"'{token.Type}'";
            }
        }


        private bool IsAssignmentToken(TokenType type)
        {
            return AssignmentTokens.Contains(type);
        }


        private bool IsTryUnwrapNext()
        {
            int idx = _tokenIndex + 1;
            if (idx >= _tokens.Count) return true;
            var t = _tokens[idx];
            switch (t.Type)
            {
                case TokenType.NEWLINE:
                case TokenType.EOF:
                case TokenType.COMMA:
                case TokenType.RPAREN:
                case TokenType.RSQUARE:
                case TokenType.RBRACKET:
                case TokenType.ARROW:
                case TokenType.ARROW_RIGHT:
                case TokenType.PIPE_FORWARD:
                case TokenType.DOT:
                case TokenType.QUESTION_MARK:
                case TokenType.NULL_COALESCE:
                case TokenType.SPREAD:
                case TokenType.DOUBLE_DOT:
                case TokenType.DOUBLE_DOT_EQ:
                case TokenType.PLUS:
                case TokenType.MINUS:
                case TokenType.MUL:
                case TokenType.DIV:
                case TokenType.MODULO:
                case TokenType.POW:
                case TokenType.EE:
                case TokenType.NE:
                case TokenType.LT:
                case TokenType.GT:
                case TokenType.LTE:
                case TokenType.GTE:
                case TokenType.STRICT_EE:
                case TokenType.STRICT_NE:
                case TokenType.BITWISE_AND:
                case TokenType.BITWISE_OR:
                case TokenType.BITWISE_LEFT_SHIFT:
                case TokenType.BITWISE_RIGHT_SHIFT:
                case TokenType.BITWISE_LOGICAL_LEFT_SHIFT:
                case TokenType.BITWISE_LOGICAL_RIGHT_SHIFT:
                case TokenType.BITWISE_ROTATE_LEFT:
                case TokenType.BITWISE_ROTATE_RIGHT:
                    return true;
                case TokenType.KEYWORD:
                    // Allow specific postfix-friendly keywords (operators / scope-end).
                    if (t.Value is Lexer.Tokens.Keyword kw)
                    {
                        switch (kw)
                        {
                            case Lexer.Tokens.Keyword.As:
                            case Lexer.Tokens.Keyword.And:
                            case Lexer.Tokens.Keyword.Or:
                            case Lexer.Tokens.Keyword.In:
                            case Lexer.Tokens.Keyword.Is:
                                return true;
                        }
                    }
                    return false;
                default:
                    return false;
            }
        }


        private void SkipNewlines(ParserResult res)
        {
            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }
        }


        private bool IsOperatorToken(TokenType type)
        {
            return type switch
            {
                TokenType.PLUS or
                TokenType.MINUS or
                TokenType.MUL or
                TokenType.DIV or
                TokenType.MODULO or
                TokenType.POW or
                TokenType.EE or
                TokenType.NE or
                TokenType.LT or
                TokenType.GT or
                TokenType.LTE or
                TokenType.GTE or
                TokenType.BITWISE_AND or
                TokenType.BITWISE_OR or
                TokenType.BITWISE_LEFT_SHIFT or
                TokenType.BITWISE_RIGHT_SHIFT or
                TokenType.BITWISE_LOGICAL_LEFT_SHIFT or
                TokenType.BITWISE_LOGICAL_RIGHT_SHIFT or
                TokenType.BITWISE_ROTATE_LEFT or
                TokenType.BITWISE_ROTATE_RIGHT => true,
                _ => false
            };
        }


        // Kept for legacy compatibility but no longer used by ParseCastExpression;
        // `or` and `and` now live in their own precedence bands.
        private static readonly (TokenType, Keyword?)[] s_opsLogical = new (TokenType, Keyword?)[]
        {
            (TokenType.KEYWORD, Keyword.And),
            (TokenType.KEYWORD, Keyword.Or),
        };
        private static readonly (TokenType, Keyword?)[] s_opsLogicalOr = new (TokenType, Keyword?)[]
        {
            (TokenType.KEYWORD, Keyword.Or),
        };
        private static readonly (TokenType, Keyword?)[] s_opsLogicalAnd = new (TokenType, Keyword?)[]
        {
            (TokenType.KEYWORD, Keyword.And),
        };
        private static readonly (TokenType, Keyword?)[] s_opsBitwiseOr = new (TokenType, Keyword?)[]
        {
            (TokenType.BITWISE_OR, null),
        };
        private static readonly (TokenType, Keyword?)[] s_opsBitwiseAnd = new (TokenType, Keyword?)[]
        {
            (TokenType.BITWISE_AND, null),
        };
        private static readonly (TokenType, Keyword?)[] s_opsComparison = new (TokenType, Keyword?)[]
        {
            (TokenType.EE, null), (TokenType.NE, null), (TokenType.LT, null),
            (TokenType.GT, null), (TokenType.LTE, null), (TokenType.GTE, null),
            (TokenType.STRICT_EE, null), (TokenType.STRICT_NE, null),
            (TokenType.KEYWORD, Keyword.In), (TokenType.KEYWORD, Keyword.NotIn),
        };
        private static readonly (TokenType, Keyword?)[] s_opsShift = new (TokenType, Keyword?)[]
        {
            (TokenType.BITWISE_LEFT_SHIFT, null),
            (TokenType.BITWISE_RIGHT_SHIFT, null),
            (TokenType.BITWISE_LOGICAL_LEFT_SHIFT, null),
            (TokenType.BITWISE_LOGICAL_RIGHT_SHIFT, null),
            (TokenType.BITWISE_ROTATE_LEFT, null),
            (TokenType.BITWISE_ROTATE_RIGHT, null),
        };
        private static readonly (TokenType, Keyword?)[] s_opsArith = new (TokenType, Keyword?)[]
        {
            (TokenType.PLUS, null), (TokenType.MINUS, null),
        };
        private static readonly (TokenType, Keyword?)[] s_opsTerm = new (TokenType, Keyword?)[]
        {
            (TokenType.MUL, null), (TokenType.DIV, null), (TokenType.MODULO, null),
        };
        private static readonly (TokenType, Keyword?)[] s_opsPow = new (TokenType, Keyword?)[]
        {
            (TokenType.POW, null),
        };

        private ParserResult ParseBinaryOperation(Func<ParserResult> funcA, (TokenType, Keyword?)[] ops, Func<ParserResult>? funcB = null)
        {
            if (funcB == null) funcB = funcA;
            var res = new ParserResult();
            var left = res.Register(funcA());
            if (res.Error != null) return res;

            while (true)
            {
                var curType = _currentToken.Type;
                object? curVal = _currentToken.Value;
                bool matched = false;
                for (int i = 0; i < ops.Length; i++)
                {
                    var (type, kw) = ops[i];
                    if (curType != type) continue;
                    if (kw == null || (curVal is Keyword k && k == kw))
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched) break;
                var opTok = _currentToken;
                res.RegisterAdvancement();
                Advance();
                var right = res.Register(funcB());
                if (res.Error != null) return res;
                left = new BinaryOperationNode(left, opTok, right);
            }
            return res.Success(left);
        }

        private void SkipToNextStatement(ParserResult res)
        {
            // Advance through the current broken statement until a statement-terminator
            // is reached. We deliberately stop *at* the NEWLINE/RBRACKET/etc rather than
            // consuming it, so the outer ParseStatements loop's "next statement requires
            // a newline" check can still observe the separator and continue iterating
            // — this is what enables multi-error reporting across several statements.
            while (_currentToken.Type != TokenType.EOF &&
                   _currentToken.Type != TokenType.NEWLINE &&
                   _currentToken.Type != TokenType.RBRACKET &&
                   _currentToken.Type != TokenType.RPAREN &&
                   _currentToken.Type != TokenType.RSQUARE)
            {
                res.RegisterAdvancement();
                Advance();
            }
        }

    }

    internal static class AnnotationAttacher
    {
        public static void Attach(AstNode? target, List<AnnotationApplicationNode>? annotations)
        {
            if (target == null || annotations == null || annotations.Count == 0) return;
            target.Annotations ??= new List<AnnotationApplicationNode>();
            target.Annotations.AddRange(annotations);
        }
    }
}

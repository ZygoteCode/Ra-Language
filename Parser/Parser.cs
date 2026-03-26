using RaLanguage.Errors.Types;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Parser
{
    public class Parser
    {
        private readonly List<Token> _tokens;
        private int _tokenIndex;
        private Token _currentToken;

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

        public ParserResult Parse()
        {
            var res = ParseStatements();
            if (res.Error == null && _currentToken.Type != TokenType.EOF)
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentToken.PositionStart, _currentToken.PositionEnd,
                    "Token cannot appear after previous tokens"
                ));
            }
            return res;
        }

        private ParserResult ParseStatements()
        {
            var res = new ParserResult();
            var statements = new List<AstNode>();
            var positionStart = _currentToken.PositionStart.Copy();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var statement = res.Register(ParseStatement());
            if (res.Error != null) return res;
            statements.Add(statement);

            bool moreStatements = true;

            while (true)
            {
                int newlineCount = 0;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                    newlineCount++;
                }

                if (newlineCount == 0)
                {
                    moreStatements = false;
                }

                if (!moreStatements)
                {
                    break;
                }

                AstNode? stmt = null;

                if (_currentToken.Type == TokenType.LBRACKET)
                {
                    Position _positionStart = _currentToken.PositionStart.Copy();
                    res.RegisterAdvancement();
                    Advance();

                    List<AstNode?> scopeStatements = new List<AstNode?>();

                    while (true)
                    {
                        int _newLineCount = 0;

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            _newLineCount++;
                        }

                        if (_newLineCount == 0)
                        {
                            break;
                        }

                        scopeStatements.Add(res.TryRegister(ParseStatements()));

                        if (res.Error != null)
                        {
                            return res;
                        }

                        if (_currentToken.Type == TokenType.RBRACKET)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            break;
                        }
                    }

                    statements.AddRange(new ListNode(scopeStatements, _positionStart, _currentToken.PositionStart.Copy(), true));
                    continue;
                }
                else
                {
                    stmt = res.TryRegister(ParseStatement());

                    if (res.Error != null)
                    {
                        return res;
                    }
                }

                if (stmt == null)
                {
                    Reverse(res.ToReverseCount);
                    moreStatements = false;
                    continue;
                }

                statements.Add(stmt);
            }

            return res.Success(new ListNode(
                statements,
                positionStart,
                _currentToken.PositionEnd.Copy()
            ));
        }

        private ParserResult ParseStatement()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart.Copy();

            if (_currentToken.Type == TokenType.KEYWORD)
            {
                switch (_currentToken.Value)
                {
                    case Keyword.Ret:
                        res.RegisterAdvancement();
                        Advance();
                        var expr = res.TryRegister(ParseExpression());
                        if (expr == null) Reverse(res.ToReverseCount);
                        return res.Success(new ReturnNode(expr, positionStart, _currentToken.PositionStart.Copy()));
                    case Keyword.Yield:
                        res.RegisterAdvancement();
                        Advance();
                        var expr2 = res.Register(ParseExpression());
                        if (res.Error != null) Reverse(res.ToReverseCount);
                        return res.Success(new YieldNode(expr2, positionStart, _currentToken.PositionStart.Copy()));
                    case Keyword.Continue:
                        res.RegisterAdvancement();
                        Advance();
                        return res.Success(new ContinueNode(positionStart, _currentToken.PositionStart.Copy()));
                    case Keyword.Break:
                        res.RegisterAdvancement();
                        Advance();
                        return res.Success(new BreakNode(positionStart, _currentToken.PositionStart.Copy()));
                    case Keyword.Pass:
                        res.RegisterAdvancement();
                        Advance();
                        return res.Success(new PassNode(positionStart, _currentToken.PositionStart.Copy()));
                    case Keyword.Del:
                        res.RegisterAdvancement();
                        Advance();
                        List<Token> tokens = new List<Token>();

                        while (_currentToken.Type == TokenType.IDENTIFIER)
                        {
                            tokens.Add(_currentToken);
                            res.RegisterAdvancement();
                            Advance();

                            if (_currentToken.Type == TokenType.COMMA)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }
                        }

                        return res.Success(new VariableDeleteNode(tokens));
                    case Keyword.Goto:
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                        {
                            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier"));
                        }

                        Token varName = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        return res.Success(new GotoNode(positionStart, varName));
                }
            }

            if (_currentToken.Type == TokenType.IDENTIFIER && _tokens[_tokenIndex + 1].Type == TokenType.COLON)
            {
                Token varName = _currentToken;

                res.RegisterAdvancement();
                Advance();

                res.RegisterAdvancement();
                Advance();

                var statements = res.Register(ParseStatements());

                if (res.Error != null)
                {
                    return res;
                }

                return res.Success(new LabelNode(varName, statements));
            }

            var expression = res.Register(ParseExpression());
            if (res.Error != null)
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentToken.PositionStart, _currentToken.PositionEnd,
                    "Expected 'return', 'continue', 'break', 'var', 'if', 'for', 'while', 'fn', int, float, identifier, '+', '-', '(', '[' or 'not'"
                ));
            }
            return res.Success(expression);
        }

        private bool IsAssignmentToken(TokenType type)
        {
            return AssignmentTokens.Contains(type);
        }

        private ParserResult ParseRetryStatement()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart.Copy();

            res.RegisterAdvancement();
            Advance();

            if (!_currentToken.Matches(Keyword.For))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'for' after 'retry'"));

            res.RegisterAdvancement();
            Advance();

            var countNode = res.Register(ParseExpression());
            if (res.Error != null) return res;

            if (!_currentToken.Matches(Keyword.Times))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'times' after retry count"));

            res.RegisterAdvancement();
            Advance();

            AstNode? delayNode = null;
            if (_currentToken.Matches(Keyword.Delay))
            {
                res.RegisterAdvancement();
                Advance();

                delayNode = res.Register(ParseExpression());
                if (res.Error != null) return res;
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '{'"));

            res.RegisterAdvancement();
            Advance();

            var bodyNode = res.Register(ParseStatements());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}' after retry body"));

            res.RegisterAdvancement();
            Advance();

            AstNode? elseNode = null;
            if (_currentToken.Matches(Keyword.Else))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.LBRACKET)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '{' after 'else'"));

                res.RegisterAdvancement();
                Advance();

                elseNode = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}' after retry else-body"));

                res.RegisterAdvancement();
                Advance();
            }

            var node = new RetryNode(countNode, bodyNode, delayNode, elseNode);
            node.PositionStart = positionStart;
            node.PositionEnd = elseNode?.PositionEnd ?? bodyNode.PositionEnd;

            return res.Success(node);
        }

        private TypeDescriptor? ParseType(ParserResult res)
        {
            if (!(_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.KEYWORD))
            {
                return null;
            }

            var baseName = _currentToken.Value?.ToString() ?? _currentToken.ToString();

            res.RegisterAdvancement();
            Advance();

            var genericArgs = new List<TypeDescriptor>();

            if (_currentToken.Type == TokenType.LT)
            {
                res.RegisterAdvancement();
                Advance();

                while (true)
                {
                    var argType = ParseType(res);
                    if (argType == null)
                    {
                        return null;
                    }

                    genericArgs.Add(argType);

                    if (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();
                        continue;
                    }

                    if (_currentToken.Type != TokenType.GT)
                    {
                        return null;
                    }

                    res.RegisterAdvancement();
                    Advance();
                    break;
                }
            }

            if (!string.IsNullOrEmpty(baseName) && char.IsUpper(baseName[0]) && genericArgs.Count == 0)
            {
                return TypeDescriptor.TypeParameter(baseName);
            }

            return new TypeDescriptor(baseName, genericArgs);
        }

        private ParserResult ParseExpression()
        {
            var res = new ParserResult();

            if (_currentToken.Matches(Keyword.Var) || _currentToken.Matches(Keyword.Const) || _currentToken.Matches(Keyword.Final) || _currentToken.Matches(Keyword.Let))
            {
                var variableDeclaration = res.Register(ParseVariableDeclaration());
                if (res.Error != null) return res;
                return res.Success(variableDeclaration);
            }
            else if (_currentToken.Matches(Keyword.TypeOf))
            {
                res.RegisterAdvancement();
                Advance();
                var expr = res.Register(ParseExpression());

                if (res.Error != null)
                {
                    return res;
                }

                return res.Success(new TypeofNode(expr));
            }
            else if (_currentToken.Matches(Keyword.NameOf))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type == TokenType.LPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                    {
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier"));
                    }

                    Token tok = _currentToken;
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type != TokenType.RPAREN)
                    {
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ')'"));
                    }

                    res.RegisterAdvancement();
                    Advance();

                    return res.Success(new NameofNode(tok));
                }
                else
                {
                    if (_currentToken.Type != TokenType.IDENTIFIER)
                    {
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier"));
                    }

                    Token tok = _currentToken;

                    res.RegisterAdvancement();
                    Advance();

                    return res.Success(new NameofNode(tok));
                }
            }

            var leftNode = res.Register(ParseBinaryOperation(ParseBitwiseOrExpression, new List<(TokenType, Keyword?)> { (TokenType.KEYWORD, Keyword.And), (TokenType.KEYWORD, Keyword.Or) }));

            if (res.Error == null)
            {
                while (_currentToken.Matches(Keyword.As))
                {
                    res.RegisterAdvancement();
                    Advance();

                    var parsedType = ParseType(res);
                    if (parsedType == null)
                    {
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected type after 'as'"));
                    }

                    var castNode = new CastNode(leftNode, parsedType);
                    castNode.PositionStart = leftNode.PositionStart;
                    castNode.PositionEnd = _currentToken.PositionEnd.Copy();
                    leftNode = castNode;
                }
            }

            if (res.Error != null)
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentToken.PositionStart, _currentToken.PositionEnd,
                    "Expected 'var', 'if', 'for', 'while', 'fn', int, float, identifier, '+', '-', '(', '[' or 'not'"
                ));
            }

            if (_currentToken.Type == TokenType.QUESTION_MARK)
            {
                var qTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                var trueExpr = res.Register(ParseExpression());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.COLON)
                {
                    return res.Failure(new InvalidSyntaxError(
                        _currentToken.PositionStart, _currentToken.PositionEnd,
                        "Expected ':' after expression in ternary operator"
                    ));
                }

                res.RegisterAdvancement();
                Advance();

                var falseExpr = res.Register(ParseExpression());
                if (res.Error != null) return res;

                leftNode = new TernaryNode(leftNode, trueExpr, falseExpr, qTok);
            }

            if (IsAssignmentToken(_currentToken.Type))
            {
                Token assignmentToken = _currentToken;
                res.RegisterAdvancement();
                Advance();

                var rightNode = res.Register(ParseExpression());
                if (res.Error != null) return res;

                if (leftNode.NodeType == AstNodeType.VariableAccess)
                {
                    VariableAccessNode varAccess = (VariableAccessNode)leftNode;
                    return res.Success(new VariableAssignmentNode(varAccess.VarNameTok, assignmentToken, rightNode));
                }
                else if (leftNode.NodeType == AstNodeType.ListAccess)
                {
                    ListAccessNode listAccess = (ListAccessNode)leftNode;
                    return res.Success(new ListAssignmentNode(listAccess, assignmentToken, rightNode));
                }
                else if (leftNode.NodeType == AstNodeType.MemberAccess)
                {
                    MemberAccessNode memberAccess = (MemberAccessNode)leftNode;
                    return res.Success(new MemberAssignmentNode(memberAccess, assignmentToken, rightNode));
                }
                else
                {
                    return res.Failure(new InvalidSyntaxError(
                        leftNode.PositionStart, leftNode.PositionEnd,
                        "Invalid assignment target. You can only assign values to variables or list elements."
                    ));
                }
            }

            return res.Success(leftNode);
        }

        private ParserResult ParseBitwiseOrExpression()
        {
            return ParseBinaryOperation(ParseBitwiseAndExpression, new List<(TokenType, Keyword?)> { (TokenType.BITWISE_OR, null) });
        }

        private ParserResult ParseBitwiseAndExpression()
        {
            return ParseBinaryOperation(ParseComparisonExpression, new List<(TokenType, Keyword?)> { (TokenType.BITWISE_AND, null) });
        }

        private ParserResult ParseRangeExpression()
        {
            var res = new ParserResult();

            var start = res.Register(ParseArithmeticExpression());
            if (res.Error != null) return res;

            if (_currentToken.Type == TokenType.DOUBLE_DOT || _currentToken.Type == TokenType.DOUBLE_DOT_EQ)
            {
                var opTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                var end = res.Register(ParseArithmeticExpression());
                if (res.Error != null) return res;

                AstNode? step = null;

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    step = res.Register(ParseArithmeticExpression());
                    if (res.Error != null) return res;
                }

                return res.Success(new RangeNode(start, end, opTok, step));
            }

            return res.Success(start);
        }

        private ParserResult ParseNullCoalescing()
        {
            var res = new ParserResult();
            var left = res.Register(ParseShiftExpression());
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NULL_COALESCE)
            {
                var opTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                var right = res.Register(ParseShiftExpression());
                if (res.Error != null) return res;

                left = new NullCoalescingNode(left, right, opTok);
            }

            return res.Success(left);
        }

        private ParserResult ParseComparisonExpression()
        {
            var res = new ParserResult();

            if (_currentToken.Matches(Keyword.Not) || _currentToken.Type == TokenType.BITWISE_NOT)
            {
                var opTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                var node = res.Register(ParseComparisonExpression());
                if (res.Error != null) return res;
                return res.Success(new UnaryOperationNode(opTok, node));
            }

            var b_node = res.Register(ParseBinaryOperation(
                ParseNullCoalescing,
                new List<(TokenType, Keyword?)>
                {
                    (TokenType.EE, null), (TokenType.NE, null), (TokenType.LT, null),
                    (TokenType.GT, null), (TokenType.LTE, null), (TokenType.GTE, null),
                    (TokenType.STRICT_EE, null), (TokenType.STRICT_NE, null),
                    (TokenType.KEYWORD, Keyword.In), (TokenType.KEYWORD, Keyword.NotIn)
                }
            ));

            if (res.Error != null)
            {
                return res.Failure(new InvalidSyntaxError(
                   _currentToken.PositionStart, _currentToken.PositionEnd,
                   "Expected int, float, identifier, '+', '-', '(', '[', 'if', 'for', 'while', 'fn' or 'not'"
               ));
            }

            return res.Success(b_node);
        }

        private ParserResult ParseShiftExpression()
        {
            return ParseBinaryOperation(ParseRangeExpression, new List<(TokenType, Keyword?)>
            {
                (TokenType.BITWISE_LEFT_SHIFT, null),
                (TokenType.BITWISE_RIGHT_SHIFT, null)
            });
        }

        private ParserResult ParseArithmeticExpression()
        {
            return ParseBinaryOperation(ParseTerm, new List<(TokenType, Keyword?)> { (TokenType.PLUS, null), (TokenType.MINUS, null) });
        }

        private ParserResult ParseTerm()
        {
            return ParseBinaryOperation(ParseFactor, new List<(TokenType, Keyword?)>
            {
                (TokenType.MUL, null),
                (TokenType.DIV, null),
                (TokenType.MODULO, null)
            });
        }

        private ParserResult ParseFactor()
        {
            var res = new ParserResult();
            var tok = _currentToken;

            if (tok.Type == TokenType.DOUBLE_PLUS || tok.Type == TokenType.DOUBLE_MINUS)
            {
                res.RegisterAdvancement();
                Advance();
                var factor = res.Register(ParseFactor());
                if (res.Error != null) return res;
                return res.Success(new UnaryOperationNode(tok, factor, isLeft: true));
            }

            if (tok.Type == TokenType.PLUS || tok.Type == TokenType.MINUS)
            {
                res.RegisterAdvancement();
                Advance();
                var factor = res.Register(ParseFactor());
                if (res.Error != null) return res;
                return res.Success(new UnaryOperationNode(tok, factor, isLeft: true));
            }

            return ParsePower();
        }

        private ParserResult ParsePower()
        {
            return ParseBinaryOperation(ParseCall, new List<(TokenType, Keyword?)> { (TokenType.POW, null) }, ParseFactor);
        }

        private ParserResult ParseCall()
        {
            var res = new ParserResult();

            var atom = res.Register(ParseAtom());
            if (res.Error != null) return res;

            var resultNode = atom;

            List<TypeDescriptor?>? genericTypeArgs = null;
            if (_currentToken.Type == TokenType.LT)
            {
                int startIndex = _tokenIndex;
                var dummyRes = new ParserResult();
                bool isGenericCall = true;
                var tempArgs = new List<TypeDescriptor?>();

                dummyRes.RegisterAdvancement();
                Advance();

                while (true)
                {
                    var parsedType = ParseType(dummyRes);
                    if (parsedType == null)
                    {
                        isGenericCall = false;
                        break;
                    }

                    tempArgs.Add(parsedType);

                    if (_currentToken.Type == TokenType.COMMA)
                    {
                        dummyRes.RegisterAdvancement();
                        Advance();
                        continue;
                    }

                    if (_currentToken.Type != TokenType.GT)
                    {
                        isGenericCall = false;
                        break;
                    }

                    dummyRes.RegisterAdvancement();
                    Advance();
                    break;
                }

                if (isGenericCall && _currentToken.Type == TokenType.LPAREN)
                {
                    genericTypeArgs = tempArgs;
                    int totalAdvances = _tokenIndex - startIndex;
                    for (int i = 0; i < totalAdvances; i++)
                    {
                        res.RegisterAdvancement();
                    }
                }
                else
                {
                    _tokenIndex = startIndex;
                    UpdateCurrentToken();
                }
            }

            while (_currentToken.Type == TokenType.LPAREN
                || _currentToken.Type == TokenType.LSQUARE
                || _currentToken.Type == TokenType.DOT
                || _currentToken.Matches(Keyword.Not))
            {
                if (_currentToken.Matches(Keyword.Not))
                {
                    var opTok = _currentToken;

                    res.RegisterAdvancement();
                    Advance();

                    resultNode = new UnaryOperationNode(opTok, resultNode, isLeft: false);
                }
                else if (_currentToken.Type == TokenType.LPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();
                    var argNodes = new List<ArgumentNode>();

                    if (_currentToken.Type == TokenType.RPAREN)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                    else
                    {
                        while (true)
                        {
                            if (_currentToken.Type == TokenType.IDENTIFIER && _tokens[_tokenIndex + 1].Type == TokenType.COLON)
                            {
                                var nameTok = _currentToken;
                                res.RegisterAdvancement();
                                Advance();
                                res.RegisterAdvancement();
                                Advance();

                                var expr = res.Register(ParseExpression());
                                if (res.Error != null) return res;
                                argNodes.Add(new ArgumentNode(nameTok, expr));
                            }
                            else
                            {
                                var expr = res.Register(ParseExpression());
                                if (res.Error != null) return res;
                                argNodes.Add(new ArgumentNode(null, expr));
                            }

                            if (_currentToken.Type == TokenType.COMMA)
                            {
                                res.RegisterAdvancement();
                                Advance();

                                if (_currentToken.Type == TokenType.RPAREN) break;
                                continue;
                            }

                            break;
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ',' or ')'"));

                        res.RegisterAdvancement();
                        Advance();
                    }

                    resultNode = new FunctionCallNode(resultNode, argNodes, genericTypeArgs);
                    genericTypeArgs = null;
                }
                else if (_currentToken.Type == TokenType.LSQUARE)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var indexNode = res.Register(ParseExpression());
                    if (res.Error != null) return res;

                    if (_currentToken.Type != TokenType.RSQUARE)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ']'"));

                    var rBracketEndPos = _currentToken.PositionEnd.Copy();
                    res.RegisterAdvancement();
                    Advance();

                    resultNode = new ListAccessNode(resultNode, indexNode, resultNode.PositionStart, rBracketEndPos);
                }
                else if (_currentToken.Type == TokenType.DOT)
                {
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected member name after '.'"));

                    Token memberTok = _currentToken;
                    res.RegisterAdvancement();
                    Advance();

                    resultNode = new MemberAccessNode(resultNode, memberTok);
                }
            }

            return res.Success(resultNode);
        }

        private ParserResult ParseAtom()
        {
            var res = new ParserResult();
            var tok = _currentToken;

            switch (tok.Type)
            {
                case TokenType.INT:
                case TokenType.FLOAT:
                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(new NumberNode(tok));
                case TokenType.STRING_TEXT:
                    var parts = new List<AstNode>();
                    var posStart = tok.PositionStart.Copy();
                    var posEnd = tok.PositionEnd.Copy();

                    while (_currentToken.Type == TokenType.STRING_TEXT || _currentToken.Type == TokenType.INTERP_START)
                    {
                        if (_currentToken.Type == TokenType.STRING_TEXT)
                        {
                            var textTok = _currentToken;
                            res.RegisterAdvancement();
                            Advance();
                            parts.Add(new StringTextNode(textTok.Value?.ToString() ?? "", textTok.PositionStart.Copy(), textTok.PositionEnd.Copy()));
                            posEnd = textTok.PositionEnd.Copy();
                        }
                        else if (_currentToken.Type == TokenType.INTERP_START)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            var expr2 = res.Register(ParseExpression());
                            if (res.Error != null) return res;

                            if (_currentToken.Type != TokenType.INTERP_END)
                            {
                                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}' to close interpolation"));
                            }

                            res.RegisterAdvancement();
                            Advance();

                            parts.Add(expr2);
                            posEnd = expr2.PositionEnd.Copy();
                        }
                    }

                    return res.Success(new StringNode(parts, posStart, posEnd));
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Null:
                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(new NullNode(tok));
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Self:
                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(new SelfNode(tok.PositionStart, tok.PositionEnd));
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.True:
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.False:
                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(new BooleanNode(tok));
                case TokenType.IDENTIFIER:
                    res.RegisterAdvancement();
                    Advance();
                    var varNode = new VariableAccessNode(tok);

                    if (_currentToken.Type == TokenType.DOUBLE_PLUS || _currentToken.Type == TokenType.DOUBLE_MINUS)
                    {
                        var opTok = _currentToken;
                        res.RegisterAdvancement();
                        Advance();
                        return res.Success(new UnaryOperationNode(opTok, varNode, isLeft: false));
                    }

                    return res.Success(varNode);
                case TokenType.LPAREN:
                    res.RegisterAdvancement();
                    Advance();

                    var positionStart = tok.PositionStart.Copy();

                    if (_currentToken.Type == TokenType.RPAREN)
                    {
                        res.RegisterAdvancement();
                        Advance();
                        return res.Success(new TupleNode(new List<AstNode>(), positionStart, _currentToken.PositionEnd.Copy()));
                    }

                    var firstExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;

                    if (_currentToken.Type == TokenType.COMMA)
                    {
                        var elementNodes = new List<AstNode> { firstExpr };

                        while (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            if (_currentToken.Type == TokenType.RPAREN)
                            {
                                break;
                            }

                            var nextExpr = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                            elementNodes.Add(nextExpr);
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                        {
                            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ')' after tuple"));
                        }

                        var tupleEndPos = _currentToken.PositionEnd.Copy();
                        res.RegisterAdvancement();
                        Advance();

                        return res.Success(new TupleNode(elementNodes, positionStart, tupleEndPos));
                    }
                    else
                    {
                        if (_currentToken.Type == TokenType.RPAREN)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            return res.Success(firstExpr);
                        }

                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ',' or ')'"));
                    }
                case TokenType.LSQUARE:
                    var listExpr = res.Register(ParseListExpression());
                    if (res.Error != null) return res;
                    return res.Success(listExpr);
                case TokenType.LBRACKET:
                    var setExpr = res.Register(ParseSetExpression());
                    if (res.Error != null) return res;
                    return res.Success(setExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.If:
                    var ifExpr = res.Register(ParseIfExpression());
                    if (res.Error != null) return res;
                    return res.Success(ifExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Retry:
                    var retryExpr = res.Register(ParseRetryStatement());
                    if (res.Error != null) return res;
                    return res.Success(retryExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.For:
                    var forExpr = res.Register(ParseForExpression());
                    if (res.Error != null) return res;
                    return res.Success(forExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.While:
                    var whileExpr = res.Register(ParseWhileExpression());
                    if (res.Error != null) return res;
                    return res.Success(whileExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Fn:
                    var funcDef = res.Register(ParseFunctionDefinition());
                    if (res.Error != null) return res;
                    return res.Success(funcDef);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Do:
                    var doWhileExpr = res.Register(ParseDoWhileExpression());
                    if (res.Error != null) return res;
                    return res.Success(doWhileExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Switch:
                    var switchExpr = res.Register(ParseSwitchExpression());
                    if (res.Error != null) return res;
                    return res.Success(switchExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Try:
                    var tryExpr = res.Register(ParseTryExpression());
                    if (res.Error != null) return res;
                    return res.Success(tryExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Enum:
                    var enumExpr = res.Register(ParseEnumDefinition());
                    if (res.Error != null) return res;
                    return res.Success(enumExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Struct:
                    var structExpr = res.Register(ParseStructDefinition());
                    if (res.Error != null) return res;
                    return res.Success(structExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Pub:
                    var structExpr1 = res.Register(ParseStructDefinition());
                    if (res.Error != null) return res;
                    return res.Success(structExpr1);
            }

            return res.Failure(new InvalidSyntaxError(tok.PositionStart, tok.PositionEnd, "Expected int, float, identifier, '+', '-', '(', '[', 'if', 'for', 'while', 'fn'"));
        }

        private ParserResult ParseEnumDefinition()
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Enum))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'enum'"));

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected enum name"));

            Token nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '{'"));

            res.RegisterAdvancement();
            Advance();

            var members = new List<(Token MemberTok, AstNode? ValueNode)>();

            if (_currentToken.Type != TokenType.RBRACKET)
            {
                while (true)
                {
                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected enum member name"));

                    Token memberTok = _currentToken;
                    res.RegisterAdvancement();
                    Advance();

                    AstNode? valueNode = null;
                    if (_currentToken.Type == TokenType.EQ)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        valueNode = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                    }

                    members.Add((memberTok, valueNode));

                    if (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type == TokenType.RBRACKET)
                            break;

                        continue;
                    }

                    break;
                }
            }

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));

            res.RegisterAdvancement();
            Advance();

            return res.Success(new EnumDefinitionNode(nameTok, members));
        }

        private ParserResult ParseTryExpression()
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '{' after 'try'"));

            res.RegisterAdvancement();
            Advance();

            var tryBody = res.Register(ParseStatements());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}' after try block"));

            res.RegisterAdvancement();
            Advance();

            Token? catchVarTok = null;
            AstNode? catchBody = null;

            if (_currentToken.Matches(Keyword.Catch))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '(' after 'catch'"));

                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier in 'catch (identifier)'"));

                catchVarTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ')' after catch identifier"));

                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.LBRACKET)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '{' after 'catch(...)'"));

                res.RegisterAdvancement();
                Advance();

                catchBody = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}' after catch block"));

                res.RegisterAdvancement();
                Advance();
            }

            AstNode? finallyBody = null;
            if (_currentToken.Matches(Keyword.Finally))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.LBRACKET)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '{' after 'finally'"));

                res.RegisterAdvancement();
                Advance();

                finallyBody = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}' after finally block"));

                res.RegisterAdvancement();
                Advance();
            }

            return res.Success(new TryNode(tryBody, catchVarTok, catchBody, finallyBody));
        }

        private ParserResult ParseDoWhileExpression()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart.Copy();

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();
                var bodyNode = res.Register(ParseStatements());

                if (res.Error != null)
                {
                    return res;
                }

                if (_currentToken.Type != TokenType.RBRACKET)
                {
                    return res.Failure(new InvalidSyntaxError(positionStart, _currentToken.PositionStart, "Expected '}'"));
                }

                res.RegisterAdvancement();
                Advance();

                if (!_currentToken.Matches(Keyword.While))
                {
                    return res.Failure(new InvalidSyntaxError(positionStart, _currentToken.PositionStart, "Expected 'while' keyword"));
                }

                res.RegisterAdvancement();
                Advance();
                var conditionExpr = res.Register(ParseExpression());

                if (res.Error != null)
                {
                    return res;
                }

                return res.Success(new DoWhileNode(conditionExpr, bodyNode, true));
            }
            else if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();
                var bodyNode = res.Register(ParseStatement());

                if (res.Error != null)
                {
                    return res;
                }

                if (!_currentToken.Matches(Keyword.While))
                {
                    return res.Failure(new InvalidSyntaxError(positionStart, _currentToken.PositionStart, "Expected 'while' keyword"));
                }

                res.RegisterAdvancement();
                Advance();
                var conditionExpr = res.Register(ParseExpression());

                if (res.Error != null)
                {
                    return res;
                }

                return res.Success(new DoWhileNode(conditionExpr, bodyNode, false));
            }

            return res.Failure(new InvalidSyntaxError(positionStart, _currentToken.PositionStart, "Expected ':' or '{'"));
        }

        private ParserResult ParseSetExpression()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart.Copy();

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '{'"));

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type == TokenType.RBRACKET)
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new SetNode(new List<AstNode>(), positionStart, _currentToken.PositionEnd.Copy()));
            }

            var rawElements = new List<(AstNode? Key, AstNode? Value, bool IsPair)>();

            {
                var firstExpr = res.Register(ParseExpression());
                if (res.Error != null)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd,
                        "Expected '}', 'var', 'if', 'for', 'while', 'fn', int, float, identifier, '+', '-', '(', '{' or 'not'"));

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var valueExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;

                    rawElements.Add((firstExpr, valueExpr, true));
                }
                else
                {
                    rawElements.Add((firstExpr, null, false));
                }
            }

            while (_currentToken.Type == TokenType.COMMA)
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type == TokenType.RBRACKET)
                {
                    res.RegisterAdvancement();
                    Advance();
                    break;
                }

                var expr = res.Register(ParseExpression());
                if (res.Error != null) return res;

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var valueExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;

                    rawElements.Add((expr, valueExpr, true));
                }
                else
                {
                    rawElements.Add((expr, null, false));
                }
            }

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ',' or '}'"));

            var rBracketEndPos = _currentToken.PositionEnd.Copy();
            res.RegisterAdvancement();
            Advance();

            bool anyPair = rawElements.Any(e => e.IsPair);

            if (anyPair)
            {
                var pairs = new List<(AstNode, AstNode)>();

                foreach (var el in rawElements)
                {
                    if (!el.IsPair)
                    {
                        return res.Failure(new InvalidSyntaxError(positionStart, rBracketEndPos, "Mixing map entries and set elements is not allowed"));
                    }
                    pairs.Add((el.Key!, el.Value!));
                }

                return res.Success(new MapNode(pairs, positionStart, rBracketEndPos));
            }
            else
            {
                var elementNodes = rawElements.Select(e => e.Key!).ToList();
                return res.Success(new SetNode(elementNodes, positionStart, rBracketEndPos));
            }
        }

        private ParserResult ParseListExpression()
        {
            var res = new ParserResult();
            var elementNodes = new List<AstNode>();
            var positionStart = _currentToken.PositionStart.Copy();

            if (_currentToken.Type != TokenType.LSQUARE)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '['"));

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type == TokenType.RSQUARE)
            {
                res.RegisterAdvancement();
                Advance();
            }
            else
            {
                if (_currentToken.Type == TokenType.SPREAD)
                {
                    var spreadTok = _currentToken;
                    res.RegisterAdvancement();
                    Advance();

                    var spreadExpr = res.Register(ParseExpression());
                    if (res.Error != null)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd,
                            "Expected expression after '...' in list"));

                    elementNodes.Add(new SpreadNode(spreadTok, spreadExpr));
                }
                else
                {
                    var first = res.Register(ParseExpression());
                    if (res.Error != null)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd,
                            "Expected ']', 'var', 'if', 'for', 'while', 'fn', int, float, identifier, '+', '-', '(', '[' or 'not'"));

                    elementNodes.Add(first);
                }

                while (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type == TokenType.SPREAD)
                    {
                        var spreadTok = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        var spreadExpr = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                        elementNodes.Add(new SpreadNode(spreadTok, spreadExpr));
                    }
                    else
                    {
                        var elem = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                        elementNodes.Add(elem);
                    }
                }

                if (_currentToken.Type != TokenType.RSQUARE)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ',' or ']'"));

                res.RegisterAdvancement();
                Advance();
            }

            return res.Success(new ListNode(elementNodes, positionStart, _currentToken.PositionEnd.Copy()));
        }

        private ParserResult ParseIfExpression()
        {
            var res = new ParserResult();

            var allCasesNode = res.Register(ParseIfExpressionCases(Keyword.If));
            if (res.Error != null) return res;

            var wrapper = (IfCasesWrapperNode)allCasesNode;
            return res.Success(new IfNode(wrapper.Cases, wrapper.ElseCase));
        }

        private ParserResult ParseIfExpressionCases(Keyword caseKeyword)
        {
            var res = new ParserResult();
            var cases = new List<(AstNode, AstNode, bool)>();
            (AstNode, bool)? elseCase = null;

            if (!_currentToken.Matches(caseKeyword))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, $"Expected '{caseKeyword}'"));

            res.RegisterAdvancement();
            Advance();

            var condition = res.Register(ParseExpression());
            if (res.Error != null) return res;

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();

                var statements = res.Register(ParseStatements());
                if (res.Error != null) return res;

                cases.Add((condition, statements, true));

                if (_currentToken.Type != TokenType.RBRACKET)
                {
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));
                }

                res.RegisterAdvancement();
                Advance();

                var chainNode = res.Register(ParseIfExpressionBOrC());
                if (res.Error != null) return res;

                var wrapper = (IfCasesWrapperNode)chainNode;
                cases.AddRange(wrapper.Cases);
                elseCase = wrapper.ElseCase;

                return res.Success(new IfCasesWrapperNode(cases, elseCase));
            }
            else if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();

                var expr = res.Register(ParseStatement());
                if (res.Error != null) return res;

                cases.Add((condition, expr, false));

                var chainNode = res.Register(ParseIfExpressionBOrC());
                if (res.Error != null) return res;

                var wrapper = (IfCasesWrapperNode)chainNode;
                cases.AddRange(wrapper.Cases);
                elseCase = wrapper.ElseCase;

                return res.Success(new IfCasesWrapperNode(cases, elseCase));
            }

            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ':' or '{'"));
        }

        private ParserResult ParseIfExpressionBOrC()
        {
            var res = new ParserResult();
            var cases = new List<(AstNode, AstNode, bool)>();
            (AstNode, bool)? elseCase = null;

            if (_currentToken.Matches(Keyword.Elif))
            {
                var node = res.Register(ParseIfExpressionCases(Keyword.Elif));
                if (res.Error != null) return res;

                var wrapper = (IfCasesWrapperNode)node;
                cases = wrapper.Cases;
                elseCase = wrapper.ElseCase;
            }
            else
            {
                var node = res.Register(ParseIfExpressionC());
                if (res.Error != null) return res;

                var wrapper = (IfCasesWrapperNode)node;
                elseCase = wrapper.ElseCase;
            }

            return res.Success(new IfCasesWrapperNode(cases, elseCase));
        }

        private ParserResult ParseIfExpressionC()
        {
            var res = new ParserResult();
            (AstNode, bool)? elseCase = null;

            if (_currentToken.Matches(Keyword.Else))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type == TokenType.LBRACKET)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var statements = res.Register(ParseStatements());
                    if (res.Error != null) return res;
                    elseCase = (statements, true);

                    if (_currentToken.Type == TokenType.RBRACKET)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                    else
                    {
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));
                    }
                }
                else if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var expr = res.Register(ParseStatement());
                    if (res.Error != null) return res;
                    elseCase = (expr, false);
                }
            }

            return res.Success(new IfCasesWrapperNode(new List<(AstNode, AstNode, bool)>(), elseCase));
        }

        private ParserResult ParseSwitchExpression()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart.Copy();
            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.LPAREN)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '(' after 'switch'"));

            res.RegisterAdvancement();
            Advance();

            var expr = res.Register(ParseExpression());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RPAREN)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ')' after switch expression"));

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '{' to open switch block"));

            res.RegisterAdvancement();
            Advance();

            var cases = new List<SwitchCaseNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Matches(Keyword.Case))
                {
                    res.RegisterAdvancement();
                    Advance();

                    var labels = new List<AstNode>();
                    var firstLabel = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    labels.Add(firstLabel);

                    while (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        var nextLabel = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                        labels.Add(nextLabel);
                    }

                    if (_currentToken.Type == TokenType.COLON)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        var stmtList = new List<AstNode>();
                        while (!(_currentToken.Matches(Keyword.Case) || _currentToken.Matches(Keyword.Default) || _currentToken.Type == TokenType.RBRACKET))
                        {
                            if (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                                continue;
                            }

                            var stmt = res.Register(ParseStatement());
                            if (res.Error != null) return res;
                            stmtList.Add(stmt);
                        }

                        var blockNode = new ListNode(stmtList, firstLabel.PositionStart.Copy(), (stmtList.Count > 0 ? stmtList.Last().PositionEnd.Copy() : _currentToken.PositionStart.Copy()));
                        cases.Add(new SwitchCaseNode(labels, false, SwitchCaseSeparator.Colon, blockNode, firstLabel.PositionStart.Copy(), _currentToken.PositionEnd.Copy()));
                    }
                    else if (_currentToken.Type == TokenType.ARROW_RIGHT)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        AstNode? body = null;

                        if (_currentToken.Type == TokenType.LBRACKET)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            var stmts = res.Register(ParseStatements());
                            if (res.Error != null) return res;
                            if (_currentToken.Type != TokenType.RBRACKET)
                                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));
                            res.RegisterAdvancement();
                            Advance();

                            body = stmts;
                        }
                        else
                        {
                            body = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }

                        cases.Add(new SwitchCaseNode(labels, false, SwitchCaseSeparator.Arrow, body, firstLabel.PositionStart.Copy(), (body ?? firstLabel).PositionEnd.Copy()));
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                    else
                    {
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ':' or '->' after case label"));
                    }
                }
                else if (_currentToken.Matches(Keyword.Default))
                {
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type == TokenType.COLON)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        var stmtList = new List<AstNode>();
                        while (!(_currentToken.Matches(Keyword.Case) || _currentToken.Type == TokenType.RBRACKET))
                        {
                            if (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                                continue;
                            }

                            var stmt = res.Register(ParseStatement());
                            if (res.Error != null) return res;
                            stmtList.Add(stmt);
                        }

                        var blockNode = new ListNode(stmtList, positionStart.Copy(), (stmtList.Count > 0 ? stmtList.Last().PositionEnd.Copy() : _currentToken.PositionStart.Copy()));
                        cases.Add(new SwitchCaseNode(new List<AstNode>(), true, SwitchCaseSeparator.Colon, blockNode, positionStart.Copy(), _currentToken.PositionEnd.Copy()));
                    }
                    else if (_currentToken.Type == TokenType.ARROW_RIGHT)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        AstNode? body = null;
                        if (_currentToken.Type == TokenType.LBRACKET)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            var stmts = res.Register(ParseStatements());
                            if (res.Error != null) return res;
                            if (_currentToken.Type != TokenType.RBRACKET)
                                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));
                            res.RegisterAdvancement();
                            Advance();
                            body = stmts;
                        }
                        else
                        {
                            body = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }

                        cases.Add(new SwitchCaseNode(new List<AstNode>(), true, SwitchCaseSeparator.Arrow, body, positionStart.Copy(), body == null ? positionStart : body.PositionEnd.Copy()));
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                    else
                    {
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ':' or '->' after default"));
                    }
                }
                else
                {
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'case' or 'default' in switch block"));
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            res.RegisterAdvancement();
            Advance();

            var switchNode = new SwitchNode(expr, cases, positionStart, _currentToken.PositionEnd.Copy());
            return res.Success(switchNode);
        }

        private ParserResult ParseForExpression()
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type == TokenType.LPAREN)
            {
                res.RegisterAdvancement();
                Advance();
                List<AstNode> initializationExpressions = new List<AstNode>(),
                    conditionExpressions = new List<AstNode>(),
                    stepExpressions = new List<AstNode>();

                bool skipCheck_1 = false;

                if (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                    skipCheck_1 = true;
                }
                else
                {
                    var initializationExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    initializationExpressions.Add(initializationExpr);
                }


                while (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                    var initializationExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    initializationExpressions.Add(initializationExpr);
                }

                if (!skipCheck_1 && _currentToken.Type != TokenType.NEWLINE)
                {
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ';'"));
                }

                if (!skipCheck_1)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                bool skipCheck_2 = false;

                if (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                    skipCheck_2 = true;
                }
                else
                {
                    var conditionExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    conditionExpressions.Add(conditionExpr);
                }
                

                while (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                    var conditionExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    conditionExpressions.Add(conditionExpr);
                }

                if (!skipCheck_2 && _currentToken.Type != TokenType.NEWLINE)
                {
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ';'"));
                }

                if (!skipCheck_2)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                bool skipCheck_3 = false;

                if (_currentToken.Type == TokenType.RPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();
                    skipCheck_3 = true;
                }
                else
                {
                    var stepExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    stepExpressions.Add(stepExpr);
                }

                while (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                    var stepExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    stepExpressions.Add(stepExpr);
                }

                if (!skipCheck_3 && _currentToken.Type != TokenType.RPAREN)
                {
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ')'"));
                }

                if (!skipCheck_3)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var bodyInline = res.Register(ParseStatement());
                    if (res.Error != null) return res;
                    return res.Success(new SuperForNode(initializationExpressions, conditionExpressions, stepExpressions, bodyInline, false));
                }
                else if (_currentToken.Type == TokenType.LBRACKET)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var body = res.Register(ParseStatements());
                    if (res.Error != null) return res;

                    if (_currentToken.Type != TokenType.RBRACKET)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));

                    res.RegisterAdvancement();
                    Advance();

                    return res.Success(new SuperForNode(initializationExpressions, conditionExpressions, stepExpressions, body, true));
                }

                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ':' or '{'"));
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier"));

            var varName = _currentToken;
            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type == TokenType.EQ)
            {
                res.RegisterAdvancement();
                Advance();

                var startValue = res.Register(ParseExpression());
                if (res.Error != null) return res;

                if (!_currentToken.Matches(Keyword.To))
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'to'"));

                res.RegisterAdvancement();
                Advance();

                var endValue = res.Register(ParseExpression());
                if (res.Error != null) return res;

                AstNode? stepValue = null;
                if (_currentToken.Matches(Keyword.Step))
                {
                    res.RegisterAdvancement();
                    Advance();
                    stepValue = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                }

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var bodyInline = res.Register(ParseStatement());
                    if (res.Error != null) return res;
                    return res.Success(new ForNode(varName, startValue, endValue, stepValue, bodyInline, false));
                }
                else if (_currentToken.Type == TokenType.LBRACKET)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var body = res.Register(ParseStatements());
                    if (res.Error != null) return res;

                    if (_currentToken.Type != TokenType.RBRACKET)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));

                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(new ForNode(varName, startValue, endValue, stepValue, body, true));
                }

                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ':' or '{'"));
            }
            else if (_currentToken.Matches(Keyword.In))
            {
                res.RegisterAdvancement();
                Advance();

                var collectionExpr = res.Register(ParseExpression());
                if (res.Error != null) return res;

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var bodyInline = res.Register(ParseStatement());
                    if (res.Error != null) return res;

                    return res.Success(new ForEachNode(varName, collectionExpr, bodyInline, false));
                }
                else if (_currentToken.Type == TokenType.LBRACKET)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var body = res.Register(ParseStatements());
                    if (res.Error != null) return res;

                    if (_currentToken.Type != TokenType.RBRACKET)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));

                    res.RegisterAdvancement();
                    Advance();

                    return res.Success(new ForEachNode(varName, collectionExpr, body, true));
                }
            }

            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '=' or 'in'"));
        }

        private ParserResult ParseWhileExpression()
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            var condition = res.Register(ParseExpression());
            if (res.Error != null) return res;

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();

                var body = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));

                res.RegisterAdvancement();
                Advance();
                return res.Success(new WhileNode(condition, body, true));
            }
            else if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();

                var bodyInline = res.Register(ParseStatement());
                if (res.Error != null) return res;
                return res.Success(new WhileNode(condition, bodyInline, false));
            }

            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ':' or '{'"));
        }

        private ParserResult ParseFunctionDefinition(string? ownerStructName = null, bool isPublic = false, bool isDeclaringConstructor = false)
        {
            var res = new ParserResult();

            if (!isDeclaringConstructor)
            {
                if (!_currentToken.Matches(Keyword.Fn))
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'fn'"));

                res.RegisterAdvancement();
                Advance();
            }
            else
            {
                if (_currentToken.Matches(Keyword.Fn))
                {
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "The keyword 'fn' is not expected for constructors"));
                }
            }

            Token? varNameTok = null;
            var genericTypeParams = new List<string>();

            if (_currentToken.Type == TokenType.IDENTIFIER)
            {
                varNameTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type == TokenType.LT)
                {
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected generic type parameter name"));

                    genericTypeParams.Add(_currentToken.Value?.ToString() ?? "");
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected generic type parameter name"));

                        genericTypeParams.Add(_currentToken.Value?.ToString() ?? "");
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type != TokenType.GT)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '>' after generic type parameters"));

                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '('"));
            }
            else if (_currentToken.Type == TokenType.LT)
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected generic type parameter name"));

                genericTypeParams.Add(_currentToken.Value?.ToString() ?? "");
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected generic type parameter name"));

                    genericTypeParams.Add(_currentToken.Value?.ToString() ?? "");
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.GT)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '>' after generic type parameters"));

                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '(' after generic parameters"));
            }
            else
            {
                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier or '('"));
            }

            res.RegisterAdvancement();
            Advance();

            var argNameToks = new List<Token>();
            var argTypes = new List<TypeDescriptor?>();
            var paramDefaults = new List<AstNode?>();
            bool hasVarArgs = false;
            Token? varArgNameTok = null;
            TypeDescriptor? varArgType = null;

            bool sawDefault = false;

            if (_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.SPREAD)
            {
                while (true)
                {
                    if (_currentToken.Type == TokenType.SPREAD)
                    {
                        hasVarArgs = true;
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier after '...'"));

                        varArgNameTok = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            var parsed = ParseType(res);
                            if (parsed == null)
                                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected type after ':' for vararg"));

                            varArgType = parsed;
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Variadic parameter must be the last parameter"));

                        break;
                    }
                    else
                    {
                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected parameter name"));

                        var paramTok = _currentToken;
                        argNameToks.Add(paramTok);
                        res.RegisterAdvancement();
                        Advance();

                        TypeDescriptor? ptype = null;
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            var parsed = ParseType(res);
                            if (parsed == null)
                                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected type after ':'"));

                            ptype = parsed;
                        }
                        argTypes.Add(ptype);

                        AstNode? defaultExpr = null;
                        if (_currentToken.Type == TokenType.EQ)
                        {
                            sawDefault = true;
                            res.RegisterAdvancement();
                            Advance();

                            defaultExpr = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }
                        else if (sawDefault)
                        {
                            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Parameters without default cannot appear after parameters with default"));
                        }

                        paramDefaults.Add(defaultExpr);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            if (_currentToken.Type == TokenType.RPAREN) break;
                            continue;
                        }

                        break;
                    }
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ',' or ')'"));
            }
            else
            {
                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier or ')'"));
            }

            res.RegisterAdvancement();
            Advance();

            TypeDescriptor? returnType = null;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();

                var parsed = ParseType(res);
                if (parsed == null)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected return type after ':'"));

                returnType = parsed;
            }

            bool isConstructor = ownerStructName != null
                                 && varNameTok != null
                                 && string.Equals(varNameTok.Value?.ToString(), ownerStructName, StringComparison.Ordinal);

            if (_currentToken.Type == TokenType.ARROW)
            {
                res.RegisterAdvancement();
                Advance();

                var body = res.Register(ParseExpression());
                if (res.Error != null) return res;

                return res.Success(new FunctionDefinitionNode(
                    varNameTok,
                    argNameToks,
                    argTypes,
                    paramDefaults,
                    hasVarArgs,
                    varArgNameTok,
                    varArgType,
                    returnType,
                    body,
                    true,
                    genericTypeParams,
                    isPublic,
                    isConstructor
                ));
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '=>' or '{'"));

            res.RegisterAdvancement();
            Advance();

            var bodyStmts = res.Register(ParseStatements());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));

            res.RegisterAdvancement();
            Advance();

            return res.Success(new FunctionDefinitionNode(
                varNameTok,
                argNameToks,
                argTypes,
                paramDefaults,
                hasVarArgs,
                varArgNameTok,
                varArgType,
                returnType,
                bodyStmts,
                false,
                genericTypeParams,
                isPublic,
                isConstructor
            ));
        }

        private ParserResult ParseStructDefinition()
        {
            var res = new ParserResult();
            bool isPublic = false;

            if (_currentToken.Matches(Keyword.Pub))
            {
                isPublic = true;
                res.RegisterAdvancement();
                Advance();
            }

            if (!_currentToken.Matches(Keyword.Struct))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'struct'"));

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected struct name"));

            var nameTok = _currentToken;
            var structName = nameTok.Value?.ToString() ?? "";

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '{'"));

            res.RegisterAdvancement();
            Advance();

            var fields = new List<StructFieldDefinitionNode>();
            var methods = new List<StructMethodDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                bool memberPublic = false;

                if (_currentToken.Matches(Keyword.Pub))
                {
                    memberPublic = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                if (_currentToken.Matches(Keyword.Var) ||
                    _currentToken.Matches(Keyword.Const) ||
                    _currentToken.Matches(Keyword.Final) ||
                    _currentToken.Matches(Keyword.Let))
                {
                    var declRes = ParseVariableDeclaration(memberPublic);
                    if (declRes.Error != null) return declRes;

                    var declNode = (VariableDeclarationNode)declRes.Node!;
                    foreach (var d in declNode.Declarations)
                    {
                        fields.Add(new StructFieldDefinitionNode(
                            declNode.IsPublic,
                            d.Item1,
                            d.Item3,
                            d.Item2
                        ));
                    }

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Fn) || (_currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == structName))
                {
                    var fnRes = ParseFunctionDefinition(ownerStructName: structName, isPublic: memberPublic, _currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == structName);
                    if (fnRes.Error != null) return fnRes;

                    methods.Add(new StructMethodDefinitionNodeFromFunctionDefinition((FunctionDefinitionNode)fnRes.Node!));
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                return res.Failure(new InvalidSyntaxError(
                    _currentToken.PositionStart,
                    _currentToken.PositionEnd,
                    "Expected field declaration or 'fn'"));
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new StructDefinitionNode(nameTok, isPublic, fields, methods));
        }

        private ParserResult ParseVariableDeclaration(bool isPublic = false)
        {
            ParserResult res = new ParserResult();
            VariableDeclarationType variableDeclarationType = VariableDeclarationType.VARIABLE;

            if (_currentToken.Matches(Keyword.Const))
            {
                variableDeclarationType = VariableDeclarationType.CONST;
            }
            else if (_currentToken.Matches(Keyword.Final))
            {
                variableDeclarationType = VariableDeclarationType.FINAL;
            }
            else if (_currentToken.Matches(Keyword.Let))
            {
                variableDeclarationType = VariableDeclarationType.LET;
            }

            res.RegisterAdvancement();
            Advance();

            List<(Token, AstNode?, TypeDescriptor?)> declarations = new List<(Token, AstNode?, TypeDescriptor?)>();

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier"));

            while (_currentToken.Type == TokenType.IDENTIFIER)
            {
                var varName = _currentToken;
                res.RegisterAdvancement();
                Advance();

                TypeDescriptor? declaredType = null;

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var parsedType = ParseType(res);
                    if (parsedType == null)
                    {
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected type after ':'"));
                    }
                    declaredType = parsedType;
                }

                AstNode? expr = null;

                if (_currentToken.Type == TokenType.EQ)
                {
                    res.RegisterAdvancement();
                    Advance();
                    expr = res.Register(ParseExpression());

                    if (res.Error != null)
                    {
                        return res;
                    }
                }

                declarations.Add((varName, expr, declaredType));

                if (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    break;
                }
            }

            return res.Success(new VariableDeclarationNode(variableDeclarationType, declarations, isPublic));
        }

        private ParserResult ParseBinaryOperation(Func<ParserResult> funcA, List<(TokenType, Keyword?)> ops, Func<ParserResult>? funcB = null)
        {
            if (funcB == null) funcB = funcA;
            var res = new ParserResult();
            var left = res.Register(funcA());
            if (res.Error != null) return res;

            while (ops.Any(op => op.Item1 == _currentToken.Type && (op.Item2 == null || op.Item2 == ((Keyword)_currentToken.Value))))
            {
                var opTok = _currentToken;
                res.RegisterAdvancement();
                Advance();
                var right = res.Register(funcB());
                if (res.Error != null) return res;
                left = new BinaryOperationNode(left, opTok, right);
            }
            return res.Success(left);
        }
    }
}
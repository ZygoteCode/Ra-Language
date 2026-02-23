using RaLanguage.Errors.Types;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Parser
{
    public class Parser
    {
        private readonly List<Token> _tokens;
        private int _tokenIndex;
        private Token _currentToken;

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

                        if (_currentToken.Type == TokenType.RBRACKET)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            break;
                        }
                    }

                    statements.AddRange(new ListNode(scopeStatements, _positionStart, _currentToken.PositionStart.Copy()));
                    continue;
                }
                else
                {
                    stmt = res.TryRegister(ParseStatement());
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

            if (_currentToken.Matches(TokenType.KEYWORD, "ret"))
            {
                res.RegisterAdvancement();
                Advance();

                var expr = res.TryRegister(ParseExpression());
                if (expr == null) Reverse(res.ToReverseCount);
                return res.Success(new ReturnNode(expr, positionStart, _currentToken.PositionStart.Copy()));
            }

            if (_currentToken.Matches(TokenType.KEYWORD, "continue"))
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new ContinueNode(positionStart, _currentToken.PositionStart.Copy()));
            }

            if (_currentToken.Matches(TokenType.KEYWORD, "break"))
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new BreakNode(positionStart, _currentToken.PositionStart.Copy()));
            }

            if (_currentToken.Matches(TokenType.KEYWORD, "pass"))
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new PassNode(positionStart, _currentToken.PositionStart.Copy()));
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

        private ParserResult ParseExpression()
        {
            var res = new ParserResult();

            if (_currentToken.Matches(TokenType.KEYWORD, "var"))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier"));

                var varName = _currentToken;
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.EQ)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '='"));

                res.RegisterAdvancement();
                Advance();
                var expr = res.Register(ParseExpression());
                if (res.Error != null) return res;
                return res.Success(new VariableDeclarationNode(varName, expr));
            }

            var node = res.Register(ParseBinaryOperation(ParseBitwiseOrExpression, new List<(TokenType, string?)> { (TokenType.KEYWORD, "and"), (TokenType.KEYWORD, "or") }));

            if (res.Error != null)
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentToken.PositionStart, _currentToken.PositionEnd,
                    "Expected 'var', 'if', 'for', 'while', 'fn', int, float, identifier, '+', '-', '(', '[' or 'not'"
                ));
            }

            return res.Success(node);
        }

        private ParserResult ParseBitwiseOrExpression()
        {
            return ParseBinaryOperation(ParseBitwiseAndExpression, new List<(TokenType, string?)> { (TokenType.BITWISE_OR, null) });
        }

        private ParserResult ParseBitwiseAndExpression()
        {
            return ParseBinaryOperation(ParseComparisonExpression, new List<(TokenType, string?)> { (TokenType.BITWISE_AND, null) });
        }

        private ParserResult ParseComparisonExpression()
        {
            var res = new ParserResult();

            if (_currentToken.Matches(TokenType.KEYWORD, "not") || _currentToken.Type == TokenType.BITWISE_NOT)
            {
                var opTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                var node = res.Register(ParseComparisonExpression());
                if (res.Error != null) return res;
                return res.Success(new UnaryOperationNode(opTok, node));
            }

            var b_node = res.Register(ParseBinaryOperation(ParseShiftExpression, new List<(TokenType, string?)>
            {
                (TokenType.EE, null), (TokenType.NE, null), (TokenType.LT, null),
                (TokenType.GT, null), (TokenType.LTE, null), (TokenType.GTE, null)
            }));

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
            return ParseBinaryOperation(ParseArithmeticExpression, new List<(TokenType, string?)>
            {
                (TokenType.BITWISE_LEFT_SHIFT, null),
                (TokenType.BITWISE_RIGHT_SHIFT, null)
            });
        }

        private ParserResult ParseArithmeticExpression()
        {
            return ParseBinaryOperation(ParseTerm, new List<(TokenType, string?)> { (TokenType.PLUS, null), (TokenType.MINUS, null) });
        }

        private ParserResult ParseTerm()
        {
            return ParseBinaryOperation(ParseFactor, new List<(TokenType, string?)>
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

            if (tok.Type == TokenType.PLUS || tok.Type == TokenType.MINUS)
            {
                res.RegisterAdvancement();
                Advance();
                var factor = res.Register(ParseFactor());
                if (res.Error != null) return res;
                return res.Success(new UnaryOperationNode(tok, factor));
            }

            return ParsePower();
        }

        private ParserResult ParsePower()
        {
            return ParseBinaryOperation(ParseCall, new List<(TokenType, string?)> { (TokenType.POW, null) }, ParseFactor);
        }

        private ParserResult ParseCall()
        {
            var res = new ParserResult();
            var atom = res.Register(ParseAtom());
            if (res.Error != null) return res;

            if (_currentToken.Type == TokenType.LPAREN)
            {
                res.RegisterAdvancement();
                Advance();
                var argNodes = new List<AstNode>();

                if (_currentToken.Type == TokenType.RPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    argNodes.Add(res.Register(ParseExpression()));
                    if (res.Error != null)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ')', 'var', 'if', 'for', 'while', 'fn', int, float, identifier, '+', '-', '(', '[' or 'not'"));

                    while (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();
                        argNodes.Add(res.Register(ParseExpression()));
                        if (res.Error != null) return res;
                    }

                    if (_currentToken.Type != TokenType.RPAREN)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ',' or ')'"));

                    res.RegisterAdvancement();
                    Advance();
                }
                return res.Success(new FunctionCallNode(atom, argNodes));
            }
            return res.Success(atom);
        }

        private ParserResult ParseAtom()
        {
            var res = new ParserResult();
            var tok = _currentToken;

            if (tok.Type == TokenType.INT || tok.Type == TokenType.FLOAT)
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new NumberNode(tok));
            }
            else if (tok.Type == TokenType.STRING)
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new StringNode(tok));
            }
            else if (tok.Type == TokenType.IDENTIFIER)
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new VariableAccessNode(tok));
            }
            else if (tok.Type == TokenType.LPAREN)
            {
                res.RegisterAdvancement();
                Advance();
                var expr = res.Register(ParseExpression());
                if (res.Error != null) return res;
                if (_currentToken.Type == TokenType.RPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(expr);
                }
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ')'"));
            }
            else if (tok.Type == TokenType.LSQUARE)
            {
                var listExpr = res.Register(ParseListExpression());
                if (res.Error != null) return res;
                return res.Success(listExpr);
            }
            else if (tok.Matches(TokenType.KEYWORD, "if"))
            {
                var ifExpr = res.Register(ParseIfExpression());
                if (res.Error != null) return res;
                return res.Success(ifExpr);
            }
            else if (tok.Matches(TokenType.KEYWORD, "for"))
            {
                var forExpr = res.Register(ParseForExpression());
                if (res.Error != null) return res;
                return res.Success(forExpr);
            }
            else if (tok.Matches(TokenType.KEYWORD, "while"))
            {
                var whileExpr = res.Register(ParseWhileExpression());
                if (res.Error != null) return res;
                return res.Success(whileExpr);
            }
            else if (tok.Matches(TokenType.KEYWORD, "fn"))
            {
                var funcDef = res.Register(ParseFunctionDefinition());
                if (res.Error != null) return res;
                return res.Success(funcDef);
            }

            return res.Failure(new InvalidSyntaxError(tok.PositionStart, tok.PositionEnd, "Expected int, float, identifier, '+', '-', '(', '[', 'if', 'for', 'while', 'fn'"));
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
                elementNodes.Add(res.Register(ParseExpression()));
                if (res.Error != null)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ']', 'var', 'if', 'for', 'while', 'fn', int, float, identifier, '+', '-', '(', '[' or 'not'"));

                while (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                    elementNodes.Add(res.Register(ParseExpression()));
                    if (res.Error != null) return res;
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

            var allCasesNode = res.Register(ParseIfExpressionCases("if"));
            if (res.Error != null) return res;

            var wrapper = (IfCasesWrapperNode)allCasesNode;
            return res.Success(new IfNode(wrapper.Cases, wrapper.ElseCase));
        }

        private ParserResult ParseIfExpressionCases(string caseKeyword)
        {
            var res = new ParserResult();
            var cases = new List<(AstNode, AstNode, bool)>();
            (AstNode, bool)? elseCase = null;

            if (!_currentToken.Matches(TokenType.KEYWORD, caseKeyword))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, $"Expected '{caseKeyword}'"));

            res.RegisterAdvancement();
            Advance();

            var condition = res.Register(ParseExpression());
            if (res.Error != null) return res;

            if (_currentToken.Type.Equals(TokenType.LBRACKET))
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
            else if (_currentToken.Type.Equals(TokenType.COLON))
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

            if (_currentToken.Matches(TokenType.KEYWORD, "elif"))
            {
                var node = res.Register(ParseIfExpressionCases("elif"));
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

            if (_currentToken.Matches(TokenType.KEYWORD, "else"))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type.Equals(TokenType.LBRACKET))
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
                else if (_currentToken.Type.Equals(TokenType.COLON))
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

        private ParserResult ParseForExpression()
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(TokenType.KEYWORD, "for"))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'for'"));

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier"));

            var varName = _currentToken;
            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.EQ)
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '='"));

            res.RegisterAdvancement();
            Advance();

            var startValue = res.Register(ParseExpression());
            if (res.Error != null) return res;

            if (!_currentToken.Matches(TokenType.KEYWORD, "to"))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'to'"));

            res.RegisterAdvancement();
            Advance();

            var endValue = res.Register(ParseExpression());
            if (res.Error != null) return res;

            AstNode? stepValue = null;
            if (_currentToken.Matches(TokenType.KEYWORD, "step"))
            {
                res.RegisterAdvancement();
                Advance();
                stepValue = res.Register(ParseExpression());
                if (res.Error != null) return res;
            }

            if (_currentToken.Type.Equals(TokenType.COLON))
            {
                res.RegisterAdvancement();
                Advance();

                var bodyInline = res.Register(ParseStatement());
                if (res.Error != null) return res;
                return res.Success(new ForNode(varName, startValue, endValue, stepValue, bodyInline, false));
            }
            else if (_currentToken.Type.Equals(TokenType.LBRACKET))
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

        private ParserResult ParseWhileExpression()
        {
            var res = new ParserResult();
            if (!_currentToken.Matches(TokenType.KEYWORD, "while"))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'while'"));

            res.RegisterAdvancement();
            Advance();

            var condition = res.Register(ParseExpression());
            if (res.Error != null) return res;

            if (_currentToken.Type.Equals(TokenType.LBRACKET))
            {
                res.RegisterAdvancement();
                Advance();

                var body = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (!_currentToken.Type.Equals(TokenType.RBRACKET))
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '}'"));

                res.RegisterAdvancement();
                Advance();
                return res.Success(new WhileNode(condition, body, true));
            }
            else if (_currentToken.Type.Equals(TokenType.COLON))
            {
                res.RegisterAdvancement();
                Advance();

                var bodyInline = res.Register(ParseStatement());
                if (res.Error != null) return res;
                return res.Success(new WhileNode(condition, bodyInline, false));
            }

            return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected ':' or '{'"));
        }

        private ParserResult ParseFunctionDefinition()
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(TokenType.KEYWORD, "fn"))
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected 'fn'"));

            res.RegisterAdvancement();
            Advance();

            Token? varNameTok = null;
            if (_currentToken.Type == TokenType.IDENTIFIER)
            {
                varNameTok = _currentToken;
                res.RegisterAdvancement();
                Advance();
                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected '('"));
            }
            else
            {
                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier or '('"));
            }

            res.RegisterAdvancement();
            Advance();
            var argNameToks = new List<Token>();

            if (_currentToken.Type == TokenType.IDENTIFIER)
            {
                argNameToks.Add(_currentToken);
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd, "Expected identifier"));

                    argNameToks.Add(_currentToken);
                    res.RegisterAdvancement();
                    Advance();
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

            if (_currentToken.Type == TokenType.ARROW)
            {
                res.RegisterAdvancement();
                Advance();
                var body = res.Register(ParseExpression());
                if (res.Error != null) return res;
                return res.Success(new FunctionDefinitionNode(varNameTok, argNameToks, body, true));
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
            return res.Success(new FunctionDefinitionNode(varNameTok, argNameToks, bodyStmts, false));
        }

        private ParserResult ParseBinaryOperation(Func<ParserResult> funcA, List<(TokenType, string?)> ops, Func<ParserResult>? funcB = null)
        {
            if (funcB == null) funcB = funcA;
            var res = new ParserResult();
            var left = res.Register(funcA());
            if (res.Error != null) return res;

            while (ops.Any(op => op.Item1 == _currentToken.Type && (op.Item2 == null || op.Item2 == _currentToken.Value?.ToString())))
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
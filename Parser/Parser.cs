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
    public class Parser
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

        private ParserResult ParseStatements()
        {
            var res = new ParserResult();
            var statements = new List<AstNode>();
            var positionStart = _currentToken.PositionStart;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var statement = res.Register(ParseStatement());
            if (res.Error != null)
            {
                res.Error = null;
                SkipToNextStatement(res);
            }
            else
            {
                statements.Add(statement);
            }

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
                    Position _positionStart = _currentToken.PositionStart;
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

                    statements.AddRange(new ScopeNode(scopeStatements, _positionStart, _currentToken.PositionStart));
                    continue;
                }
                else
                {
                    // Don't attempt to parse a statement when we've already reached
                    // a natural terminator — otherwise ParseStatement would emit a
                    // bogus "expected expression but found EOF / '}'" diagnostic.
                    if (_currentToken.Type == TokenType.EOF ||
                        _currentToken.Type == TokenType.RBRACKET)
                    {
                        break;
                    }

                    var stmtRes = ParseStatement();
                    var stmtNode = res.Register(stmtRes);

                    if (stmtRes.Error != null)
                    {
                        // Panic-mode recovery: record the failure (diagnostics are
                        // already in the bag via Register) and keep scanning so later
                        // statements still produce their own diagnostics instead of
                        // being hidden by the first broken one. Stop at the next
                        // newline / closing brace so the outer loop can re-enter.
                        res.Error = null;
                        while (_currentToken.Type != TokenType.EOF &&
                               _currentToken.Type != TokenType.NEWLINE &&
                               _currentToken.Type != TokenType.RBRACKET)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                        moreStatements = true;
                        continue;
                    }

                    stmt = stmtNode;
                }

                if (stmt == null)
                {
                    Reverse(res.ToReverseCount);
                    moreStatements = false;
                    continue;
                }

                statements.Add(stmt);
            }

            // If recovery emitted diagnostics but produced no fatal Error, the outer
            // driver still needs to know parsing failed. Surface the first error so
            // ParseResult.HasErrors reports correctly.
            if (res.Error == null && res.Diagnostics.HasErrors)
            {
                var firstErr = res.Diagnostics.FirstError;
                if (firstErr != null)
                {
                    res.Error = new InvalidSyntaxError(
                        firstErr.PrimarySpan.Start,
                        firstErr.PrimarySpan.End,
                        firstErr.Title,
                        firstErr.Code);
                }
            }

            return res.Success(new ScopeNode(
                statements,
                positionStart,
                _currentToken.PositionEnd
            ));
        }

        private ParserResult ParseStatement()
        {
            if (_currentToken.Type != TokenType.AT_SIGN)
                return ParseStatementCore();

            var res = new ParserResult();
            var (annotations, err) = ParseAnnotationListInline(res);
            if (err != null) return res.Failure(err);

            var innerRes = ParseStatementCore();
            var innerNode = res.Register(innerRes);
            if (res.Error != null) return res;

            AnnotationAttacher.Attach(innerNode, annotations);
            return innerNode != null ? res.Success(innerNode) : res;
        }

        private ParserResult ParseStatementCore()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            if (_currentToken.Type == TokenType.KEYWORD)
            {
                switch (_currentToken.Value)
                {
                    case Keyword.Ret:
                        res.RegisterAdvancement();
                        Advance();
                        var expr = res.TryRegister(ParseExpression());
                        if (expr == null) Reverse(res.ToReverseCount);
                        return res.Success(new ReturnNode(expr, positionStart, _currentToken.PositionStart));
                    case Keyword.Yield:
                        res.RegisterAdvancement();
                        Advance();
                        var expr2 = res.Register(ParseExpression());
                        if (res.Error != null) Reverse(res.ToReverseCount);
                        return res.Success(new YieldNode(expr2, positionStart, _currentToken.PositionStart));
                    case Keyword.Emit:
                        res.RegisterAdvancement();
                        Advance();
                        var emitExpr = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                        return res.Success(new RaLanguage.Parser.Nodes.Async.EmitNode(emitExpr, positionStart, _currentToken.PositionStart));
                    case Keyword.Continue:
                        res.RegisterAdvancement();
                        Advance();
                        return res.Success(new ContinueNode(positionStart, _currentToken.PositionStart));
                    case Keyword.Break:
                        res.RegisterAdvancement();
                        Advance();
                        return res.Success(new BreakNode(positionStart, _currentToken.PositionStart));
                    case Keyword.Pass:
                        res.RegisterAdvancement();
                        Advance();
                        return res.Success(new PassNode(positionStart, _currentToken.PositionStart));
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
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'goto'",
                                help: "goto targets a label declared as 'name:'"));
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
                // Preserve the deeper parser error (ParseExpression already produced a
                // specific diagnostic). Only fall back to a synthetic message if none.
                return res;
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
            var positionStart = _currentToken.PositionStart;

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (!_currentToken.Matches(Keyword.For))
                return res.Failure(ParserDiagnostics.ExpectedRetryFor(_currentToken));

            res.RegisterAdvancement();
            Advance();

            var countNode = res.Register(ParseExpression());
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (!_currentToken.Matches(Keyword.Times))
                return res.Failure(ParserDiagnostics.ExpectedRetryTimes(_currentToken));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            AstNode? delayNode = null;
            if (_currentToken.Matches(Keyword.Delay))
            {
                res.RegisterAdvancement();
                Advance();

                delayNode = res.Register(ParseExpression());
                if (res.Error != null) return res;
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the retry body"));

            res.RegisterAdvancement();
            Advance();

            var bodyNode = res.Register(ParseStatements());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the retry body"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            AstNode? elseNode = null;
            if (_currentToken.Matches(Keyword.Else))
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the retry 'else' branch"));

                res.RegisterAdvancement();
                Advance();

                elseNode = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the retry 'else' branch"));

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
            if (_currentToken.Type == TokenType.LPAREN)
            {
                res.RegisterAdvancement();
                Advance();

                var elements = new List<TypeDescriptor>();

                if (_currentToken.Type == TokenType.RPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();
                    return TypeDescriptor.Tuple(elements);
                }

                while (true)
                {
                    var elem = ParseType(res);
                    if (elem == null) return null;
                    elements.Add(elem);

                    if (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();
                        continue;
                    }

                    if (_currentToken.Type != TokenType.RPAREN) return null;
                    res.RegisterAdvancement();
                    Advance();
                    break;
                }

                return TypeDescriptor.Tuple(elements);
            }

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

            if (IsActiveGenericParam(baseName) && genericArgs.Count == 0)
            {
                return TypeDescriptor.TypeParameter(baseName);
            }

            return new TypeDescriptor(baseName, genericArgs);
        }

        private ParserResult ParseOptionalGenericTypeParameters(out List<string> genericTypeParams)
        {
            var res = new ParserResult();
            genericTypeParams = new List<string>();

            if (_currentToken.Type != TokenType.LT)
                return res.Success(null);

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedGenericParamName(_currentToken));

            genericTypeParams.Add(_currentToken.Value?.ToString() ?? "");
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            while (_currentToken.Type == TokenType.COMMA)
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(ParserDiagnostics.ExpectedGenericParamName(_currentToken));

                var name = _currentToken.Value?.ToString() ?? "";
                if (genericTypeParams.Contains(name))
                    return res.Failure(ParserDiagnostics.DuplicateGenericParam(name, _currentToken.PositionStart, _currentToken.PositionEnd));

                genericTypeParams.Add(name);
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            if (_currentToken.Type != TokenType.GT)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '>', '<', context: "the generic type parameter list"));

            res.RegisterAdvancement();
            Advance();

            return res.Success(null);
        }

        private ParserResult ParseOptionalWhereClause(List<string> genericTypeParams, out List<WhereConstraintNode> constraints)
        {
            var res = new ParserResult();
            constraints = new List<WhereConstraintNode>();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (!_currentToken.Matches(Keyword.Where))
                return res.Success(null);

            if (genericTypeParams == null || genericTypeParams.Count == 0)
                return res.Failure(ParserDiagnostics.WhereClauseRequiresGeneric(_currentToken.PositionStart, _currentToken.PositionEnd));

            res.RegisterAdvancement();
            Advance();

            while (true)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'where'",
                        help: "the 'where' clause constrains one of the declared generic parameters"));

                var paramTok = _currentToken;
                var paramName = paramTok.Value?.ToString() ?? "";

                if (!genericTypeParams.Contains(paramName))
                    return res.Failure(ParserDiagnostics.UnknownGenericParam(paramName, paramTok.PositionStart, paramTok.PositionEnd));

                if (constraints.Any(c => string.Equals(c.ParameterName, paramName, StringComparison.Ordinal)))
                    return res.Failure(ParserDiagnostics.DuplicateWhereConstraint(paramName, paramTok.PositionStart, paramTok.PositionEnd));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.COLON)
                    return res.Failure(ParserDiagnostics.ExpectedColon(_currentToken,
                        context: "after the parameter name in a 'where' clause"));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var constraintType = ParseType(res);
                if (constraintType == null)
                    return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "a 'where' clause constraint"));

                constraints.Add(new WhereConstraintNode(paramTok, constraintType));

                if (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                    continue;
                }

                break;
            }

            return res.Success(null);
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
            else if (_currentToken.Matches(Keyword.Await))
            {
                var awaitStart = _currentToken.PositionStart;
                res.RegisterAdvancement();
                Advance();
                var inner = res.Register(ParseExpression());
                if (res.Error != null) return res;
                return res.Success(new RaLanguage.Parser.Nodes.Async.AwaitNode(inner, awaitStart, _currentToken.PositionStart));
            }
            else if (_currentToken.Matches(Keyword.Spawn))
            {
                var spawnStart = _currentToken.PositionStart;
                res.RegisterAdvancement();
                Advance();
                var inner = res.Register(ParseExpression());
                if (res.Error != null) return res;
                return res.Success(new RaLanguage.Parser.Nodes.Async.SpawnNode(inner, spawnStart, _currentToken.PositionStart));
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
                        return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                            after: "'nameof('",
                            help: "nameof(x) returns the textual name of a declared symbol"));
                    }

                    Token tok = _currentToken;
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type != TokenType.RPAREN)
                    {
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(', context: "the 'nameof' argument"));
                    }

                    res.RegisterAdvancement();
                    Advance();

                    return res.Success(new NameofNode(tok));
                }
                else
                {
                    if (_currentToken.Type != TokenType.IDENTIFIER)
                    {
                        return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                            after: "'nameof'",
                            help: "nameof requires a symbol name, e.g. 'nameof myVar' or 'nameof(myVar)'"));
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
                        return res.Failure(ParserDiagnostics.ExpectedTypeName(_currentToken, after: "'as'"));
                    }

                    var castNode = new CastNode(leftNode, parsedType);
                    castNode.PositionStart = leftNode.PositionStart;
                    castNode.PositionEnd = _currentToken.PositionEnd;
                    leftNode = castNode;
                }
            }

            if (res.Error != null)
            {
                // ParseBinaryOperation / ParseBitwiseOrExpression already emitted a
                // specific diagnostic for the offending token; bubble it up untouched.
                return res;
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
                    return res.Failure(ParserDiagnostics.ExpectedColon(_currentToken,
                        context: "to separate the two branches of the '?:' ternary"));
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
                    return res.Failure(ParserDiagnostics.InvalidAssignmentTarget(
                        leftNode.PositionStart, leftNode.PositionEnd,
                        "only variables, indexed access (a[i]) and member access (a.b) may appear on the left of an assignment"));
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
                // The inner comparison parser already produced a precise diagnostic
                // for the offending token; preserve it rather than overwriting with a
                // generic "expected one of N tokens" fallback.
                return res;
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

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

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

                                while (_currentToken.Type == TokenType.NEWLINE)
                                {
                                    res.RegisterAdvancement();
                                    Advance();
                                }

                                bool isRef = false;
                                if (_currentToken.Type == TokenType.BITWISE_AND)
                                {
                                    isRef = true;
                                    res.RegisterAdvancement();
                                    Advance();
                                }

                                var expr = res.Register(ParseExpression());
                                if (res.Error != null) return res;
                                argNodes.Add(new ArgumentNode(nameTok, expr, isRef));
                            }
                            else
                            {
                                bool isRef = false;
                                if (_currentToken.Type == TokenType.BITWISE_AND)
                                {
                                    isRef = true;
                                    res.RegisterAdvancement();
                                    Advance();
                                }

                                var expr = res.Register(ParseExpression());
                                if (res.Error != null) return res;
                                argNodes.Add(new ArgumentNode(null, expr, isRef));
                            }

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            if (_currentToken.Type == TokenType.COMMA)
                            {
                                res.RegisterAdvancement();
                                Advance();

                                while (_currentToken.Type == TokenType.NEWLINE)
                                {
                                    res.RegisterAdvancement();
                                    Advance();
                                }

                                if (_currentToken.Type == TokenType.RPAREN) break;
                                continue;
                            }

                            break;
                        }

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                                "',' or ')'",
                                contextHint: "argument lists are comma-separated and end with ')'"));

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
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ']', '[', context: "an index expression"));

                    var rBracketEndPos = _currentToken.PositionEnd;
                    res.RegisterAdvancement();
                    Advance();

                    resultNode = new ListAccessNode(resultNode, indexNode, resultNode.PositionStart, rBracketEndPos);
                }
                else if (_currentToken.Type == TokenType.DOT)
                {
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(ParserDiagnostics.ExpectedMemberName(_currentToken));

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
                    var posStart = tok.PositionStart;
                    var posEnd = tok.PositionEnd;

                    while (_currentToken.Type == TokenType.STRING_TEXT || _currentToken.Type == TokenType.INTERP_START)
                    {
                        if (_currentToken.Type == TokenType.STRING_TEXT)
                        {
                            var textTok = _currentToken;
                            res.RegisterAdvancement();
                            Advance();
                            parts.Add(new StringTextNode(textTok.Value?.ToString() ?? "", textTok.PositionStart, textTok.PositionEnd));
                            posEnd = textTok.PositionEnd;
                        }
                        else if (_currentToken.Type == TokenType.INTERP_START)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            var expr2 = res.Register(ParseExpression());
                            if (res.Error != null) return res;

                            if (_currentToken.Type != TokenType.INTERP_END)
                            {
                                return res.Failure(ParserDiagnostics.ExpectedInterpClose(_currentToken));
                            }

                            res.RegisterAdvancement();
                            Advance();

                            parts.Add(expr2);
                            posEnd = expr2.PositionEnd;
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

                    var positionStart = tok.PositionStart;

                    if (_currentToken.Type == TokenType.RPAREN)
                    {
                        res.RegisterAdvancement();
                        Advance();
                        return res.Success(new TupleNode(new List<AstNode>(), positionStart, _currentToken.PositionEnd));
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
                            return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(', context: "the tuple literal"));
                        }

                        var tupleEndPos = _currentToken.PositionEnd;
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

                        return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                            "',' (continuing the tuple) or ')' (closing the parenthesized expression)",
                            contextHint: "parenthesized expressions need ')', tuples need ',' between their elements"));
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
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Async:
                    var asyncDef = res.Register(ParseAsyncFunctionDefinition());
                    if (res.Error != null) return res;
                    return res.Success(asyncDef);
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
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Pub:
                    var pubExpr = res.Register(ParserPubDefinition());
                    if (res.Error != null) return res;
                    return res.Success(pubExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Trait:
                    var traitExpr = res.Register(ParseTraitDefinition(false));
                    if (res.Error != null) return res;
                    return res.Success(traitExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Super:
                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(new SuperNode(tok.PositionStart, tok.PositionEnd));
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Struct:
                    var structExpr = res.Register(ParseStructDefinition(false));
                    if (res.Error != null) return res;
                    return res.Success(structExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Class:
                    var classExpr = res.Register(ParseClassDefinition(false, false, false));
                    if (res.Error != null) return res;
                    return res.Success(classExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Interface:
                    var interfaceExpr = res.Register(ParseInterfaceDefinition(false));
                    if (res.Error != null) return res;
                    return res.Success(interfaceExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Extend:
                    var extensionDef = res.Register(ParseExtensionDefinition());
                    if (res.Error != null) return res;
                    return res.Success(extensionDef);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Import:
                    var importDef = res.Register(ParseImportStatement());
                    if (res.Error != null) return res;
                    return res.Success(importDef);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Namespace:
                    var nsDef = res.Register(ParseNamespaceDeclaration());
                    if (res.Error != null) return res;
                    return res.Success(nsDef);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Using:
                    var usingDef = res.Register(ParseUsingStatement());
                    if (res.Error != null) return res;
                    return res.Success(usingDef);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Annotation:
                    var annDef = res.Register(ParseAnnotationDefinition(false));
                    if (res.Error != null) return res;
                    return res.Success(annDef);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Asm:
                    var asmBlk = res.Register(ParseAsmBlock());
                    if (res.Error != null) return res;
                    return res.Success(asmBlk);
                case TokenType.AT_SIGN:
                {
                    var (annNode, annErr) = ParseSingleAnnotationApplication(res);
                    if (annErr != null) return res.Failure(annErr);
                    return res.Success(annNode!);
                }
            }

            return res.Failure(ParserDiagnostics.ExpectedExpression(tok));
        }

        private ParserResult ParseExtensionDefinition()
        {
            var res = new ParserResult();
            bool isPublic = false;

            if (_currentToken.Matches(Keyword.Pub))
            {
                isPublic = true;
                res.RegisterAdvancement();
                Advance();
            }

            if (!_currentToken.Matches(Keyword.Extend))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "extend",
                    context: "to start an extension block",
                    help: "extension syntax: 'extend TargetType { fn ... }'"));

            res.RegisterAdvancement();
            Advance();

            var targetType = ParseType(res);
            if (targetType == null)
                return res.Failure(ParserDiagnostics.ExpectedTypeName(_currentToken, after: "'extend'"));

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the extension body"));

            res.RegisterAdvancement();
            Advance();

            var methods = new List<FunctionDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                bool methodPublic = false;
                if (_currentToken.Matches(Keyword.Pub))
                {
                    methodPublic = true;
                    res.RegisterAdvancement();
                    Advance();
                }

                if (!_currentToken.Matches(Keyword.Fn))
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "fn",
                        context: "to declare an extension method",
                        help: "only 'fn' declarations are allowed inside an extension body"));

                var fnRes = ParseFunctionDefinition(isPublic: methodPublic);
                if (fnRes.Error != null) return fnRes;

                var fnNode = (FunctionDefinitionNode)fnRes.Node!;

                if (fnNode.IsConstructor)
                    return res.Failure(ParserDiagnostics.ExtensionConstructorNotAllowed(fnNode.PositionStart, fnNode.PositionEnd));

                if (fnNode.IsAbstract)
                    return res.Failure(ParserDiagnostics.ExtensionMethodNeedsBody(fnNode.PositionStart, fnNode.PositionEnd));

                if (fnNode.BodyNode == null)
                    return res.Failure(ParserDiagnostics.ExtensionMethodNeedsBody(fnNode.PositionStart, fnNode.PositionEnd));

                methods.Add(fnNode);

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new ExtensionDefinitionNode(targetType, isPublic, methods));
        }

        private ParserResult ParseImportStatement()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                return ParseImportSelective(res, positionStart);
            }

            var spec = ParseModuleSpecifier(res);
            if (res.Error != null) return res;
            if (spec == null)
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentToken.PositionStart, _currentToken.PositionEnd,
                    $"expected a string path, '{{' selector or dotted module name after 'import' but found {DescribeToken(_currentToken)}",
                    DiagnosticCode.ParserExpectedToken,
                    help: "imports look like 'import \"./mod.ra\"', 'import std.io' or 'import { a, b } from \"./mod.ra\"'",
                    primaryLabel: "module specifier expected here"));
            }

            if (_currentToken.Type == TokenType.KEYWORD && _currentToken.Matches(Keyword.As))
            {
                res.RegisterAdvancement();
                Advance();

                SkipNewlines(res);

                if (_currentToken.Type != TokenType.IDENTIFIER)
                {
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'as'",
                        help: "the alias must be a single identifier, e.g. 'import std.io as IO'"));
                }

                var aliasTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                return res.Success(new ImportAliasNode(spec, aliasTok, positionStart, _currentToken.PositionEnd));
            }

            return res.Success(new ImportAllNode(spec, positionStart, _currentToken.PositionEnd));
        }

        private ParserResult ParseImportSelective(ParserResult res, Position positionStart)
        {
            res.RegisterAdvancement();
            Advance();

            var symbolNames = new List<Token>();
            while (_currentToken.Type != TokenType.RBRACKET)
            {
                SkipNewlines(res);

                if (_currentToken.Type != TokenType.IDENTIFIER)
                {
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'{'",
                        help: "the selective import list contains comma-separated symbol names"));
                }

                symbolNames.Add(_currentToken);
                res.RegisterAdvancement();
                Advance();

                SkipNewlines(res);

                if (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else if (_currentToken.Type != TokenType.RBRACKET)
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "',' or '}'",
                        contextHint: "the selective import list is comma-separated and ends with '}'"));
                }
            }

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            if (!_currentToken.Matches(Keyword.From))
            {
                return res.Failure(ParserDiagnostics.ExpectedFromAfterImport(_currentToken));
            }

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            var spec = ParseModuleSpecifier(res);
            if (res.Error != null) return res;
            if (spec == null)
            {
                return res.Failure(ParserDiagnostics.ExpectedImportSource(_currentToken));
            }

            return res.Success(new ImportSelectiveNode(spec, symbolNames, positionStart, _currentToken.PositionEnd));
        }

        private ParserResult ParseNamespaceDeclaration()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            var segments = ParseQualifiedNameSegments(res);
            if (res.Error != null) return res;
            if (segments == null || segments.Count == 0)
            {
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "'namespace'",
                    help: "namespace names are dotted identifiers, e.g. 'namespace system.io { ... }'"));
            }

            SkipNewlines(res);

            if (_currentToken.Type != TokenType.LBRACKET)
            {
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the namespace body"));
            }

            var bodyStart = _currentToken.PositionStart;
            res.RegisterAdvancement();
            Advance();

            var body = res.Register(ParseStatements());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RBRACKET)
            {
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the namespace body"));
            }

            var bodyEnd = _currentToken.PositionEnd;
            res.RegisterAdvancement();
            Advance();

            return res.Success(new NamespaceDeclarationNode(
                segments,
                body!,
                isFileScoped: false,
                positionStart,
                bodyEnd));
        }

        private ParserResult ParseUsingStatement()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            var segments = ParseQualifiedNameSegments(res);
            if (res.Error != null) return res;
            if (segments == null || segments.Count == 0)
            {
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "'using'",
                    help: "using takes a dotted namespace path, e.g. 'using system.io'"));
            }

            Token? aliasTok = null;
            if (_currentToken.Type == TokenType.KEYWORD && _currentToken.Matches(Keyword.As))
            {
                res.RegisterAdvancement();
                Advance();
                SkipNewlines(res);

                if (_currentToken.Type != TokenType.IDENTIFIER)
                {
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'as'",
                        help: "the using-alias must be a single identifier, e.g. 'using system.io as IO'"));
                }
                aliasTok = _currentToken;
                res.RegisterAdvancement();
                Advance();
            }

            return res.Success(new UsingNamespaceNode(
                segments,
                aliasTok,
                positionStart,
                _currentToken.PositionEnd));
        }

        private List<Token>? ParseQualifiedNameSegments(ParserResult res)
        {
            if (_currentToken.Type != TokenType.IDENTIFIER)
            {
                res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    help: "qualified names are dotted identifiers (e.g. 'system.io.console')"));
                return null;
            }

            var segments = new List<Token> { _currentToken };
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.DOT)
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.IDENTIFIER)
                {
                    res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'.'",
                        help: "each dotted segment must be an identifier"));
                    return null;
                }

                segments.Add(_currentToken);
                res.RegisterAdvancement();
                Advance();
            }

            return segments;
        }

        private Interpreter.Modules.ModuleSpecifier? ParseModuleSpecifier(ParserResult res)
        {
            if (_currentToken.Type == TokenType.STRING_TEXT)
            {
                string rawPath = _currentToken.Value?.ToString() ?? "";
                res.RegisterAdvancement();
                Advance();
                return Interpreter.Modules.ModuleSpecifier.FromStringLiteral(rawPath);
            }

            if (_currentToken.Type == TokenType.IDENTIFIER)
            {
                var segments = new List<string>();
                segments.Add(_currentToken.Value?.ToString() ?? "");
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.DOT)
                {
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                    {
                        res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                            after: "'.' in module path",
                            help: "module paths are dotted identifiers, e.g. 'std.io.file'"));
                        return null;
                    }

                    segments.Add(_currentToken.Value?.ToString() ?? "");
                    res.RegisterAdvancement();
                    Advance();
                }

                return Interpreter.Modules.ModuleSpecifier.FromDotted(segments);
            }

            return null;
        }

        private void SkipNewlines(ParserResult res)
        {
            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }
        }

        private ParserResult ParseCallableSignatureAfterName(bool allowReturnType)
        {
            var res = new ParserResult();

            var argNameToks = new List<Token>();
            var argTypes = new List<TypeDescriptor?>();
            var isRefParams = new List<bool>();
            var paramDefaults = new List<AstNode?>();

            bool hasVarArgs = false;
            Token? varArgNameTok = null;
            TypeDescriptor? varArgType = null;
            TypeDescriptor? returnType = null;

            bool sawDefault = false;

            if (_currentToken.Type != TokenType.LPAREN)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '(', context: "the parameter list"));

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.SPREAD || _currentToken.Matches(Keyword.Ref))
            {
                while (true)
                {
                    if (_currentToken.Type == TokenType.SPREAD)
                    {
                        hasVarArgs = true;
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                                after: "'...'",
                                help: "variadic parameters take an identifier, e.g. '...args: int'"));

                        varArgNameTok = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            var parsed = ParseType(res);
                            if (parsed == null)
                                return res.Failure(ParserDiagnostics.ExpectedVarArgsType(_currentToken));

                            varArgType = parsed;
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                            return res.Failure(ParserDiagnostics.VariadicMustBeLast(_currentToken.PositionStart, _currentToken.PositionEnd));

                        break;
                    }

                    bool isRef = false;
                    if (_currentToken.Matches(Keyword.Ref))
                    {
                        isRef = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken, hostingConstruct: "parameter list"));

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
                            return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "a parameter declaration"));

                        if (isRef)
                        {
                            ptype = TypeDescriptor.RefType(parsed);
                        }
                        else
                        {
                            ptype = parsed;
                        }
                    }
                    argTypes.Add(ptype);
                    isRefParams.Add(isRef);

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
                        return res.Failure(ParserDiagnostics.DefaultParameterMustBeTrailing(_currentToken.PositionStart, _currentToken.PositionEnd));
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

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "',' or ')'",
                        contextHint: "parameter lists are comma-separated and end with ')'"));
            }
            else
            {
                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "a parameter name or ')'",
                        contextHint: "parameter lists begin with names or '...' and end with ')'"));
            }

            res.RegisterAdvancement();
            Advance();

            if (allowReturnType && _currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();

                var parsed = ParseType(res);
                if (parsed == null)
                    return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "the return type annotation"));

                returnType = parsed;
            }

            return res.Success(new CallableSignatureNode(
                argNameToks,
                argTypes,
                isRefParams,
                paramDefaults,
                hasVarArgs,
                varArgNameTok,
                varArgType,
                returnType
            ));
        }

        private ParserResult ParseTraitDefinition(bool isPublic)
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Trait))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "trait",
                    context: "to start a trait declaration"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "'trait'",
                    help: "trait declarations begin with a name, e.g. 'trait Printable { ... }'"));

            var nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the trait body"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var methods = new List<TraitMethodDefinitionNode>();
            var fields = new List<StructFieldDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                List<AnnotationApplicationNode>? memberAnnotations = null;
                if (_currentToken.Type == TokenType.AT_SIGN)
                {
                    var (annList, annErr) = ParseAnnotationListInline(res);
                    if (annErr != null) return res.Failure(annErr);
                    memberAnnotations = annList;
                }

                bool memberPublic = false;
                bool isAbstract = false;

                if (_currentToken.Matches(Keyword.Pub))
                {
                    res.RegisterAdvancement();
                    Advance();

                    memberPublic = true;

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
                    var declRes = ParseTraitFieldDeclaration(memberPublic);
                    if (declRes.Error != null) return declRes;

                    var declNode = (VariableDeclarationNode)declRes.Node!;
                    foreach (var d in declNode.Declarations)
                    {
                        var (nameTokh, defaultValueNode, typeNode) = d;
                        var fieldNode = new StructFieldDefinitionNode(
                            memberPublic,
                            nameTokh,
                            typeNode,
                            defaultValueNode,
                            false,
                            false,
                            false,
                            declNode.DeclarationType
                        );
                        AnnotationAttacher.Attach(fieldNode, memberAnnotations);
                        fields.Add(fieldNode);
                    }

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Abstract))
                {
                    isAbstract = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                bool traitMemberAsync = false;
                bool traitMemberAsyncStream = false;
                if (_currentToken.Matches(Keyword.Async))
                {
                    traitMemberAsync = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.IDENTIFIER && string.Equals(_currentToken.Value?.ToString(), "stream", System.StringComparison.Ordinal))
                    {
                        traitMemberAsyncStream = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                }

                if (!_currentToken.Matches(Keyword.Fn))
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "fn",
                        context: "to declare a trait method",
                        help: "trait bodies contain field declarations or 'fn' method signatures"));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'fn'",
                        help: "every method declaration needs a name following 'fn'"));

                var methodNameTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var sigRes = ParseCallableSignatureAfterName(true);
                if (sigRes.Error != null) return sigRes;
                var sigNode = (CallableSignatureNode)sigRes.Node!;

                AstNode? bodyNode = null;

                if (_currentToken.Type == TokenType.ARROW)
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    bodyNode = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                }
                else if (_currentToken.Type == TokenType.LBRACKET)
                {
                    res.RegisterAdvancement();
                    Advance();

                    bodyNode = res.Register(ParseStatements());
                    if (res.Error != null) return res;

                    if (_currentToken.Type != TokenType.RBRACKET)
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the trait method body"));

                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    bodyNode = null;
                }

                var traitMethodNode = new TraitMethodDefinitionNode(
                    methodNameTok,
                    sigNode.ArgNameToks,
                    sigNode.ArgTypes,
                    sigNode.IsRefParams,
                    sigNode.ParamDefaults,
                    sigNode.HasVarArgs,
                    sigNode.VarArgNameTok,
                    sigNode.VarArgType,
                    sigNode.ReturnType,
                    bodyNode,
                    bodyNode != null,
                    isAbstract
                );
                traitMethodNode.IsAsync = traitMemberAsync;
                traitMethodNode.IsAsyncStream = traitMemberAsyncStream;
                AnnotationAttacher.Attach(traitMethodNode, memberAnnotations);
                methods.Add(traitMethodNode);

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new TraitDefinitionNode(nameTok, isPublic, methods, fields, genericTypeParams, whereConstraints));
            }
            finally
            {
                PopGenericScope();
            }
        }

        private ParserResult ParseInterfaceDefinition(bool isPublic)
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'interface'", help: "interface declarations begin with a name, e.g. 'interface Drawable { fn draw(); }'"));

            Token nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var methods = new List<InterfaceMethodSignatureNode>();
            var fields = new List<StructFieldDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                List<AnnotationApplicationNode>? memberAnnotations = null;
                if (_currentToken.Type == TokenType.AT_SIGN)
                {
                    var (annList, annErr) = ParseAnnotationListInline(res);
                    if (annErr != null) return res.Failure(annErr);
                    memberAnnotations = annList;
                }

                bool memberPublic = false;
                if (_currentToken.Matches(Keyword.Pub))
                {
                    res.RegisterAdvancement();
                    Advance();
                    memberPublic = true;

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
                    var declRes = ParseInterfaceFieldDeclaration(memberPublic);
                    if (declRes.Error != null) return declRes;

                    var declNode = (VariableDeclarationNode)declRes.Node!;
                    foreach (var d in declNode.Declarations)
                    {
                        var (nameTokh, typeNode, _) = d;
                        var fieldNode = new StructFieldDefinitionNode(
                            memberPublic,
                            nameTokh,
                            d.Item3,
                            typeNode,
                            false,
                            false,
                            false,
                            declNode.DeclarationType
                        );
                        AnnotationAttacher.Attach(fieldNode, memberAnnotations);
                        fields.Add(fieldNode);
                    }

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                if (!_currentToken.Matches(Keyword.Fn))
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "'fn' (for methods) or a field declaration",
                        contextHint: "interface bodies contain method signatures ('fn ...') or field declarations ('var', 'let', 'const', 'final')"));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'fn'", help: "every method declaration needs a name following 'fn'"));

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                Token methodNameTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '('));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var argNameToks = new List<Token>();
                var argTypes = new List<TypeDescriptor?>();

                if (_currentToken.Type != TokenType.RPAREN)
                {
                    while (true)
                    {
                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken));

                        var argTok = _currentToken;
                        argNameToks.Add(argTok);

                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        TypeDescriptor? argType = null;
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            var parsedType = ParseType(res);
                            if (parsedType == null)
                                return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));

                            argType = parsedType;
                        }

                        argTypes.Add(argType);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }

                        break;
                    }

                    if (_currentToken.Type != TokenType.RPAREN)
                        return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "',' or ')'", contextHint: "the parameter / argument list is comma-separated and ends with ')'"));
                }

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                TypeDescriptor? returnType = null;
                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    var parsedType = ParseType(res);
                    if (parsedType == null)
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "the return type annotation"));

                    returnType = parsedType;
                }

                var ifaceMethodNode = new InterfaceMethodSignatureNode(methodNameTok, argNameToks, argTypes, returnType);
                AnnotationAttacher.Attach(ifaceMethodNode, memberAnnotations);
                methods.Add(ifaceMethodNode);

                if (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new InterfaceDefinitionNode(nameTok, isPublic, methods, fields, genericTypeParams, whereConstraints));
            }
            finally
            {
                PopGenericScope();
            }
        }

        private ParserResult ParserPubDefinition()
        {
            var res = new ParserResult();
            bool isAbstract = false;
            bool isStatic = false;

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            while (_currentToken.Matches(Keyword.Abstract) || _currentToken.Matches(Keyword.Static))
            {
                if (_currentToken.Matches(Keyword.Abstract))
                {
                    isAbstract = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                if (_currentToken.Matches(Keyword.Static))
                {
                    isStatic = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }
            }

            if (_currentToken.Matches(Keyword.Struct))
            {
                var structDef = res.Register(ParseStructDefinition(true));
                if (res.Error != null) return res;
                return res.Success(structDef);
            }
            else if (_currentToken.Matches(Keyword.Class))
            {
                var classDef = res.Register(ParseClassDefinition(true, isAbstract, isStatic));
                if (res.Error != null) return res;
                return res.Success(classDef);
            }
            else if (_currentToken.Matches(Keyword.Interface))
            {
                var interfaceDef = res.Register(ParseInterfaceDefinition(true));
                if (res.Error != null) return res;
                return res.Success(interfaceDef);
            }
            else if (_currentToken.Matches(Keyword.Trait))
            {
                var traitDef = res.Register(ParseTraitDefinition(true));
                if (res.Error != null) return res;
                return res.Success(traitDef);
            }
            else if (_currentToken.Matches(Keyword.Fn))
            {
                var funcDef = res.Register(ParseFunctionDefinition(isPublic: true));
                if (res.Error != null) return res;
                return res.Success(funcDef);
            }
            else if (_currentToken.Matches(Keyword.Async))
            {
                var asyncDef = res.Register(ParseAsyncFunctionDefinition(isPublic: true));
                if (res.Error != null) return res;
                return res.Success(asyncDef);
            }
            else if (_currentToken.Matches(Keyword.Var) || _currentToken.Matches(Keyword.Final) || _currentToken.Matches(Keyword.Let) || _currentToken.Matches(Keyword.Const))
            {
                var variableDecl = res.Register(ParseVariableDeclaration(isPublic: true));
                if (res.Error != null) return res;
                return res.Success(variableDecl);
            }
            else if (_currentToken.Matches(Keyword.Annotation))
            {
                var annDef = res.Register(ParseAnnotationDefinition(true));
                if (res.Error != null) return res;
                return res.Success(annDef);
            }

            return res.Failure(ParserDiagnostics.ExpectedOneOfKeywords(_currentToken, new[] { "struct", "class" }, context: "after the access / modifier list"));
        }

        private ParserResult ParseClassDefinition(bool isPublic, bool isAbstract, bool isStatic)
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'class'", help: "class declarations begin with a name, e.g. 'class Point { ... }'"));

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var nameTok = _currentToken;
            string className = nameTok.Value?.ToString() ?? "";
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            TypeDescriptor? baseType = null;
            var implementedInterfaces = new List<TypeDescriptor>();
            var withTraits = new List<TypeDescriptor>();
            List<WhereConstraintNode> whereConstraints = new List<WhereConstraintNode>();

            while (_currentToken.Type != TokenType.LBRACKET)
            {
                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    baseType = ParseType(res);
                    if (baseType == null)
                        return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "':'", help: "the ':' in a class header is followed by the base class name"));

                    continue;
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Matches(Keyword.With))
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    while (true)
                    {
                        var ifaceType = ParseType(res);
                        if (ifaceType == null)
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'impl'", help: "list the implemented interface(s) after 'impl', e.g. 'class C impl I1, I2 { ... }'"));

                        withTraits.Add(ifaceType);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }

                        break;
                    }

                    continue;
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Matches(Keyword.Impl))
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    while (true)
                    {
                        var ifaceType = ParseType(res);
                        if (ifaceType == null)
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'impl'", help: "list the implemented interface(s) after 'impl', e.g. 'class C impl I1, I2 { ... }'"));

                        implementedInterfaces.Add(ifaceType);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }

                        break;
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Where))
                {
                    res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
                    if (res.Error != null) return res;
                    continue;
                }

                if (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                    continue;
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "':' (base class), 'impl' (interfaces), 'where' (constraints) or '{' (body)",
                    contextHint: "after a class name you may declare a base class with ':', interfaces with 'impl', constraints with 'where', or open the body with '{'"));
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));

            res.RegisterAdvancement();
            Advance();

            var fields = new List<StructFieldDefinitionNode>();
            var methods = new List<FunctionDefinitionNode>();
            var operators = new List<RaLanguage.Parser.Nodes.Classes.OperatorDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                List<AnnotationApplicationNode>? memberAnnotations = null;
                if (_currentToken.Type == TokenType.AT_SIGN)
                {
                    var (annList, annErr) = ParseAnnotationListInline(res);
                    if (annErr != null) return res.Failure(annErr);
                    memberAnnotations = annList;
                }

                bool isMemberPublic = false,
                    isMemberOverride = false,
                    isMemberAbstract = false,
                    isMemberStatic = false;

                while (_currentToken.Matches(Keyword.Pub) || _currentToken.Matches(Keyword.Override) || _currentToken.Matches(Keyword.Abstract) || _currentToken.Matches(Keyword.Static))
                {
                    if (_currentToken.Matches(Keyword.Pub))
                    {
                        if (isMemberPublic)
                        {
                            return res.Failure(ParserDiagnostics.DuplicateModifier("pub", _currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        isMemberPublic = true;
                    }

                    if (_currentToken.Matches(Keyword.Override))
                    {
                        if (isMemberOverride)
                        {
                            return res.Failure(ParserDiagnostics.DuplicateModifier("override", _currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        isMemberOverride = true;
                    }

                    if (_currentToken.Matches(Keyword.Abstract))
                    {
                        if (isMemberAbstract)
                        {
                            return res.Failure(ParserDiagnostics.DuplicateModifier("abstract", _currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        isMemberAbstract = true;
                    }

                    if (_currentToken.Matches(Keyword.Static))
                    {
                        if (isMemberStatic)
                        {
                            return res.Failure(ParserDiagnostics.DuplicateModifier("static", _currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        isMemberStatic = true;
                    }

                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Matches(Keyword.Var) ||
                    _currentToken.Matches(Keyword.Const) ||
                    _currentToken.Matches(Keyword.Final) ||
                    _currentToken.Matches(Keyword.Let))
                {
                    var declRes = ParseVariableDeclaration(isMemberPublic, isMemberStatic);
                    if (declRes.Error != null) return declRes;

                    var declNode = (VariableDeclarationNode)declRes.Node!;
                    foreach (var d in declNode.Declarations)
                    {
                        var fieldNode = new StructFieldDefinitionNode(
                            isMemberPublic,
                            d.Item1,
                            d.Item3,
                            d.Item2,
                            isMemberStatic,
                            isMemberAbstract,
                            isMemberOverride,
                            declNode.DeclarationType
                        );
                        AnnotationAttacher.Attach(fieldNode, memberAnnotations);
                        fields.Add(fieldNode);
                    }

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                bool isMemberAsync = false;
                bool isMemberAsyncStream = false;
                if (_currentToken.Matches(Keyword.Async))
                {
                    isMemberAsync = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.IDENTIFIER && string.Equals(_currentToken.Value?.ToString(), "stream", System.StringComparison.Ordinal))
                    {
                        isMemberAsyncStream = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                }

                if (_currentToken.Matches(Keyword.Fn) || (_currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == className))
                {
                    var fnRes = ParseFunctionDefinition(ownerTypeName: className, isPublic: isMemberPublic, isOverride: isMemberOverride, isAbstract: isMemberAbstract, isStatic: isMemberStatic, isDeclaringConstructor: _currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == className, isAsync: isMemberAsync, isAsyncStream: isMemberAsyncStream);
                    if (fnRes.Error != null) return fnRes;

                    var methodNode = (FunctionDefinitionNode)fnRes.Node!;
                    AnnotationAttacher.Attach(methodNode, memberAnnotations);
                    methods.Add(methodNode);
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Operator))
                {
                    var opRes = ParseOperatorDefinition(isPublic: isMemberPublic, isOverride: isMemberOverride, isStatic: isMemberStatic, ownerTypeName: className);
                    if (opRes.Error != null) return opRes;

                    var opNode = (RaLanguage.Parser.Nodes.Classes.OperatorDefinitionNode)opRes.Node!;
                    AnnotationAttacher.Attach(opNode, memberAnnotations);
                    operators.Add(opNode);
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "a field declaration ('var' / 'let' / 'const' / 'final'), a method ('fn') or an operator overload ('operator')",
                    contextHint: "class / struct bodies allow only fields, methods and operator overloads"));
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new ClassDefinitionNode(nameTok, isPublic, isAbstract, isStatic, baseType, implementedInterfaces, withTraits, fields, methods, operators, genericTypeParams, whereConstraints));
            }
            finally
            {
                PopGenericScope();
            }
        }

        private ParserResult ParseEnumDefinition()
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Enum))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "enum", context: "to start an enum declaration"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'enum'", help: "enum declarations begin with a name, e.g. 'enum Color { Red, Green, Blue }'"));

            Token nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var members = new List<(Token MemberTok, AstNode? ValueNode)>();

            if (_currentToken.Type != TokenType.RBRACKET)
            {
                while (true)
                {
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "previous enum member or '{'", help: "enum members are comma-separated identifiers (e.g. 'enum E { A, B, C }')"));

                    Token memberTok = _currentToken;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    AstNode? valueNode = null;
                    if (_currentToken.Type == TokenType.EQ)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        valueNode = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                    }

                    members.Add((memberTok, valueNode));

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.RBRACKET)
                            break;

                        continue;
                    }

                    break;
                }
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

            res.RegisterAdvancement();
            Advance();

            return res.Success(new EnumDefinitionNode(nameTok, members, genericTypeParams, whereConstraints));
            }
            finally
            {
                PopGenericScope();
            }
        }

        private ParserResult ParseTryExpression()
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the try block"));

            res.RegisterAdvancement();
            Advance();

            var tryBody = res.Register(ParseStatements());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the try block"));

            res.RegisterAdvancement();
            Advance();

            Token? catchVarTok = null;
            AstNode? catchBody = null;

            int catchLookaheadConsumed = 0;
            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
                catchLookaheadConsumed++;
            }

            if (!_currentToken.Matches(Keyword.Catch) && !_currentToken.Matches(Keyword.Finally) && catchLookaheadConsumed > 0)
            {
                Reverse(catchLookaheadConsumed);
                UpdateCurrentToken();
            }

            if (_currentToken.Matches(Keyword.Catch))
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '(', context: "the catch clause"));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'catch ('", help: "catch clauses bind the thrown value, e.g. 'catch (err) { ... }'"));

                catchVarTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(', context: "the catch binder"));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the catch body"));

                res.RegisterAdvancement();
                Advance();

                catchBody = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the catch block"));

                res.RegisterAdvancement();
                Advance();
            }

            int finallyLookaheadStart = _tokenIndex;
            int finallyLookaheadConsumed = 0;
            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
                finallyLookaheadConsumed++;
            }

            AstNode? finallyBody = null;
            if (!_currentToken.Matches(Keyword.Finally) && finallyLookaheadConsumed > 0)
            {
                Reverse(finallyLookaheadConsumed);
                UpdateCurrentToken();
            }

            if (_currentToken.Matches(Keyword.Finally))
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the finally block"));

                res.RegisterAdvancement();
                Advance();

                finallyBody = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the finally block"));

                res.RegisterAdvancement();
                Advance();
            }

            return res.Success(new TryNode(tryBody, catchVarTok, catchBody, finallyBody));
        }

        private ParserResult ParseDoWhileExpression()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

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
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                }

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (!_currentToken.Matches(Keyword.While))
                {
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "while", context: "to close the loop", help: "do-while loops are 'do { ... } while expr'"));
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
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "while", context: "to close the loop", help: "do-while loops are 'do { ... } while expr'"));
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

            return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "':' or '{'", contextHint: "single-line bodies use ':', multi-line bodies use '{ ... }'"));
        }

        private ParserResult ParseSetExpression()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type == TokenType.RBRACKET)
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new SetNode(new List<AstNode>(), positionStart, _currentToken.PositionEnd));
            }

            var rawElements = new List<(AstNode? Key, AstNode? Value, bool IsPair)>();

            {
                var firstExpr = res.Register(ParseExpression());
                if (res.Error != null)
                {
                    // ParseExpression already produced a more accurate diagnostic
                    // for the offending token inside the set/map literal — propagate it.
                    return res;
                }

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
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "',' or '}'", contextHint: "the map / set literal is comma-separated and ends with '}'"));

            var rBracketEndPos = _currentToken.PositionEnd;
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
                        return res.Failure(ParserDiagnostics.MapAndSetCannotMix(positionStart, rBracketEndPos));
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
            var positionStart = _currentToken.PositionStart;

            if (_currentToken.Type != TokenType.LSQUARE)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '[', context: "the list / array literal"));

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
                        return res.Failure(ParserDiagnostics.ExpectedExprAfterEllipsis(_currentToken));

                    elementNodes.Add(new SpreadNode(spreadTok, spreadExpr));
                }
                else
                {
                    var first = res.Register(ParseExpression());
                    if (res.Error != null)
                    {
                        // ParseExpression already produced a precise diagnostic for the
                        // offending token inside the list literal — propagate it directly.
                        return res;
                    }

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
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "',' or ']'", contextHint: "list literals are comma-separated and end with ']'"));

                res.RegisterAdvancement();
                Advance();
            }

            return res.Success(new ListNode(elementNodes, positionStart, _currentToken.PositionEnd));
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
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, caseKeyword.ToString().ToLowerInvariant()));

            res.RegisterAdvancement();
            Advance();

            var condition = res.Register(ParseExpression());
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();

                var statements = res.Register(ParseStatements());
                if (res.Error != null) return res;

                cases.Add((condition, statements, true));

                if (_currentToken.Type != TokenType.RBRACKET)
                {
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
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

            return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "':' or '{'", contextHint: "single-line bodies start with ':', multi-line bodies use '{ ... }'"));
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

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

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
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
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
            var positionStart = _currentToken.PositionStart;
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LPAREN)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '(', context: "the switch discriminant"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var expr = res.Register(ParseExpression());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RPAREN)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(', context: "the switch discriminant"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the switch body"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

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

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    var labels = new List<AstNode>();
                    var firstLabel = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    labels.Add(firstLabel);

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    while (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        var nextLabel = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                        labels.Add(nextLabel);
                    }

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.COLON)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

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

                        var blockNode = new ScopeNode(stmtList, firstLabel.PositionStart, (stmtList.Count > 0 ? stmtList.Last().PositionEnd : _currentToken.PositionStart));
                        cases.Add(new SwitchCaseNode(labels, false, SwitchCaseSeparator.Colon, blockNode, firstLabel.PositionStart, _currentToken.PositionEnd));
                    }
                    else if (_currentToken.Type == TokenType.ARROW_RIGHT)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        AstNode? body = null;

                        if (_currentToken.Type == TokenType.LBRACKET)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            var stmts = res.Register(ParseStatements());
                            if (res.Error != null) return res;
                            if (_currentToken.Type != TokenType.RBRACKET)
                                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                            res.RegisterAdvancement();
                            Advance();

                            body = stmts;
                        }
                        else
                        {
                            body = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }

                        cases.Add(new SwitchCaseNode(labels, false, SwitchCaseSeparator.Arrow, body, firstLabel.PositionStart, (body ?? firstLabel).PositionEnd));
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                    else
                    {
                        return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "':' or '->'", contextHint: "case bodies use ':' (block) or '->' (single expression)"));
                    }
                }
                else if (_currentToken.Matches(Keyword.Default))
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.COLON)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

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

                        var blockNode = new ScopeNode(stmtList, positionStart, (stmtList.Count > 0 ? stmtList.Last().PositionEnd : _currentToken.PositionStart));
                        cases.Add(new SwitchCaseNode(new List<AstNode>(), true, SwitchCaseSeparator.Colon, blockNode, positionStart, _currentToken.PositionEnd));
                    }
                    else if (_currentToken.Type == TokenType.ARROW_RIGHT)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        AstNode? body = null;
                        if (_currentToken.Type == TokenType.LBRACKET)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            var stmts = res.Register(ParseStatements());
                            if (res.Error != null) return res;
                            if (_currentToken.Type != TokenType.RBRACKET)
                                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                            res.RegisterAdvancement();
                            Advance();
                            body = stmts;
                        }
                        else
                        {
                            body = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }

                        cases.Add(new SwitchCaseNode(new List<AstNode>(), true, SwitchCaseSeparator.Arrow, body, positionStart, body == null ? positionStart : body.PositionEnd));

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                    else
                    {
                        return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "':' or '->'", contextHint: "the default branch uses ':' (block) or '->' (single expression)"));
                    }
                }
                else
                {
                    return res.Failure(ParserDiagnostics.ExpectedCaseOrDefault(_currentToken));
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            res.RegisterAdvancement();
            Advance();

            var switchNode = new SwitchNode(expr, cases, positionStart, _currentToken.PositionEnd);
            return res.Success(switchNode);
        }

        private ParserResult ParseForExpression()
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Matches(Keyword.Await))
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'for await'", help: "syntax: 'for await x in stream { ... }'"));

                var awaitVarName = _currentToken;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (!_currentToken.Matches(Keyword.In))
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "in", context: "after the binder of 'for await'", help: "syntax: 'for await x in stream { ... }'"));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var awaitStreamExpr = res.Register(ParseExpression());
                if (res.Error != null) return res;

                while (_currentToken.Type == TokenType.NEWLINE)
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
                    return res.Success(new RaLanguage.Parser.Nodes.Async.ForAwaitNode(awaitVarName, awaitStreamExpr, bodyInline, false));
                }
                else if (_currentToken.Type == TokenType.LBRACKET)
                {
                    res.RegisterAdvancement();
                    Advance();
                    var bodyBlock = res.Register(ParseStatements());
                    if (res.Error != null) return res;
                    if (_currentToken.Type != TokenType.RBRACKET)
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(new RaLanguage.Parser.Nodes.Async.ForAwaitNode(awaitVarName, awaitStreamExpr, bodyBlock, true));
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "':' or '{'",
                    contextHint: "'for await' bodies use ':' (single statement) or '{ ... }' (block)"));
            }

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
                    return res.Failure(ParserDiagnostics.ExpectedSemicolon(_currentToken));
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
                    return res.Failure(ParserDiagnostics.ExpectedSemicolon(_currentToken));
                }

                if (!skipCheck_2)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                bool skipCheck_3 = false;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

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
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '('));
                }

                if (!skipCheck_3)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                while (_currentToken.Type == TokenType.NEWLINE)
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
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

                    res.RegisterAdvancement();
                    Advance();

                    return res.Success(new SuperForNode(initializationExpressions, conditionExpressions, stepExpressions, body, true));
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "':' or '{'", contextHint: "single-line bodies start with ':', multi-line bodies use '{ ... }'"));
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken));

            var varName = _currentToken;
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type == TokenType.EQ)
            {
                res.RegisterAdvancement();
                Advance();

                var startValue = res.Register(ParseExpression());
                if (res.Error != null) return res;

                if (!_currentToken.Matches(Keyword.To))
                    return res.Failure(ParserDiagnostics.ExpectedRangeTo(_currentToken));

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
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(new ForNode(varName, startValue, endValue, stepValue, body, true));
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "':' or '{'", contextHint: "single-line bodies start with ':', multi-line bodies use '{ ... }'"));
            }
            else if (_currentToken.Matches(Keyword.In))
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var collectionExpr = res.Register(ParseExpression());
                if (res.Error != null) return res;

                while (_currentToken.Type == TokenType.NEWLINE)
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

                    return res.Success(new ForEachNode(varName, collectionExpr, bodyInline, false));
                }
                else if (_currentToken.Type == TokenType.LBRACKET)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var body = res.Register(ParseStatements());
                    if (res.Error != null) return res;

                    if (_currentToken.Type != TokenType.RBRACKET)
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

                    res.RegisterAdvancement();
                    Advance();

                    return res.Success(new ForEachNode(varName, collectionExpr, body, true));
                }
            }

            return res.Failure(ParserDiagnostics.ExpectedForLoopBinder(_currentToken));
        }

        private ParserResult ParseWhileExpression()
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            var condition = res.Register(ParseExpression());
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();

                var body = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

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

            return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "':' or '{'", contextHint: "single-line bodies start with ':', multi-line bodies use '{ ... }'"));
        }

        private ParserResult ParseOperatorDefinition(bool isPublic = false, bool isOverride = false, bool isStatic = false, string? ownerTypeName = null)
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            TokenType opType = _currentToken.Type;
            Keyword? opKeyword = null;
            
            if (_currentToken.Type == TokenType.KEYWORD)
            {
                opKeyword = (Keyword)_currentToken.Value!;

                if (opKeyword != Keyword.And && opKeyword != Keyword.Or)
                {
                    return res.Failure(ParserDiagnostics.InvalidOperatorOverload(_currentToken));
                }
            }
            else if (!IsOperatorToken(_currentToken.Type))
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentToken.PositionStart,
                    _currentToken.PositionEnd,
                    $"expected an operator symbol but found {DescribeToken(_currentToken)}",
                    DiagnosticCode.ParserExpectedToken,
                    help: "overloadable operators are '+', '-', '*', '/', '==', '!=', '<', '>', '<=', '>=', '&', '|', '^', '<<', '>>', 'and' or 'or'",
                    primaryLabel: "operator symbol expected here"));
            }

            var operatorTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            if (_currentToken.Type != TokenType.LPAREN)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '('));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken));

            var argNameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            TypeDescriptor? argType = null;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();
                argType = ParseType(res);
                if (argType == null)
                    return res.Failure(ParserDiagnostics.ExpectedTypeName(_currentToken, after: "':'"));
            }
            else
            {
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd,
                    "operator parameters require an explicit type annotation",
                    DiagnosticCode.ParserExpectedType,
                    help: "annotate each parameter with ': Type', e.g. 'operator+(rhs: Vec)'",
                    primaryLabel: "missing parameter type"));
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.RPAREN)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '('));

            res.RegisterAdvancement();
            Advance();

            TypeDescriptor? returnType = null;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();
                returnType = ParseType(res);
                if (returnType == null)
                    return res.Failure(ParserDiagnostics.ExpectedReturnType(_currentToken));
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            AstNode? bodyNode = null;
            bool shouldAutoReturn = false;

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();

                var scope = res.Register(ParseStatements());
                if (res.Error != null) return res;
                bodyNode = scope;

                res.RegisterAdvancement();
                Advance();
            }
            else if (_currentToken.Type == TokenType.ARROW)
            {
                shouldAutoReturn = true;
                res.RegisterAdvancement();
                Advance();

                var expr = res.Register(ParseStatement());
                if (res.Error != null) return res;
                bodyNode = expr;
            }
            else
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "'{' (multi-statement body) or '->' (expression body)",
                    contextHint: "operator overloads need a body: '{ ... }' or '=> expr'"));
            }

            if (bodyNode == null)
                return res.Failure(ParserDiagnostics.ExpectedOperatorBody(_currentToken));

            return res.Success(new RaLanguage.Parser.Nodes.Classes.OperatorDefinitionNode(
                isPublic, isOverride, isStatic, operatorTok, argNameTok, argType, returnType, bodyNode, shouldAutoReturn, genericTypeParams, whereConstraints));
            }
            finally
            {
                PopGenericScope();
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
                TokenType.BITWISE_RIGHT_SHIFT => true,
                _ => false
            };
        }

        private ParserResult ParseAsyncFunctionDefinition(bool isPublic = false)
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Async))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "async", context: "to start an async function declaration"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            bool isAsyncStream = false;
            if (_currentToken.Type == TokenType.IDENTIFIER && string.Equals(_currentToken.Value?.ToString(), "stream", System.StringComparison.Ordinal))
            {
                isAsyncStream = true;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            if (!_currentToken.Matches(Keyword.Fn))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "fn",
                    context: "after 'async' (or 'async stream')",
                    help: "async functions are declared 'async fn ...' or 'async stream fn ...'"));

            return ParseFunctionDefinition(isPublic: isPublic, isAsync: true, isAsyncStream: isAsyncStream);
        }

        private ParserResult ParseFunctionDefinition(string? ownerTypeName = null, bool isPublic = false, bool isOverride = false, bool isAbstract = false, bool isStatic = false, bool isDeclaringConstructor = false, bool isAsync = false, bool isAsyncStream = false)
        {
            var res = new ParserResult();

            if (!isDeclaringConstructor)
            {
                if (!_currentToken.Matches(Keyword.Fn))
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "fn", context: "to begin a function declaration"));

                res.RegisterAdvancement();
                Advance();
            }
            else
            {
                if (_currentToken.Matches(Keyword.Fn))
                {
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd,
                    "constructors must not be preceded by the 'fn' keyword",
                    DiagnosticCode.ParserInvalidSyntax,
                    help: "declare constructors using the type name directly, e.g. 'Point(x, y) { ... }'",
                    primaryLabel: "unexpected 'fn' before constructor"));
                }
            }

            Token? varNameTok = null;
            var genericTypeParams = new List<string>();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type == TokenType.IDENTIFIER)
            {
                varNameTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
                if (res.Error != null) return res;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '('));
            }
            else if (_currentToken.Type == TokenType.LT)
            {
                res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
                if (res.Error != null) return res;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '(', context: "the parameter list (after the generic type parameters)"));
            }
            else
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "a function name or '('",
                        contextHint: "function declarations need either a name (e.g. 'fn foo(...)') or '(' for an anonymous function"));
            }

            PushGenericScope(genericTypeParams);
            try
            {
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var argNameToks = new List<Token>();
            var argTypes = new List<TypeDescriptor?>();
            var isRefParams = new List<bool>();
            var paramDefaults = new List<AstNode?>();
            var paramAnnotations = new List<List<AnnotationApplicationNode>?>();
            List<AnnotationApplicationNode>? varArgAnnotations = null;
            bool hasVarArgs = false;
            Token? varArgNameTok = null;
            TypeDescriptor? varArgType = null;

            bool sawDefault = false;

            if (_currentToken.Type == TokenType.RPAREN)
            {
                goto otherRparen;
            }

            if (_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.SPREAD || _currentToken.Matches(Keyword.Ref) || _currentToken.Type == TokenType.AT_SIGN)
            {
                while (true)
                {
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    List<AnnotationApplicationNode>? pendingParamAnnotations = null;
                    if (_currentToken.Type == TokenType.AT_SIGN)
                    {
                        var (annList, annErr) = ParseAnnotationListInline(res);
                        if (annErr != null) return res.Failure(annErr);
                        pendingParamAnnotations = annList;
                    }

                    if (_currentToken.Type == TokenType.SPREAD)
                    {
                        hasVarArgs = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'...'", help: "variadic parameters take an identifier, e.g. '...args: int'"));

                        varArgNameTok = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            var parsed = ParseType(res);
                            if (parsed == null)
                                return res.Failure(ParserDiagnostics.ExpectedVarArgsType(_currentToken));

                            varArgType = parsed;
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                            return res.Failure(ParserDiagnostics.VariadicMustBeLast(_currentToken.PositionStart, _currentToken.PositionEnd));

                        varArgAnnotations = pendingParamAnnotations;
                        break;
                    }
                    else
                    {
                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        bool isRef = false;
                        if (_currentToken.Matches(Keyword.Ref))
                        {
                            isRef = true;
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }
                        }

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken));

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        var paramTok = _currentToken;
                        argNameToks.Add(paramTok);
                        paramAnnotations.Add(pendingParamAnnotations);
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        TypeDescriptor? ptype = null;
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            var parsed = ParseType(res);
                            if (parsed == null)
                                return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));

                            if (isRef)
                            {
                                ptype = TypeDescriptor.RefType(parsed);
                            }
                            else
                            {
                                ptype = parsed;
                            }
                        }
                        argTypes.Add(ptype);
                        isRefParams.Add(isRef);

                        AstNode? defaultExpr = null;
                        if (_currentToken.Type == TokenType.EQ)
                        {
                            sawDefault = true;
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            defaultExpr = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }
                        else if (sawDefault)
                        {
                            return res.Failure(ParserDiagnostics.DefaultParameterMustBeTrailing(_currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        paramDefaults.Add(defaultExpr);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            if (_currentToken.Type == TokenType.RPAREN) break;
                            continue;
                        }

                        break;
                    }
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "',' or ')'", contextHint: "the parameter / argument list is comma-separated and ends with ')'"));
            }
            else
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "a parameter name or ')'", contextHint: "parameter lists begin with names or '...' and end with ')'"));
            }

            otherRparen:  res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            TypeDescriptor? returnType = null;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var parsed = ParseType(res);
                if (parsed == null)
                    return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "the return type annotation"));

                returnType = parsed;
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            bool isConstructor = ownerTypeName != null
                                 && varNameTok != null
                                 && string.Equals(varNameTok.Value.ToString(), ownerTypeName, StringComparison.Ordinal);

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type == TokenType.ARROW)
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var body = res.Register(ParseExpression());
                if (res.Error != null) return res;

                return res.Success(new FunctionDefinitionNode(
                    varNameTok,
                    argNameToks,
                    argTypes,
                    isRefParams,
                    paramDefaults,
                    hasVarArgs,
                    varArgNameTok,
                    varArgType,
                    returnType,
                    body,
                    true,
                    genericTypeParams,
                    isPublic,
                    isConstructor,
                    isOverride,
                    isAbstract,
                    isStatic,
                    whereConstraints,
                    paramAnnotations
                ) { VarArgAnnotations = varArgAnnotations, IsAsync = isAsync, IsAsyncStream = isAsyncStream });
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
            {
                return res.Success(new FunctionDefinitionNode(
                    varNameTok,
                    argNameToks,
                    argTypes,
                    isRefParams,
                    paramDefaults,
                    hasVarArgs,
                    varArgNameTok,
                    varArgType,
                    returnType,
                    null,
                    false,
                    genericTypeParams,
                    isPublic,
                    isConstructor,
                    isOverride,
                    isAbstract,
                    isStatic,
                    whereConstraints,
                    paramAnnotations
                ) { VarArgAnnotations = varArgAnnotations, IsAsync = isAsync, IsAsyncStream = isAsyncStream });
            }

            res.RegisterAdvancement();
            Advance();

            var bodyStmts = res.Register(ParseStatements());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

            res.RegisterAdvancement();
            Advance();

            return res.Success(new FunctionDefinitionNode(
                varNameTok,
                argNameToks,
                argTypes,
                isRefParams,
                paramDefaults,
                hasVarArgs,
                varArgNameTok,
                varArgType,
                returnType,
                bodyStmts,
                false,
                genericTypeParams,
                isPublic,
                isConstructor,
                isOverride,
                isAbstract,
                isStatic,
                whereConstraints,
                paramAnnotations
            ) { VarArgAnnotations = varArgAnnotations, IsAsync = isAsync, IsAsyncStream = isAsyncStream });
            }
            finally
            {
                PopGenericScope();
            }
        }

        private ParserResult ParseStructDefinition(bool isPublic)
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'struct'", help: "struct declarations begin with a name, e.g. 'struct Point { x: int, y: int }'"));

            var nameTok = _currentToken;
            var structName = nameTok.Value?.ToString() ?? "";

            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var fields = new List<StructFieldDefinitionNode>();
            var methods = new List<StructMethodDefinitionNode>();
            var operators = new List<OperatorDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                List<AnnotationApplicationNode>? memberAnnotations = null;
                if (_currentToken.Type == TokenType.AT_SIGN)
                {
                    var (annList, annErr) = ParseAnnotationListInline(res);
                    if (annErr != null) return res.Failure(annErr);
                    memberAnnotations = annList;
                }

                bool memberPublic = false;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

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
                        var fieldNode = new StructFieldDefinitionNode(
                            declNode.IsPublic,
                            d.Item1,
                            d.Item3,
                            d.Item2,
                            false,
                            false,
                            false,
                            declNode.DeclarationType
                        );
                        AnnotationAttacher.Attach(fieldNode, memberAnnotations);
                        fields.Add(fieldNode);
                    }

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                bool memberIsAsync = false;
                bool memberIsAsyncStream = false;
                if (_currentToken.Matches(Keyword.Async))
                {
                    memberIsAsync = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.IDENTIFIER && string.Equals(_currentToken.Value?.ToString(), "stream", System.StringComparison.Ordinal))
                    {
                        memberIsAsyncStream = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                }

                if (_currentToken.Matches(Keyword.Fn) || (_currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == structName))
                {
                    var fnRes = ParseFunctionDefinition(ownerTypeName: structName, isPublic: memberPublic, isDeclaringConstructor: _currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == structName, isAsync: memberIsAsync, isAsyncStream: memberIsAsyncStream);
                    if (fnRes.Error != null) return fnRes;

                    var methodNode = (FunctionDefinitionNode)fnRes.Node!;
                    AnnotationAttacher.Attach(methodNode, memberAnnotations);
                    methods.Add(new StructMethodDefinitionNodeFromFunctionDefinition(methodNode));
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Operator))
                {
                    var opRes = ParseOperatorDefinition(isPublic: memberPublic, ownerTypeName: null);
                    if (opRes.Error != null) return opRes;

                    var opNode = (OperatorDefinitionNode)opRes.Node!;
                    AnnotationAttacher.Attach(opNode, memberAnnotations);
                    operators.Add(opNode);
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "a field declaration ('var' / 'let' / 'const' / 'final'), a method ('fn') or an operator overload ('operator')",
                    contextHint: "class / struct bodies allow only fields, methods and operator overloads"));
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new StructDefinitionNode(nameTok, isPublic, fields, methods, operators, genericTypeParams, whereConstraints));
            }
            finally
            {
                PopGenericScope();
            }
        }

        private ParserResult ParseVariableDeclaration(bool isPublic = false, bool isStatic = false)
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
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken));

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
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));
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

            return res.Success(new VariableDeclarationNode(variableDeclarationType, declarations, isPublic, isStatic));
        }

        private ParserResult ParseInterfaceFieldDeclaration(bool isPublic = false)
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
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken));

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
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));
                    }
                    declaredType = parsedType;
                }

                if (_currentToken.Type == TokenType.EQ)
                {
                    return res.Failure(ParserDiagnostics.InterfaceFieldHasDefault(_currentToken.PositionStart, _currentToken.PositionEnd));
                }

                declarations.Add((varName, null, declaredType));

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

            return res.Success(new VariableDeclarationNode(variableDeclarationType, declarations, isPublic, false));
        }

        private ParserResult ParseTraitFieldDeclaration(bool isPublic = false)
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
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken));

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
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));
                    }
                    declaredType = parsedType;
                }

                AstNode? defaultValueNode = null;
                if (_currentToken.Type == TokenType.EQ)
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    defaultValueNode = res.Register(ParseExpression());
                    if (res.Error != null)
                    {
                        return res;
                    }
                }

                declarations.Add((varName, defaultValueNode, declaredType));

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

            return res.Success(new VariableDeclarationNode(variableDeclarationType, declarations, isPublic, false));
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

        private (List<AnnotationApplicationNode>? List, Errors.Error? Error) ParseAnnotationListInline(ParserResult outerRes)
        {
            if (_currentToken.Type != TokenType.AT_SIGN)
                return (null, null);

            var list = new List<AnnotationApplicationNode>();

            while (_currentToken.Type == TokenType.AT_SIGN)
            {
                var (node, err) = ParseSingleAnnotationApplication(outerRes);
                if (err != null) return (null, err);
                list.Add(node!);

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    outerRes.RegisterAdvancement();
                    Advance();
                }
            }

            return (list, null);
        }

        private (AnnotationApplicationNode? Node, Errors.Error? Error) ParseSingleAnnotationApplication(ParserResult outerRes)
        {
            if (_currentToken.Type != TokenType.AT_SIGN)
                return (null, ParserDiagnostics.ExpectedAtSign(_currentToken));

            var startPos = _currentToken.PositionStart;
            outerRes.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                outerRes.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return (null, ParserDiagnostics.ExpectedAnnotationName(_currentToken, after: "'@'"));

            var nameTok = _currentToken;
            Position endPos = nameTok.PositionEnd;
            outerRes.RegisterAdvancement();
            Advance();

            var positional = new List<AstNode>();
            var named = new List<(Token, AstNode)>();

            if (_currentToken.Type == TokenType.LPAREN)
            {
                outerRes.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    outerRes.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RPAREN)
                {
                    while (true)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            outerRes.RegisterAdvancement();
                            Advance();
                        }

                        Token? namedKey = null;
                        if (_currentToken.Type == TokenType.IDENTIFIER &&
                            _tokenIndex + 1 < _tokens.Count &&
                            _tokens[_tokenIndex + 1].Type == TokenType.EQ)
                        {
                            namedKey = _currentToken;
                            outerRes.RegisterAdvancement();
                            Advance();
                            outerRes.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                outerRes.RegisterAdvancement();
                                Advance();
                            }
                        }

                        var exprRes = ParseExpression();
                        var exprNode = outerRes.Register(exprRes);
                        if (outerRes.Error != null) return (null, outerRes.Error);

                        if (namedKey != null) named.Add((namedKey.Value, exprNode));
                        else positional.Add(exprNode);

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            outerRes.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            outerRes.RegisterAdvancement();
                            Advance();
                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                outerRes.RegisterAdvancement();
                                Advance();
                            }
                            if (_currentToken.Type == TokenType.RPAREN) break;
                            continue;
                        }

                        break;
                    }
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return (null, ParserDiagnostics.UnexpectedToken(_currentToken,
                        "',' or ')'",
                        contextHint: "annotation argument lists are comma-separated and end with ')'"));

                endPos = _currentToken.PositionEnd;
                outerRes.RegisterAdvancement();
                Advance();
            }

            return (new AnnotationApplicationNode(nameTok, positional, named, startPos, endPos), null);
        }

        private ParserResult ParseAnnotationDefinition(bool isPublic)
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Annotation))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "annotation", context: "to start an annotation declaration"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedAnnotationName(_currentToken, after: "'annotation'"));

            var nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            var parameters = new List<AnnotationParameterNode>();

            if (_currentToken.Type == TokenType.LPAREN)
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RPAREN)
                {
                    bool sawDefault = false;
                    while (true)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        bool isVarArgs = false;
                        if (_currentToken.Type == TokenType.SPREAD)
                        {
                            isVarArgs = true;
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken));

                        var paramName = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        TypeDescriptor? paramType = null;
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            paramType = ParseType(res);
                            if (paramType == null)
                                return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "an annotation parameter"));
                        }

                        AstNode? defaultValue = null;
                        if (_currentToken.Type == TokenType.EQ)
                        {
                            sawDefault = true;
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            defaultValue = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }
                        else if (sawDefault && !isVarArgs)
                        {
                            return res.Failure(ParserDiagnostics.DefaultParameterMustBeTrailing(_currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        parameters.Add(new AnnotationParameterNode(paramName, paramType, defaultValue, isVarArgs));

                        if (isVarArgs) break;

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }
                            if (_currentToken.Type == TokenType.RPAREN) break;
                            continue;
                        }

                        break;
                    }
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(', context: "the annotation parameter list"));

                res.RegisterAdvancement();
                Advance();
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(ParserDiagnostics.AnnotationBodyMustBeEmpty(_currentToken.PositionStart, _currentToken.PositionEnd));
                res.RegisterAdvancement();
                Advance();
            }

            var defNode = new AnnotationDefinitionNode(nameTok, isPublic, parameters);
            defNode.PositionEnd = _currentToken.PositionEnd;
            return res.Success(defNode);
        }

        private ParserResult ParseAsmBlock()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            if (!_currentToken.Matches(Keyword.Asm))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "asm", context: "to start an inline assembly block"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            var returnTypes = new List<string>();

            if (_currentToken.Type == TokenType.ARROW_RIGHT)
            {
                res.RegisterAdvancement();
                Advance();
                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

                if (_currentToken.Type == TokenType.LPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();
                    while (_currentToken.Type != TokenType.RPAREN && _currentToken.Type != TokenType.EOF)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE || _currentToken.Type == TokenType.COMMA) { res.RegisterAdvancement(); Advance(); }
                        if (_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.KEYWORD)
                        {
                            returnTypes.Add(_currentToken.Value?.ToString() ?? "");
                            res.RegisterAdvancement();
                            Advance();
                        }
                        else break;
                    }
                    if (_currentToken.Type != TokenType.RPAREN)
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(', context: "the asm return type list"));
                    res.RegisterAdvancement();
                    Advance();
                }
                else if (_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.KEYWORD)
                {
                    returnTypes.Add(_currentToken.Value?.ToString() ?? "");
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    return res.Failure(ParserDiagnostics.ExpectedReturnType(_currentToken));
                }
            }

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the asm block"));

            res.RegisterAdvancement();
            Advance();

            var parts = new List<AstNode>();

            while (_currentToken.Type == TokenType.ASM_TEXT || _currentToken.Type == TokenType.INTERP_START)
            {
                if (_currentToken.Type == TokenType.ASM_TEXT)
                {
                    var textTok = _currentToken;
                    parts.Add(new RaLanguage.Parser.Nodes.Asm.AsmTextPartNode(textTok.Value?.ToString() ?? "", textTok.PositionStart, textTok.PositionEnd));
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    var interpStartPos = _currentToken.PositionStart;
                    res.RegisterAdvancement();
                    Advance();

                    var expr = res.Register(ParseExpression());
                    if (res.Error != null) return res;

                    if (_currentToken.Type != TokenType.INTERP_END)
                        return res.Failure(ParserDiagnostics.ExpectedAsmInterpClose(_currentToken));

                    string? typeHint = _currentToken.Value as string;
                    var interpEndPos = _currentToken.PositionEnd;
                    res.RegisterAdvancement();
                    Advance();

                    parts.Add(new RaLanguage.Parser.Nodes.Asm.AsmInterpPartNode(expr, typeHint, interpStartPos, interpEndPos));
                }
            }

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the asm block"));

            var positionEnd = _currentToken.PositionEnd;
            res.RegisterAdvancement();
            Advance();

            var node = new RaLanguage.Parser.Nodes.Asm.AsmBlockNode(parts, positionStart, positionEnd);
            node.ReturnTypes = returnTypes;
            return res.Success(node);
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
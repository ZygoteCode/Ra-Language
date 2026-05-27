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

            var leftNode = res.Register(ParsePipelineExpression());

            if (res.Error != null)
            {
                // ParsePipelineExpression / inner parsers already produced a
                // precise diagnostic for the offending token; bubble it up
                // untouched.
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
                else if (leftNode.NodeType == AstNodeType.Dereference)
                {
                    DereferenceNode derefNode = (DereferenceNode)leftNode;
                    return res.Success(new DereferenceAssignmentNode(
                        derefNode.Target, assignmentToken, rightNode,
                        leftNode.PositionStart, rightNode.PositionEnd));
                }
                else
                {
                    return res.Failure(ParserDiagnostics.InvalidAssignmentTarget(
                        leftNode.PositionStart, leftNode.PositionEnd,
                        "only variables, indexed access (a[i]), member access (a.b), and dereferences (*ref) may appear on the left of an assignment"));
                }
            }

            return res.Success(leftNode);
        }

        // Pipeline layer sits between the cast layer (which is itself the
        // tightest non-assignment band) and assignment / ternary. Left
        // associative so `a |> b |> c` parses as `c(b(a))`.
        //
        // The right-hand expression is parsed at the same precedence band as
        // the left so a call expression on the RHS (`value |> pow(2)`) binds
        // naturally without requiring parentheses.
        private ParserResult ParsePipelineExpression()
        {
            var res = new ParserResult();
            var left = res.Register(ParseCastExpression());
            if (res.Error != null) return res;

            while (true)
            {
                // Allow newline(s) between pipeline stages so multiline chains
                //   value
                //     |> first
                //     |> second
                // parse as a single expression. Only commit to consuming the
                // newlines once we have confirmed `|>` is the next significant
                // token — otherwise we leave the stream untouched so the outer
                // statement parser can see the newline as a terminator.
                int rewindTo = _tokenIndex;
                int rewindAdvanceCount = 0;
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    rewindAdvanceCount++;
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.PIPE_FORWARD)
                {
                    if (rewindAdvanceCount > 0)
                    {
                        _tokenIndex = rewindTo;
                        UpdateCurrentToken();
                        // RegisterAdvancement only adjusts diagnostics' "last
                        // advance count" - it has no side-effect on the stream.
                        // Rewinding _tokenIndex is enough to retract.
                    }
                    break;
                }

                var pipeTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                // Also tolerate newlines directly after `|>`.
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.EOF
                    || _currentToken.Type == TokenType.PIPE_FORWARD)
                {
                    return res.Failure(ParserDiagnostics.PipelineMissingRhs(pipeTok));
                }

                var right = res.Register(ParseCastExpression());
                if (res.Error != null) return res;

                left = new PipelineNode(left, right, pipeTok);
            }

            return res.Success(left);
        }

        // Parses the historical cast-loop body (`expr (as Type)*`) at a
        // precedence band higher than pipeline and assignment. Extracted from
        // ParseExpression so the pipeline layer can sit above it cleanly.
        private ParserResult ParseCastExpression()
        {
            var res = new ParserResult();
            // `or` sits at the lowest band, `and` binds tighter; both are
            // above bitwise-or. This matches the Python / Ruby precedence
            // most users expect.
            var leftNode = res.Register(ParseBinaryOperation(ParseLogicalAndExpression, s_opsLogicalOr));
            if (res.Error != null) return res;

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

            return res.Success(leftNode);
        }

        private ParserResult ParseLogicalAndExpression()
        {
            return ParseBinaryOperation(ParseBitwiseOrExpression, s_opsLogicalAnd);
        }

        private ParserResult ParseBitwiseOrExpression()
        {
            return ParseBinaryOperation(ParseBitwiseAndExpression, s_opsBitwiseOr);
        }

        private ParserResult ParseBitwiseAndExpression()
        {
            return ParseBinaryOperation(ParseComparisonExpression, s_opsBitwiseAnd);
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

            if (_currentToken.Matches(Keyword.Not))
            {
                var opTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                var node = res.Register(ParseComparisonExpression());
                if (res.Error != null) return res;
                return res.Success(new UnaryOperationNode(opTok, node));
            }

            var b_node = res.Register(ParseBinaryOperation(ParseNullCoalescing, s_opsComparison));

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
            return ParseBinaryOperation(ParseRangeExpression, s_opsShift);
        }

        private ParserResult ParseArithmeticExpression()
        {
            return ParseBinaryOperation(ParseTerm, s_opsArith);
        }

        private ParserResult ParseTerm()
        {
            return ParseBinaryOperation(ParseFactor, s_opsTerm);
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

            // Bitwise NOT binds at the factor level so `~0 + 1` parses as
            // `(~0) + 1`, not `~(0 + 1)`.
            if (tok.Type == TokenType.BITWISE_NOT)
            {
                res.RegisterAdvancement();
                Advance();
                var factor = res.Register(ParseFactor());
                if (res.Error != null) return res;
                return res.Success(new UnaryOperationNode(tok, factor, isLeft: true));
            }

            // Unary borrow: `&place` (shared) or `&mut place` (exclusive). Optional
            // lifetime annotation slots between: `&'a place` / `&'a mut place`.
            // The `&` is BITWISE_AND in tokenstream; at factor-start position it can
            // never be a binary operand, so reinterpretation is unambiguous.
            if (tok.Type == TokenType.BITWISE_AND)
            {
                var posStart = tok.PositionStart;
                res.RegisterAdvancement();
                Advance();

                string? lifetime = null;
                if (_currentToken.Type == TokenType.LIFETIME)
                {
                    lifetime = _currentToken.Value?.ToString();
                    res.RegisterAdvancement();
                    Advance();
                }

                bool isMut = false;
                if (_currentToken.Matches(Keyword.Mut))
                {
                    isMut = true;
                    res.RegisterAdvancement();
                    Advance();
                }

                var target = res.Register(ParseFactor());
                if (res.Error != null) return res;
                return res.Success(new BorrowNode(target, isMut, posStart, target.PositionEnd, lifetime));
            }

            // Unary dereference: `*expr`. Same factor-start disambiguation as `&`.
            if (tok.Type == TokenType.MUL)
            {
                var posStart = tok.PositionStart;
                res.RegisterAdvancement();
                Advance();
                var target = res.Register(ParseFactor());
                if (res.Error != null) return res;
                return res.Success(new DereferenceNode(target, posStart, target.PositionEnd));
            }

            return ParsePower();
        }

        private ParserResult ParsePower()
        {
            return ParseBinaryOperation(ParseCall, s_opsPow, ParseFactor);
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
                || _currentToken.Matches(Keyword.Not)
                || (_currentToken.Matches(Keyword.With)
                    && _tokenIndex + 1 < _tokens.Count
                    && _tokens[_tokenIndex + 1].Type == TokenType.LBRACKET))
            {
                if (_currentToken.Matches(Keyword.With))
                {
                    res.RegisterAdvancement();
                    Advance();

                    // Consume `{`.
                    res.RegisterAdvancement();
                    Advance();

                    var updates = new List<(Token, AstNode)>();
                    var seenWithNames = new HashSet<string>(StringComparer.Ordinal);

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    while (_currentToken.Type != TokenType.RBRACKET)
                    {
                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                                after: "the opening brace of a record-update",
                                help: "record-update syntax: 'expr with { name: value, name: value }'"));

                        var nameTok = _currentToken;
                        var name = nameTok.Value?.ToString() ?? "";

                        if (!seenWithNames.Add(name))
                        {
                            return res.Failure(ParserDiagnostics.UnexpectedToken(nameTok,
                                $"a unique field name (duplicate '{name}')",
                                contextHint: "record-update lists may not name the same field twice"));
                        }

                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type != TokenType.COLON)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                                "':'",
                                contextHint: "record-update pairs are 'name: value'"));

                        res.RegisterAdvancement();
                        Advance();

                        var valExpr = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                        updates.Add((nameTok, valExpr));

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

                            continue;
                        }

                        break;
                    }

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type != TokenType.RBRACKET)
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{',
                            context: "the record-update list"));

                    res.RegisterAdvancement();
                    Advance();

                    resultNode = new RaLanguage.Parser.Nodes.Operations.WithExpressionNode(resultNode, updates);
                    continue;
                }

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

            // Postfix `?` try-unwrap. Disambiguated against the ternary `?:`
            // by peeking the next token: if it cannot start an expression, the
            // `?` is the unwrap operator. Otherwise we leave it for the
            // ternary parser higher in the chain. Allowed terminators include
            // NEWLINE, EOF, COMMA, RPAREN/RSQUARE/RBRACKET, ARROW(_RIGHT),
            // PIPE_FORWARD, DOT (for `.chain` after unwrap), QUESTION_MARK
            // (allows `expr??` chaining), and `as` (cast after unwrap).
            while (_currentToken.Type == TokenType.QUESTION_MARK && IsTryUnwrapNext())
            {
                var qTok = _currentToken;
                res.RegisterAdvancement();
                Advance();
                resultNode = new RaLanguage.Parser.Nodes.Patterns.TryUnwrapNode(resultNode, resultNode.PositionStart, qTok.PositionEnd);
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
                case TokenType.REGEX_LITERAL:
                {
                    var payload = (Lexer.Tokens.RegexLiteralPayload)tok.Value!;
                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(new RegexLiteralNode(payload.Pattern, payload.Flags, tok.PositionStart, tok.PositionEnd));
                }
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
                            var interpStart = _currentToken.PositionStart;
                            res.RegisterAdvancement();
                            Advance();

                            var expr2 = res.Register(ParseExpression());
                            if (res.Error != null) return res;

                            // Optional `:spec` form. The lexer pre-validates the
                            // spec text and emits FORMAT_SPEC right before
                            // INTERP_END when present, so we just attach it.
                            AstNode segmentNode = expr2;
                            if (_currentToken.Type == TokenType.FORMAT_SPEC)
                            {
                                var specTok = _currentToken;
                                string rawSpec = specTok.Value?.ToString() ?? string.Empty;
                                var parsedSpec = Types.Formatting.FormatSpec.Parse(rawSpec);
                                if (parsedSpec.IsDefault && rawSpec.Length > 0)
                                {
                                    return res.Failure(ParserDiagnostics.InvalidFormatSpec(specTok, rawSpec));
                                }
                                segmentNode = new FormattedInterpolationNode(expr2, parsedSpec, rawSpec, interpStart, specTok.PositionEnd);
                                res.RegisterAdvancement();
                                Advance();
                            }

                            if (_currentToken.Type != TokenType.INTERP_END)
                            {
                                return res.Failure(ParserDiagnostics.ExpectedInterpClose(_currentToken));
                            }

                            res.RegisterAdvancement();
                            Advance();

                            parts.Add(segmentNode);
                            posEnd = segmentNode.PositionEnd;
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
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Match:
                    var matchExpr = res.Register(ParseMatchExpression());
                    if (res.Error != null) return res;
                    return res.Success(matchExpr);
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
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Record:
                    var recordExpr = res.Register(ParseRecordDefinition(false, isAbstract: false));
                    if (res.Error != null) return res;
                    return res.Success(recordExpr);
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Abstract:
                {
                    // Bare `abstract record class X(...) : Base(...)` —
                    // accepted at top-level without the `pub` prefix. The
                    // only abstract-able shape outside `pub` today is a
                    // record class; classes still require the `pub`
                    // route. Peek past whitespace to ensure `record` is
                    // next; otherwise bubble the error.
                    res.RegisterAdvancement();
                    Advance();
                    while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                    if (!_currentToken.Matches(Keyword.Record))
                        return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                            "'record' after 'abstract'",
                            contextHint: "bare 'abstract' at top-level may only precede 'record class' — use 'pub abstract class' for classes"));
                    var absRec = res.Register(ParseRecordDefinition(false, isAbstract: true));
                    if (res.Error != null) return res;
                    return res.Success(absRec);
                }
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
                case TokenType.KEYWORD when ((Keyword)tok.Value) == Keyword.Delegate:
                    var delDef = res.Register(ParseDelegateDefinition(false));
                    if (res.Error != null) return res;
                    return res.Success(delDef);
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

    }
}

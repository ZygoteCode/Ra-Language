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

        // ============================================================
        // match expression
        //
        //   match expr {
        //       case Pattern (if guard)? -> body
        //       case OtherPattern -> body
        //   }
        //
        // Patterns live in Parser/Nodes/Patterns/. The visitor evaluates the
        // scrutinee once, walks each arm in source order, and runs the body
        // of the first arm whose pattern + guard succeeds. Exhaustiveness is
        // analysed statically before execution (see StaticAnalyzer).
        // ============================================================
        private ParserResult ParseMatchExpression()
        {
            var res = new ParserResult();
            var posStart = _currentToken.PositionStart;
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var scrutinee = res.Register(ParseExpression());
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

            var arms = new List<RaLanguage.Parser.Nodes.Patterns.MatchArmNode>();

            while (true)
            {
                while (_currentToken.Type == TokenType.NEWLINE || _currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET) break;

                if (!_currentToken.Matches(Lexer.Tokens.Keyword.Case))
                {
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "case",
                        context: "inside a match block; each arm starts with 'case <pattern> -> <body>'"));
                }

                var armStart = _currentToken.PositionStart;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var pattern = ParsePattern(res);
                if (res.Error != null) return res;

                AstNode? guard = null;
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Matches(Lexer.Tokens.Keyword.If))
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    guard = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.ARROW && _currentToken.Type != TokenType.ARROW_RIGHT)
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "'->' or '=>' to introduce the arm body",
                        contextHint: "match arms have the shape 'case <pattern> -> <expression>'"));
                }

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                AstNode body;
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
                    body = stmts!;
                }
                else
                {
                    body = res.Register(ParseExpression())!;
                    if (res.Error != null) return res;
                }

                arms.Add(new RaLanguage.Parser.Nodes.Patterns.MatchArmNode(pattern!, guard, body, armStart, _currentToken.PositionStart));
            }

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

            var posEnd = _currentToken.PositionEnd;
            res.RegisterAdvancement();
            Advance();

            return res.Success(new RaLanguage.Parser.Nodes.Patterns.MatchNode(scrutinee!, arms, posStart, posEnd));
        }

        // Single pattern. Distinguishes:
        //   _                          → wildcard
        //   123 / "x" / true / null    → literal
        //   ident                      → variable binding (or shorthand variant
        //                                 when name resolves to a constructor;
        //                                 the engine decides at runtime)
        //   ident(p1, p2)              → variant pattern with payload subs
        //   ident.member(p1)?          → qualified variant pattern
        //   ident { field, field: p }  → struct pattern
        //   (p1, p2, ...)              → tuple pattern (2+ elements) / paren
        //   [p1, p2, ..rest]           → list pattern with optional rest
        //   ..ident?                   → rest pattern (only inside lists)
        private RaLanguage.Parser.Nodes.Patterns.PatternNode? ParsePattern(ParserResult res)
        {
            var tok = _currentToken;

            switch (tok.Type)
            {
                case TokenType.IDENTIFIER:
                {
                    string name = tok.Value?.ToString() ?? "";

                    if (name == "_")
                    {
                        res.RegisterAdvancement();
                        Advance();
                        return new RaLanguage.Parser.Nodes.Patterns.WildcardPatternNode(tok.PositionStart, tok.PositionEnd);
                    }

                    res.RegisterAdvancement();
                    Advance();

                    string? enumName = null;
                    string variantName = name;

                    if (_currentToken.Type == TokenType.DOT)
                    {
                        res.RegisterAdvancement();
                        Advance();
                        if (_currentToken.Type != TokenType.IDENTIFIER)
                        {
                            res.Failure(ParserDiagnostics.ExpectedMemberName(_currentToken));
                            return null;
                        }
                        enumName = name;
                        variantName = _currentToken.Value?.ToString() ?? "";
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.LPAREN)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        var subs = new List<RaLanguage.Parser.Nodes.Patterns.PatternNode>();
                        if (_currentToken.Type != TokenType.RPAREN)
                        {
                            while (true)
                            {
                                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                                var sub = ParsePattern(res);
                                if (res.Error != null) return null;
                                subs.Add(sub!);
                                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                                if (_currentToken.Type == TokenType.COMMA)
                                {
                                    res.RegisterAdvancement();
                                    Advance();
                                    continue;
                                }
                                break;
                            }
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                        {
                            res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '('));
                            return null;
                        }

                        var end = _currentToken.PositionEnd;
                        res.RegisterAdvancement();
                        Advance();

                        return new RaLanguage.Parser.Nodes.Patterns.VariantPatternNode(enumName, variantName, subs, tok.PositionStart, end);
                    }

                    if (_currentToken.Type == TokenType.LBRACKET && enumName == null)
                    {
                        // Struct destructuring: `User { name, age: a }`.
                        res.RegisterAdvancement();
                        Advance();

                        var fields = new List<(string, RaLanguage.Parser.Nodes.Patterns.PatternNode?)>();
                        while (true)
                        {
                            while (_currentToken.Type == TokenType.NEWLINE || _currentToken.Type == TokenType.COMMA)
                            { res.RegisterAdvancement(); Advance(); }
                            if (_currentToken.Type == TokenType.RBRACKET) break;
                            if (_currentToken.Type != TokenType.IDENTIFIER)
                            {
                                res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'{' in struct pattern"));
                                return null;
                            }
                            string fieldName = _currentToken.Value?.ToString() ?? "";
                            res.RegisterAdvancement();
                            Advance();

                            RaLanguage.Parser.Nodes.Patterns.PatternNode? fieldPattern = null;
                            if (_currentToken.Type == TokenType.COLON)
                            {
                                res.RegisterAdvancement();
                                Advance();
                                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                                fieldPattern = ParsePattern(res);
                                if (res.Error != null) return null;
                            }
                            fields.Add((fieldName, fieldPattern));
                        }

                        if (_currentToken.Type != TokenType.RBRACKET)
                        {
                            res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                            return null;
                        }
                        var endPos = _currentToken.PositionEnd;
                        res.RegisterAdvancement();
                        Advance();
                        return new RaLanguage.Parser.Nodes.Patterns.StructPatternNode(variantName, fields, tok.PositionStart, endPos);
                    }

                    if (enumName != null)
                    {
                        // Qualified zero-arity variant pattern: `Result.Ok`
                        // without payload syntax. Treat as variant with empty
                        // sub-patterns list.
                        return new RaLanguage.Parser.Nodes.Patterns.VariantPatternNode(enumName, variantName, null, tok.PositionStart, tok.PositionEnd);
                    }

                    // Bare identifier = binding (or zero-arity variant; the
                    // match engine resolves the ambiguity by looking up the
                    // name as an EnumVariantConstructor in scope).
                    return new RaLanguage.Parser.Nodes.Patterns.VariablePatternNode(name, tok.PositionStart, tok.PositionEnd);
                }

                case TokenType.INT:
                case TokenType.FLOAT:
                {
                    var node = new RaLanguage.Parser.Nodes.Primitives.NumberNode(tok);
                    res.RegisterAdvancement();
                    Advance();
                    return new RaLanguage.Parser.Nodes.Patterns.LiteralPatternNode(node, tok.PositionStart, tok.PositionEnd);
                }
                case TokenType.STRING_TEXT:
                {
                    // Re-use the existing string atom parser; literal strings
                    // become StringNode (no interpolation allowed inside a
                    // pattern; the visitor enforces purity).
                    var prev = _tokenIndex;
                    var atom = res.Register(ParseAtom());
                    if (res.Error != null) return null;
                    return new RaLanguage.Parser.Nodes.Patterns.LiteralPatternNode(atom!, tok.PositionStart, _currentToken.PositionStart);
                }
                case TokenType.KEYWORD when ((Lexer.Tokens.Keyword)tok.Value) == Lexer.Tokens.Keyword.True
                                       || ((Lexer.Tokens.Keyword)tok.Value) == Lexer.Tokens.Keyword.False:
                {
                    var bnode = new RaLanguage.Parser.Nodes.Primitives.BooleanNode(tok);
                    res.RegisterAdvancement();
                    Advance();
                    return new RaLanguage.Parser.Nodes.Patterns.LiteralPatternNode(bnode, tok.PositionStart, tok.PositionEnd);
                }
                case TokenType.KEYWORD when ((Lexer.Tokens.Keyword)tok.Value) == Lexer.Tokens.Keyword.Null:
                {
                    var nnode = new RaLanguage.Parser.Nodes.Primitives.NullNode(tok);
                    res.RegisterAdvancement();
                    Advance();
                    return new RaLanguage.Parser.Nodes.Patterns.LiteralPatternNode(nnode, tok.PositionStart, tok.PositionEnd);
                }
                case TokenType.MINUS:
                {
                    // negative numeric literal in pattern position
                    res.RegisterAdvancement();
                    Advance();
                    if (_currentToken.Type != TokenType.INT && _currentToken.Type != TokenType.FLOAT)
                    {
                        res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "a numeric literal after '-' in a pattern"));
                        return null;
                    }
                    var numTok = _currentToken;
                    var num = new RaLanguage.Parser.Nodes.Primitives.NumberNode(numTok);
                    var unary = new RaLanguage.Parser.Nodes.Operations.UnaryOperationNode(tok, num, isLeft: true);
                    res.RegisterAdvancement();
                    Advance();
                    return new RaLanguage.Parser.Nodes.Patterns.LiteralPatternNode(unary, tok.PositionStart, numTok.PositionEnd);
                }
                case TokenType.LPAREN:
                {
                    var lparenStart = tok.PositionStart;
                    res.RegisterAdvancement();
                    Advance();

                    var elements = new List<RaLanguage.Parser.Nodes.Patterns.PatternNode>();
                    while (true)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                        if (_currentToken.Type == TokenType.RPAREN) break;
                        var sub = ParsePattern(res);
                        if (res.Error != null) return null;
                        elements.Add(sub!);
                        while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }
                        break;
                    }

                    if (_currentToken.Type != TokenType.RPAREN)
                    {
                        res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '('));
                        return null;
                    }
                    var endP = _currentToken.PositionEnd;
                    res.RegisterAdvancement();
                    Advance();

                    if (elements.Count == 1) return elements[0]; // parenthesised single pattern
                    return new RaLanguage.Parser.Nodes.Patterns.TuplePatternNode(elements, lparenStart, endP);
                }
                case TokenType.LSQUARE:
                {
                    var lsqStart = tok.PositionStart;
                    res.RegisterAdvancement();
                    Advance();

                    var elements = new List<RaLanguage.Parser.Nodes.Patterns.PatternNode>();
                    RaLanguage.Parser.Nodes.Patterns.RestPatternNode? rest = null;
                    int restIndex = -1;

                    while (true)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                        if (_currentToken.Type == TokenType.RSQUARE) break;

                        if (_currentToken.Type == TokenType.SPREAD || _currentToken.Type == TokenType.DOUBLE_DOT)
                        {
                            var rtok = _currentToken;
                            res.RegisterAdvancement();
                            Advance();
                            string? bindName = null;
                            if (_currentToken.Type == TokenType.IDENTIFIER)
                            {
                                bindName = _currentToken.Value?.ToString();
                                res.RegisterAdvancement();
                                Advance();
                            }
                            if (rest != null)
                            {
                                res.Failure(ParserDiagnostics.UnexpectedToken(rtok, "a single '..rest' inside a list pattern"));
                                return null;
                            }
                            rest = new RaLanguage.Parser.Nodes.Patterns.RestPatternNode(bindName, rtok.PositionStart, _currentToken.PositionStart);
                            restIndex = elements.Count;
                        }
                        else
                        {
                            var sub = ParsePattern(res);
                            if (res.Error != null) return null;
                            elements.Add(sub!);
                        }

                        while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }
                        break;
                    }

                    if (_currentToken.Type != TokenType.RSQUARE)
                    {
                        res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ']', '['));
                        return null;
                    }
                    var endL = _currentToken.PositionEnd;
                    res.RegisterAdvancement();
                    Advance();

                    return new RaLanguage.Parser.Nodes.Patterns.ListPatternNode(elements, rest, restIndex, lsqStart, endL);
                }
                default:
                    res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "a pattern: literal, identifier, '_', '(', '[', or 'Variant(...)'"));
                    return null;
            }
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

    }
}

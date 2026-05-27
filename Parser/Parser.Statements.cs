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

                    // Accept both `{ pass; }` on one line and the
                    // multi-line form. Previously the loop only treated the
                    // block as scoped when a newline immediately followed
                    // the `{`, so `{ pass; }` was rejected with
                    // "this token has nowhere to attach".
                    while (true)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.RBRACKET)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            break;
                        }

                        if (_currentToken.Type == TokenType.EOF)
                        {
                            return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                        }

                        scopeStatements.Add(res.TryRegister(ParseStatements()));
                        if (res.Error != null) return res;
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
                    case Keyword.Throw:
                        res.RegisterAdvancement();
                        Advance();
                        var throwExpr = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                        return res.Success(new RaLanguage.Parser.Nodes.Statements.ThrowNode(
                            throwExpr, positionStart, _currentToken.PositionStart));
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

                // Catch binder may be either a bare identifier
                // ('catch (err) { ... }') or a pattern that destructures
                // the thrown value ('catch (Err(code)) { ... }'). Pattern
                // detection mirrors fn/lambda/destructure entry points.
                bool isCatchPattern =
                    _currentToken.Type == TokenType.LPAREN
                    || _currentToken.Type == TokenType.LSQUARE
                    || _currentToken.Type == TokenType.LBRACKET
                    || (_currentToken.Type == TokenType.IDENTIFIER
                        && _tokenIndex + 1 < _tokens.Count
                        && (_tokens[_tokenIndex + 1].Type == TokenType.LPAREN     // variant
                            || _tokens[_tokenIndex + 1].Type == TokenType.LBRACKET // struct
                            || _tokens[_tokenIndex + 1].Type == TokenType.DOT));   // qualified variant

                RaLanguage.Parser.Nodes.Patterns.PatternNode? catchPattern = null;
                Position catchPatternStart = _currentToken.PositionStart;

                if (isCatchPattern)
                {
                    catchPattern = ParseBasePattern(res);
                    if (res.Error != null) return res;
                    if (catchPattern == null)
                        return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                            "a pattern after 'catch ('"));

                    // Mint synthetic binder; the destructure runs at the
                    // head of the catch body.
                    string synthName = "$$catch$" + _patternForeachCounter.ToString();
                    _patternForeachCounter++;
                    catchVarTok = new Lexer.Tokens.Token(TokenType.IDENTIFIER, synthName, catchPatternStart, catchPatternStart);
                }
                else
                {
                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'catch ('", help: "catch clauses bind the thrown value, e.g. 'catch (err) { ... }' or a pattern 'catch (Err(code)) { ... }'"));
                    catchVarTok = _currentToken;
                    res.RegisterAdvancement();
                    Advance();
                }

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

                // Splice the destructuring declaration at the head of the
                // catch body when a pattern was used. Note: if the pattern
                // fails at runtime (the thrown value doesn't match), the
                // catch body raises a RuntimeError — i.e. the catch
                // declines to handle the exception. Future work: re-throw
                // instead, so partial catches fall through to outer try.
                if (catchPattern != null && catchBody != null && catchVarTok != null)
                {
                    var accessTok = new Lexer.Tokens.Token(TokenType.IDENTIFIER,
                        catchVarTok.Value!, catchPatternStart, catchPatternStart);
                    var access = new RaLanguage.Parser.Nodes.Variables.VariableAccessNode(accessTok);
                    var destructure = new RaLanguage.Parser.Nodes.Patterns.DestructuringDeclarationNode(
                        catchPattern, access,
                        RaLanguage.Parser.Nodes.Variables.VariableDeclarationType.LET, null,
                        catchPatternStart, catchBody.PositionEnd);
                    var merged = new List<AstNode>(2);
                    merged.Add(destructure);
                    if (catchBody is RaLanguage.Parser.Nodes.Special.ScopeNode existing)
                    {
                        merged.AddRange(existing.Nodes);
                    }
                    else
                    {
                        merged.Add(catchBody);
                    }
                    catchBody = new RaLanguage.Parser.Nodes.Special.ScopeNode(merged, catchPatternStart, catchBody.PositionEnd);
                }
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

            // 'if let' sugar — desugars to a match expression with two
            // arms ('case PATTERN -> then' and 'case _ -> else'). The
            // outer caller never needs to know — the AST that comes back
            // is an ordinary MatchNode that the existing visitor runs.
            if (_currentToken.Matches(Keyword.If)
                && _tokenIndex + 1 < _tokens.Count
                && _tokens[_tokenIndex + 1].Matches(Keyword.Let))
            {
                return ParseIfLetSugar();
            }

            var allCasesNode = res.Register(ParseIfExpressionCases(Keyword.If));
            if (res.Error != null) return res;

            var wrapper = (IfCasesWrapperNode)allCasesNode;
            return res.Success(new IfNode(wrapper.Cases, wrapper.ElseCase));
        }

        // 'if let PAT = EXPR { THEN }' / 'if let PAT = EXPR { THEN } else { ELSE }'.
        // Lowered to: 'match EXPR { case PAT -> { THEN }  case _ -> { ELSE? } }'.
        // The scrutinee is evaluated once; the engine handles binding
        // commit / rollback exactly like a regular match arm.
        private ParserResult ParseIfLetSugar()
        {
            var res = new ParserResult();
            var ifStart = _currentToken.PositionStart;

            res.RegisterAdvancement(); Advance();              // consume 'if'
            res.RegisterAdvancement(); Advance();              // consume 'let'
            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            var pattern = ParsePattern(res);
            if (res.Error != null) return res;
            if (pattern == null) return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                "a pattern after 'if let'"));

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
            if (_currentToken.Type != TokenType.EQ)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "'=' to introduce the scrutinee of 'if let'",
                    contextHint: "syntax: 'if let PATTERN = EXPR { ... } else { ... }'"));
            }
            res.RegisterAdvancement(); Advance();
            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            var scrutinee = res.Register(ParseExpression());
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
            if (_currentToken.Type != TokenType.LBRACKET)
            {
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{',
                    context: "the 'then' branch of an 'if let'"));
            }
            res.RegisterAdvancement(); Advance();

            var thenStart = _currentToken.PositionStart;
            var thenStmts = res.Register(ParseStatements());
            if (res.Error != null) return res;
            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
            var thenEnd = _currentToken.PositionEnd;
            res.RegisterAdvancement(); Advance();

            AstNode? elseBody = null;
            var elseEnd = thenEnd;
            int saved = _tokenIndex;
            int savedReverse = res.ToReverseCount;
            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
            if (_currentToken.Matches(Keyword.Else))
            {
                res.RegisterAdvancement(); Advance();
                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                if (_currentToken.Type != TokenType.LBRACKET)
                {
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{',
                        context: "the 'else' branch of an 'if let'"));
                }
                res.RegisterAdvancement(); Advance();
                var elseStmts = res.Register(ParseStatements());
                if (res.Error != null) return res;
                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                elseEnd = _currentToken.PositionEnd;
                res.RegisterAdvancement(); Advance();
                elseBody = elseStmts;
            }
            else
            {
                while (_tokenIndex > saved) Reverse();
                elseBody = new RaLanguage.Parser.Nodes.Special.ScopeNode(new List<AstNode>(), thenEnd, thenEnd);
            }

            var arms = new List<RaLanguage.Parser.Nodes.Patterns.MatchArmNode>
            {
                new RaLanguage.Parser.Nodes.Patterns.MatchArmNode(pattern, null, thenStmts!, thenStart, thenEnd),
                new RaLanguage.Parser.Nodes.Patterns.MatchArmNode(
                    new RaLanguage.Parser.Nodes.Patterns.WildcardPatternNode(elseEnd, elseEnd),
                    null, elseBody!, thenEnd, elseEnd)
            };
            return res.Success(new RaLanguage.Parser.Nodes.Patterns.MatchNode(scrutinee, arms, ifStart, elseEnd));
        }

        // 'while let PAT = EXPR { BODY }' — desugar to an infinite loop
        // whose body matches EXPR; the wildcard arm breaks out:
        //
        //   while true {
        //       match EXPR {
        //           case PAT -> { BODY }
        //           case _   -> { break }
        //       }
        //   }
        //
        // The match value is discarded; this is purely a statement form.
        private ParserResult ParseWhileLetSugar()
        {
            var res = new ParserResult();
            var whileStart = _currentToken.PositionStart;

            res.RegisterAdvancement(); Advance();              // consume 'while'
            res.RegisterAdvancement(); Advance();              // consume 'let'
            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            var pattern = ParsePattern(res);
            if (res.Error != null) return res;
            if (pattern == null) return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                "a pattern after 'while let'"));

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
            if (_currentToken.Type != TokenType.EQ)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "'=' to introduce the scrutinee of 'while let'",
                    contextHint: "syntax: 'while let PATTERN = EXPR { ... }'"));
            }
            res.RegisterAdvancement(); Advance();
            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            var scrutinee = res.Register(ParseExpression());
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
            if (_currentToken.Type != TokenType.LBRACKET)
            {
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{',
                    context: "the body of a 'while let' loop"));
            }
            res.RegisterAdvancement(); Advance();

            var bodyStart = _currentToken.PositionStart;
            var bodyStmts = res.Register(ParseStatements());
            if (res.Error != null) return res;
            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
            var bodyEnd = _currentToken.PositionEnd;
            res.RegisterAdvancement(); Advance();

            // We lower 'while let' to a sentinel-flag loop because 'break'
            // emitted from inside a match arm does not currently propagate
            // through the match's RuntimeResult chain back to the outer
            // 'while' (the match swallows it). A boolean continue-flag is
            // both correct and trivially optimisable by the VM's loop
            // analyser:
            //
            //   var $$cont = true
            //   while $$cont {
            //       match EXPR {
            //           case PAT -> { BODY }
            //           case _   -> { $$cont = false }
            //       }
            //   }
            string contName = "$$loop$" + _patternForeachCounter.ToString();
            _patternForeachCounter++;
            var contTok = new Lexer.Tokens.Token(TokenType.IDENTIFIER, contName, whileStart, whileStart);

            var trueLitTok = new Lexer.Tokens.Token(TokenType.KEYWORD, Lexer.Tokens.Keyword.True, whileStart, whileStart);
            var trueLit = new RaLanguage.Parser.Nodes.Primitives.BooleanNode(trueLitTok);
            var falseLitTok = new Lexer.Tokens.Token(TokenType.KEYWORD, Lexer.Tokens.Keyword.False, bodyEnd, bodyEnd);
            var falseLit = new RaLanguage.Parser.Nodes.Primitives.BooleanNode(falseLitTok);

            var contDecl = new RaLanguage.Parser.Nodes.Variables.VariableDeclarationNode(
                RaLanguage.Parser.Nodes.Variables.VariableDeclarationType.VARIABLE,
                new List<(Lexer.Tokens.Token, AstNode?, Types.TypeDescriptor?)>
                {
                    (contTok, trueLit, null)
                });

            // Wildcard arm body: '$$cont = false'.
            var eqTok = new Lexer.Tokens.Token(TokenType.EQ, null, bodyEnd, bodyEnd);
            var contAssign = new RaLanguage.Parser.Nodes.Variables.VariableAssignmentNode(
                contTok, eqTok, falseLit);
            var contAssignBlock = new RaLanguage.Parser.Nodes.Special.ScopeNode(
                new List<AstNode> { contAssign }, bodyEnd, bodyEnd);

            var armBody = new RaLanguage.Parser.Nodes.Patterns.MatchArmNode(pattern, null, bodyStmts!, bodyStart, bodyEnd);
            var armWild = new RaLanguage.Parser.Nodes.Patterns.MatchArmNode(
                new RaLanguage.Parser.Nodes.Patterns.WildcardPatternNode(bodyEnd, bodyEnd),
                null, contAssignBlock, bodyEnd, bodyEnd);

            var matchNode = new RaLanguage.Parser.Nodes.Patterns.MatchNode(
                scrutinee,
                new List<RaLanguage.Parser.Nodes.Patterns.MatchArmNode> { armBody, armWild },
                whileStart, bodyEnd);

            var contAccess = new RaLanguage.Parser.Nodes.Variables.VariableAccessNode(contTok);
            var loopBody = new RaLanguage.Parser.Nodes.Special.ScopeNode(
                new List<AstNode> { matchNode }, whileStart, bodyEnd);

            var whileNode = new RaLanguage.Parser.Nodes.Statements.WhileNode(contAccess, loopBody, true);

            var outerBlock = new RaLanguage.Parser.Nodes.Special.ScopeNode(
                new List<AstNode> { contDecl, whileNode }, whileStart, bodyEnd);

            return res.Success(outerBlock);
        }

        // 'for let PAT in EXPR { BODY }' destructuring foreach.
        //
        // Lowers to:
        //   for __it__ in EXPR { let PAT = __it__; BODY }
        // where __it__ is a fresh hygienic name (parser-local counter).
        // The pattern must be irrefutable; refutable patterns must use
        // 'if let' or 'match' inside the loop body.
        //
        // Pre-condition: caller consumed 'for' but NOT 'let'.
        private ParserResult ParseForLetSugar(Position forStart)
        {
            var res = new ParserResult();

            // consume 'let'
            res.RegisterAdvancement(); Advance();
            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            var patStart = _currentToken.PositionStart;
            var pattern = ParsePattern(res);
            if (res.Error != null) return res;
            if (pattern == null) return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                "a pattern after 'for let'"));

            // 'for let' accepts refutable patterns too (runtime checks the
            // match against every iteration element). See ParseDestructuringDeclaration
            // for the rationale.
            _ = PatternRefutability.IsIrrefutable(pattern);

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
            if (!_currentToken.Matches(Keyword.In))
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "'in' between the pattern and the iterable",
                    contextHint: "syntax: 'for let PATTERN in EXPRESSION { ... }'"));
            }
            res.RegisterAdvancement(); Advance();
            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            var collectionExpr = res.Register(ParseExpression());
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            // Body — either ': stmt' or '{ stmts }'.
            AstNode body;
            bool blockBody;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement(); Advance();
                body = res.Register(ParseStatement());
                if (res.Error != null) return res;
                blockBody = false;
            }
            else if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement(); Advance();
                var inner = res.Register(ParseStatements());
                if (res.Error != null) return res;
                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                res.RegisterAdvancement(); Advance();
                body = inner!;
                blockBody = true;
            }
            else
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "':' or '{'",
                    contextHint: "single-line bodies start with ':', multi-line bodies use '{ ... }'"));
            }

            // Mint a fresh synthetic name. Prefix '$$pat' is not a valid
            // identifier in Ra source, so user code can never collide.
            string syntheticName = "$$pat$" + _patternForeachCounter.ToString();
            _patternForeachCounter++;
            var synthTok = new Lexer.Tokens.Token(TokenType.IDENTIFIER, syntheticName, patStart, patStart);

            // Build the destructuring declaration that runs at the head of
            // each iteration. Initializer is a VariableAccess on the
            // synthetic name.
            var accessTok = new Lexer.Tokens.Token(TokenType.IDENTIFIER, syntheticName, patStart, patStart);
            var accessNode = new RaLanguage.Parser.Nodes.Variables.VariableAccessNode(accessTok);
            var destructure = new RaLanguage.Parser.Nodes.Patterns.DestructuringDeclarationNode(
                pattern, accessNode, VariableDeclarationType.LET, null,
                patStart, body.PositionEnd);

            // Splice the destructuring declaration into the body. If the
            // body is already a block, prepend; otherwise wrap.
            List<AstNode> stmts;
            if (body is RaLanguage.Parser.Nodes.Special.ScopeNode existingScope)
            {
                stmts = new List<AstNode>(existingScope.Nodes.Count + 1) { destructure };
                stmts.AddRange(existingScope.Nodes);
            }
            else
            {
                stmts = new List<AstNode> { destructure, body };
            }
            var wrappedBody = new RaLanguage.Parser.Nodes.Special.ScopeNode(stmts, forStart, body.PositionEnd);

            return res.Success(new RaLanguage.Parser.Nodes.Statements.ForEachNode(
                synthTok, collectionExpr!, wrappedBody, blockBody));
        }

        private int _patternForeachCounter = 0;

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

            // Allow `elif` / `else` to appear on the line AFTER the closing `}`
            // of the previous branch, e.g.
            //     if X { ... }
            //     elif Y { ... }
            // Skip any newlines / semicolons separating the two without
            // consuming the chain marker itself, so a stray bare `elif` later
            // in the file still produces a parse error.
            int saved = _tokenIndex;
            int saveAdvance = res.ToReverseCount;
            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }
            if (!_currentToken.Matches(Keyword.Elif) && !_currentToken.Matches(Keyword.Else))
            {
                // Not a chain continuation -> rewind so the consumed newlines
                // are visible to the outer ParseStatements loop.
                while (_tokenIndex > saved) Reverse();
            }

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

        // Top-level pattern entry. Composes or-patterns ('|' alternation)
        // and a trailing 'as IDENT' alias over a base pattern.
        //
        //   P := OrPattern ('as' IDENT)?
        //   OrPattern := BasePattern ('|' BasePattern)*
        //
        // 'is T as v' is folded into TypePatternNode directly inside
        // ParseBasePattern (a backwards-compatible fast path); the generic
        // alias trailer applies to every other pattern shape uniformly.
        private RaLanguage.Parser.Nodes.Patterns.PatternNode? ParsePattern(ParserResult res)
        {
            var inner = ParseOrPattern(res);
            if (res.Error != null || inner == null) return inner;

            // Generic alias trailer. Note: TypePatternNode already consumes
            // its own 'as IDENT' inside ParseBasePattern, so this branch is
            // unreachable when 'inner' is a TypePatternNode produced by the
            // 'is' fast path.
            if (_currentToken.Matches(Lexer.Tokens.Keyword.As))
            {
                res.RegisterAdvancement();
                Advance();
                if (_currentToken.Type != TokenType.IDENTIFIER)
                {
                    res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'as' in a pattern alias",
                        help: "alias binds the matched value to a name; e.g. 'case (1..10) as n -> ...'"));
                    return null;
                }
                string binder = _currentToken.Value?.ToString() ?? "";
                var aliasEnd = _currentToken.PositionEnd;
                res.RegisterAdvancement();
                Advance();
                return new RaLanguage.Parser.Nodes.Patterns.AliasPatternNode(inner, binder, inner.PositionStart, aliasEnd);
            }
            return inner;
        }

        // Or-pattern: comma-free, '|'-separated alternation of and-patterns.
        // Each alternative is tried in source order; bindings introduced by
        // a failing alternative are rolled back before the next one is
        // tried. Every alternative must bind the same set of names — the
        // analyzer reports mismatches.
        private RaLanguage.Parser.Nodes.Patterns.PatternNode? ParseOrPattern(ParserResult res)
        {
            var first = ParseAndPattern(res);
            if (res.Error != null || first == null) return first;

            if (_currentToken.Type != TokenType.BITWISE_OR) return first;

            var alts = new List<RaLanguage.Parser.Nodes.Patterns.PatternNode> { first };
            var startPos = first.PositionStart;
            var endPos = first.PositionEnd;

            while (_currentToken.Type == TokenType.BITWISE_OR)
            {
                res.RegisterAdvancement();
                Advance();
                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                var next = ParseAndPattern(res);
                if (res.Error != null) return null;
                if (next == null) return null;
                alts.Add(next);
                endPos = next.PositionEnd;
            }

            return new RaLanguage.Parser.Nodes.Patterns.OrPatternNode(alts, startPos, endPos);
        }

        // And-pattern: infix '&' between not-patterns. Binds tighter than
        // '|' so 'A | B & C' parses as 'A | (B & C)'. Both sides must
        // match for the conjunction to match; both contribute bindings.
        private RaLanguage.Parser.Nodes.Patterns.PatternNode? ParseAndPattern(ParserResult res)
        {
            var first = ParseNotPattern(res);
            if (res.Error != null || first == null) return first;

            if (_currentToken.Type != TokenType.BITWISE_AND) return first;

            var parts = new List<RaLanguage.Parser.Nodes.Patterns.PatternNode> { first };
            var startPos = first.PositionStart;
            var endPos = first.PositionEnd;

            while (_currentToken.Type == TokenType.BITWISE_AND)
            {
                res.RegisterAdvancement();
                Advance();
                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                var next = ParseNotPattern(res);
                if (res.Error != null) return null;
                if (next == null) return null;
                parts.Add(next);
                endPos = next.PositionEnd;
            }

            return new RaLanguage.Parser.Nodes.Patterns.AndPatternNode(parts, startPos, endPos);
        }

        // Not-pattern: optional 'not' prefix over a base pattern. Nested
        // 'not not P' is allowed (acts as identity-with-double-negation).
        // The inner pattern is forbidden from introducing bindings because
        // a failed match cannot reasonably produce them.
        private RaLanguage.Parser.Nodes.Patterns.PatternNode? ParseNotPattern(ParserResult res)
        {
            if (_currentToken.Matches(Lexer.Tokens.Keyword.Not))
            {
                var notStart = _currentToken.PositionStart;
                res.RegisterAdvancement();
                Advance();
                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                var inner = ParseNotPattern(res);
                if (res.Error != null) return null;
                if (inner == null) return null;
                if (ContainsBinding(inner))
                {
                    res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "a non-binding pattern under 'not'",
                        contextHint: "'not P' cannot expose bindings — wrap the binder outside the 'not' if you need it"));
                    return null;
                }
                return new RaLanguage.Parser.Nodes.Patterns.NotPatternNode(inner, notStart, inner.PositionEnd);
            }
            return ParseBasePattern(res);
        }

        // Pure structural check used by ParseNotPattern: does the pattern
        // subtree introduce any binding name? Conservative — answers
        // 'true' for any wildcard-or-bind subtree.
        private static bool ContainsBinding(RaLanguage.Parser.Nodes.Patterns.PatternNode p)
        {
            switch (p)
            {
                case RaLanguage.Parser.Nodes.Patterns.WildcardPatternNode _: return false;
                case RaLanguage.Parser.Nodes.Patterns.LiteralPatternNode _: return false;
                case RaLanguage.Parser.Nodes.Patterns.RangePatternNode _: return false;
                case RaLanguage.Parser.Nodes.Patterns.RelationalPatternNode _: return false;
                case RaLanguage.Parser.Nodes.Patterns.TypePatternNode tp: return !string.IsNullOrEmpty(tp.BinderName);
                case RaLanguage.Parser.Nodes.Patterns.VariablePatternNode _: return true;
                case RaLanguage.Parser.Nodes.Patterns.AliasPatternNode _: return true;
                case RaLanguage.Parser.Nodes.Patterns.NotPatternNode np: return ContainsBinding(np.Inner);
                case RaLanguage.Parser.Nodes.Patterns.AndPatternNode ap:
                    foreach (var c in ap.Conjuncts) if (ContainsBinding(c)) return true;
                    return false;
                case RaLanguage.Parser.Nodes.Patterns.OrPatternNode op:
                    foreach (var a in op.Alternatives) if (ContainsBinding(a)) return true;
                    return false;
                case RaLanguage.Parser.Nodes.Patterns.TuplePatternNode tp:
                    foreach (var e in tp.Elements) if (ContainsBinding(e)) return true;
                    return false;
                case RaLanguage.Parser.Nodes.Patterns.ListPatternNode lp:
                    foreach (var e in lp.Elements) if (ContainsBinding(e)) return true;
                    if (lp.Rest != null && !string.IsNullOrEmpty(lp.Rest.BindName)) return true;
                    return false;
                case RaLanguage.Parser.Nodes.Patterns.StructPatternNode sp:
                    foreach (var (_, fp) in sp.Fields)
                    {
                        if (fp == null) return true; // shorthand binds.
                        if (ContainsBinding(fp)) return true;
                    }
                    return false;
                case RaLanguage.Parser.Nodes.Patterns.VariantPatternNode vp:
                    if (vp.SubPatterns == null) return false;
                    foreach (var s in vp.SubPatterns) if (ContainsBinding(s)) return true;
                    return false;
                case RaLanguage.Parser.Nodes.Patterns.MapPatternNode mp:
                    foreach (var (_, vp2) in mp.Entries) if (ContainsBinding(vp2)) return true;
                    return false;
                default: return false;
            }
        }

        // Single base pattern. Distinguishes:
        //   _                          → wildcard
        //   123 / "x" / true / null    → literal
        //   1..10  /  1..=10           → range (closed-open / closed-closed)
        //   ..10   /  ..=10            → open-low range
        //   5..                        → open-high range
        //   < 5 / >= 0 / != -1         → relational against literal
        //   ident                      → variable binding (or shorthand variant
        //                                 when name resolves to a constructor;
        //                                 the engine decides at runtime)
        //   ident(p1, p2)              → variant pattern with payload subs
        //   ident.member(p1)?          → qualified variant pattern
        //   ident { field, field: p }  → struct pattern
        //   { "k": p1, "k2": p2, .. }  → map pattern (open-rest with trailing '..')
        //   (p1, p2, ...)              → tuple pattern (2+ elements) / paren grouping
        //   [p1, p2, ..rest]           → list pattern with optional rest
        //   ..ident?                   → rest pattern (only inside lists)
        private RaLanguage.Parser.Nodes.Patterns.PatternNode? ParseBasePattern(ParserResult res)
        {
            var tok = _currentToken;

            // Relational at base position: '< 5', '<= 10', '> 0', '>= -1',
            // '== 0', '!= 0'. Operand is a literal evaluated once.
            if (tok.Type == TokenType.LT || tok.Type == TokenType.LTE
                || tok.Type == TokenType.GT || tok.Type == TokenType.GTE
                || tok.Type == TokenType.EE || tok.Type == TokenType.NE)
            {
                var opTok = tok;
                res.RegisterAdvancement();
                Advance();
                var operand = ParseRangeBound(res);
                if (res.Error != null || operand == null) return null;
                return new RaLanguage.Parser.Nodes.Patterns.RelationalPatternNode(opTok.Type, operand, opTok.PositionStart, operand.PositionEnd);
            }

            // Open-low range at base position: '..hi' / '..=hi'.
            if (tok.Type == TokenType.DOUBLE_DOT || tok.Type == TokenType.DOUBLE_DOT_EQ)
            {
                bool inclusive = tok.Type == TokenType.DOUBLE_DOT_EQ;
                var rangeStart = tok.PositionStart;
                res.RegisterAdvancement();
                Advance();
                var hi = ParseRangeBound(res);
                if (res.Error != null || hi == null) return null;
                return new RaLanguage.Parser.Nodes.Patterns.RangePatternNode(null, hi, inclusive, rangeStart, hi.PositionEnd);
            }

            // Map pattern at base position: '{ key: pat, key: pat, .. }'.
            // Struct pattern uses 'IDENT { ... }' and is handled in the
            // identifier branch below.
            if (tok.Type == TokenType.LBRACKET)
            {
                return ParseMapPattern(res);
            }

            // Type-test pattern: `case is Type [as binder] -> body`. The
            // `is` prefix disambiguates this form from a bare-identifier
            // binding (which would otherwise capture `case int -> ...` as
            // "bind anything to `int`"). The binder, when present, is
            // introduced into the arm scope already narrowed to the matched
            // alternative.
            if (tok.Type == TokenType.KEYWORD && tok.Value is Lexer.Tokens.Keyword kwTokForPat && kwTokForPat == Lexer.Tokens.Keyword.Is)
            {
                res.RegisterAdvancement();
                Advance();

                var ttype = ParseType(res);
                if (ttype == null)
                {
                    res.Failure(ParserDiagnostics.ExpectedTypeName(_currentToken, after: "'is' in a match pattern"));
                    return null;
                }

                string? binder = null;
                Position endPos = _currentToken.PositionStart;
                if (_currentToken.Matches(Lexer.Tokens.Keyword.As))
                {
                    res.RegisterAdvancement();
                    Advance();
                    if (_currentToken.Type != TokenType.IDENTIFIER)
                    {
                        res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                            after: "'as' in a type pattern",
                            help: "the binder name introduces the matched value into the arm body, narrowed to the matched type"));
                        return null;
                    }
                    binder = _currentToken.Value?.ToString();
                    endPos = _currentToken.PositionEnd;
                    res.RegisterAdvancement();
                    Advance();
                }

                return new RaLanguage.Parser.Nodes.Patterns.TypePatternNode(ttype, binder, tok.PositionStart, endPos);
            }

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
                    return MaybeUpgradeToRange(res, node, tok.PositionStart, tok.PositionEnd);
                }
                case TokenType.STRING_TEXT:
                {
                    // Re-use the existing string atom parser; literal strings
                    // become StringNode (no interpolation allowed inside a
                    // pattern; the visitor enforces purity).
                    var prev = _tokenIndex;
                    var atom = res.Register(ParseAtom());
                    if (res.Error != null) return null;
                    return MaybeUpgradeToRange(res, atom!, tok.PositionStart, _currentToken.PositionStart);
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
                    return MaybeUpgradeToRange(res, unary, tok.PositionStart, numTok.PositionEnd);
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
                        "a pattern: literal, identifier, '_', '(', '[', '{', 'Variant(...)', '< 5', or '1..10'"));
                    return null;
            }
        }

        // Internal helpers shared by the base-pattern decoder.

        // After parsing a low-bound literal, if the next token starts a
        // range terminator ('..' or '..='), upgrade the pattern to a
        // RangePatternNode. Otherwise wrap the literal as a LiteralPattern.
        private RaLanguage.Parser.Nodes.Patterns.PatternNode? MaybeUpgradeToRange(
            ParserResult res, AstNode lowLiteral, Position startPos, Position litEndPos)
        {
            if (_currentToken.Type != TokenType.DOUBLE_DOT && _currentToken.Type != TokenType.DOUBLE_DOT_EQ)
                return new RaLanguage.Parser.Nodes.Patterns.LiteralPatternNode(lowLiteral, startPos, litEndPos);

            bool inclusive = _currentToken.Type == TokenType.DOUBLE_DOT_EQ;
            res.RegisterAdvancement();
            Advance();

            AstNode? hi = null;
            var endPos = litEndPos;
            if (CanStartRangeBound(_currentToken.Type))
            {
                hi = ParseRangeBound(res);
                if (res.Error != null || hi == null) return null;
                endPos = hi.PositionEnd;
            }
            else if (inclusive)
            {
                res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "a literal high bound after '..=' in a range pattern",
                    contextHint: "inclusive range patterns require a high bound; use '..' for an open-ended range"));
                return null;
            }

            return new RaLanguage.Parser.Nodes.Patterns.RangePatternNode(lowLiteral, hi, inclusive, startPos, endPos);
        }

        private static bool CanStartRangeBound(TokenType t)
            => t == TokenType.INT || t == TokenType.FLOAT || t == TokenType.STRING_TEXT || t == TokenType.MINUS;

        // Range bound: a single literal (no expressions, no compound ops).
        // Numeric (positive or negative) and string-text literals are
        // accepted; matches with strings use lexicographic ordering through
        // the standard string comparison operators.
        private AstNode? ParseRangeBound(ParserResult res)
        {
            var tok = _currentToken;
            if (tok.Type == TokenType.INT || tok.Type == TokenType.FLOAT)
            {
                var node = new RaLanguage.Parser.Nodes.Primitives.NumberNode(tok);
                res.RegisterAdvancement();
                Advance();
                return node;
            }
            if (tok.Type == TokenType.STRING_TEXT)
            {
                var atom = res.Register(ParseAtom());
                if (res.Error != null) return null;
                return atom;
            }
            if (tok.Type == TokenType.MINUS)
            {
                res.RegisterAdvancement();
                Advance();
                if (_currentToken.Type != TokenType.INT && _currentToken.Type != TokenType.FLOAT)
                {
                    res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "a numeric literal after '-' in a range bound"));
                    return null;
                }
                var numTok = _currentToken;
                var num = new RaLanguage.Parser.Nodes.Primitives.NumberNode(numTok);
                var unary = new RaLanguage.Parser.Nodes.Operations.UnaryOperationNode(tok, num, isLeft: true);
                res.RegisterAdvancement();
                Advance();
                return unary;
            }
            res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "a literal range bound (number or string)"));
            return null;
        }

        // Map pattern: '{ key: pat, key2: pat2, .. }'. The '..' (if present)
        // must be the last entry and turns the pattern open: extra keys in
        // the scrutinee are ignored. Without '..' the key set of the map
        // must equal the pattern's key set.
        private RaLanguage.Parser.Nodes.Patterns.PatternNode? ParseMapPattern(ParserResult res)
        {
            var startTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            var entries = new List<(AstNode, RaLanguage.Parser.Nodes.Patterns.PatternNode)>();
            bool hasOpenRest = false;

            while (true)
            {
                while (_currentToken.Type == TokenType.NEWLINE || _currentToken.Type == TokenType.COMMA)
                { res.RegisterAdvancement(); Advance(); }
                if (_currentToken.Type == TokenType.RBRACKET) break;

                if (_currentToken.Type == TokenType.DOUBLE_DOT || _currentToken.Type == TokenType.SPREAD)
                {
                    res.RegisterAdvancement();
                    Advance();
                    hasOpenRest = true;
                    while (_currentToken.Type == TokenType.NEWLINE || _currentToken.Type == TokenType.COMMA)
                    { res.RegisterAdvancement(); Advance(); }
                    if (_currentToken.Type != TokenType.RBRACKET)
                    {
                        res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                            "'}' after '..' in a map pattern",
                            contextHint: "'..' must be the last entry of a map pattern"));
                        return null;
                    }
                    break;
                }

                var key = res.Register(ParseExpression());
                if (res.Error != null) return null;

                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                if (_currentToken.Type != TokenType.COLON)
                {
                    res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "':' after a key in a map pattern",
                        contextHint: "map entries use 'key: pattern' syntax"));
                    return null;
                }
                res.RegisterAdvancement();
                Advance();
                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

                var valuePat = ParsePattern(res);
                if (res.Error != null || valuePat == null) return null;
                entries.Add((key, valuePat));
            }

            if (_currentToken.Type != TokenType.RBRACKET)
            {
                res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                return null;
            }
            var endPos = _currentToken.PositionEnd;
            res.RegisterAdvancement();
            Advance();
            return new RaLanguage.Parser.Nodes.Patterns.MapPatternNode(entries, hasOpenRest, startTok.PositionStart, endPos);
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
            var forStart = _currentToken.PositionStart;
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            // 'for let PAT in EXPR { BODY }' — destructuring foreach.
            //
            // Lowers to an ordinary ForEachNode iterating over EXPR and
            // binding to a synthetic identifier; the body is wrapped in a
            // DestructuringDeclarationNode that unpacks PAT from that
            // synthetic identifier at every iteration.
            if (_currentToken.Matches(Keyword.Let))
            {
                return ParseForLetSugar(forStart);
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
            // 'while let' sugar — handle before consuming the 'while' so
            // ParseWhileLetSugar can read both tokens itself.
            if (_currentToken.Matches(Keyword.While)
                && _tokenIndex + 1 < _tokens.Count
                && _tokens[_tokenIndex + 1].Matches(Keyword.Let))
            {
                return ParseWhileLetSugar();
            }

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

using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Parser
{
    // Bar-style lambdas — `|x| body` and friends. Lowers to a
    // FunctionDefinitionNode with VarNameTok = null so every downstream layer
    // (Resolver, IrCompiler, FunctionDefinitionHelper, VM OP_DefineFunction,
    // FunctionValue, BaseFunctionValue.FreezeCaptures, delegate structural
    // typing, multicast, partial, compose) reuses the existing function /
    // anonymous-fn pipeline verbatim. No new AST nodes, no new opcodes, no
    // new visitors.
    //
    // Grammar additions:
    //
    //   atom        ::= bar_lambda | …
    //   bar_lambda  ::= [capture_clause] (zero_arg_bars | param_bars)
    //                   ['->' return_type] (block_body | expression_body)
    //   zero_arg_bars ::= '||'                  -- lexed as Keyword.Or
    //   param_bars  ::= '|' params? '|'         -- '|' is BITWISE_OR at atom pos
    //   params      ::= param (',' param)*
    //   param       ::= ['ref'] IDENT [':' type]
    //   capture_clause ::= '[' capture_list ']' -- shared with the fn() form
    //   block_body  ::= '{' statements '}'
    //   expression_body ::= expression
    //
    // Disambiguation: at expression-start position, `|` (BITWISE_OR) and `||`
    // (Keyword.Or) cannot be binary operators — there is no left-hand operand
    // yet. Same atom-position reinterpretation Ra already uses for `&place`
    // (BITWISE_AND → borrow) and `*place` (MUL → deref). For a leading `[`,
    // the parser probes: if the bracket content is a valid capture-list
    // followed by `|` or `||`, it is a lambda capture clause; otherwise it is
    // a list literal and the token cursor is rolled back.
    public partial class Parser
    {
        // True when the current token opens a bar-lambda. Used by ParseAtom
        // before falling into the literal / identifier cases.
        private bool IsBarLambdaOpener()
        {
            if (_currentToken.Type == TokenType.BITWISE_OR) return true;
            if (_currentToken.Type == TokenType.KEYWORD
                && _currentToken.Value is Keyword kw
                && kw == Keyword.Or)
            {
                return true;
            }
            return false;
        }

        // Probes for an explicit capture clause immediately preceding a bar
        // lambda: `[caps] |x| body` or `[caps] || body`. Returns true on a
        // match and consumes through the closing `]` (the caller continues
        // into ParseBarLambdaCore with the captured list). Returns false and
        // rolls the token cursor back when the bracket does not lead a
        // lambda — leaving ParseAtom free to treat the `[` as a list literal.
        private bool TryParseLambdaCaptureClause(ParserResult outer, out List<CaptureSpec>? captureList)
        {
            captureList = null;
            if (_currentToken.Type != TokenType.LSQUARE) return false;

            int savedIdx = _tokenIndex;
            int savedAdvances = outer.AdvanceCount;

            var probe = new ParserResult();
            List<CaptureSpec>? probed = null;
            probe.Register(ParseOptionalCaptureList(out probed));

            if (probe.Error != null || probed == null)
            {
                _tokenIndex = savedIdx;
                UpdateCurrentToken();
                return false;
            }

            // Skip newlines between the closing `]` and the lambda bars.
            int probeIdxBeforeNL = _tokenIndex;
            while (_currentToken.Type == TokenType.NEWLINE)
            {
                probe.RegisterAdvancement();
                Advance();
            }

            if (!IsBarLambdaOpener())
            {
                _tokenIndex = savedIdx;
                UpdateCurrentToken();
                return false;
            }

            // Probe accepted — fold its advance count into the outer result so
            // the caller's bookkeeping matches reality.
            for (int i = 0; i < probe.AdvanceCount; i++) outer.RegisterAdvancement();
            captureList = probed;
            return true;
        }

        // Main bar-lambda parser. Caller has already confirmed that the
        // current token is `|` (BITWISE_OR) or `||` (Keyword.Or). `capture`
        // is non-null when a `[...]` capture clause preceded the bars.
        internal ParserResult ParseBarLambda(List<CaptureSpec>? capture)
        {
            var res = new ParserResult();
            var openTok = _currentToken;
            Position posStart = openTok.PositionStart;

            var argNameToks = new List<Token>();
            var argTypes = new List<TypeDescriptor?>();
            var isRefParams = new List<bool>();
            var paramDefaults = new List<AstNode?>();
            var paramAnnotations = new List<List<AnnotationApplicationNode>?>();
            // Lambda-parameter destructuring: `|(a, b)| body`, `|[h, ..t]| body`,
            // `|User { name }| body`, `|{ "k": v }| body`. Each pattern
            // parameter is replaced by a synthetic name; the matching
            // 'let pattern_i = $$lambda_i' declarations are spliced at the
            // head of the body so the user-visible names exist inside it.
            var paramDestructures = new List<(Token Synth, RaLanguage.Parser.Nodes.Patterns.PatternNode Pattern)>();

            // `||` lexes as a single Keyword.Or token. At atom position it
            // can never be a logical-or, so we reinterpret it as an empty
            // parameter list.
            if (_currentToken.Type == TokenType.KEYWORD
                && _currentToken.Value is Keyword kw && kw == Keyword.Or)
            {
                res.RegisterAdvancement();
                Advance();
            }
            else
            {
                // We have a `|` (BITWISE_OR). Consume it.
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                // `| |` — an empty param list spelled with two bars. Accept
                // it as a synonym for `||` since the lexer cannot merge the
                // bars across an intervening newline / space.
                if (_currentToken.Type == TokenType.BITWISE_OR)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    while (true)
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

                        // Pattern-parameter detection inside the bar list.
                        // Same shapes as fn() params: '(', '[', '{',
                        // and 'IDENT {'.
                        bool isPatternParam =
                            !isRef && (
                                _currentToken.Type == TokenType.LPAREN
                                || _currentToken.Type == TokenType.LSQUARE
                                || _currentToken.Type == TokenType.LBRACKET
                                || (_currentToken.Type == TokenType.IDENTIFIER
                                    && _tokenIndex + 1 < _tokens.Count
                                    && _tokens[_tokenIndex + 1].Type == TokenType.LBRACKET));

                        if (isPatternParam)
                        {
                            var patStart = _currentToken.PositionStart;
                            // Use ParseBasePattern (no or / alias trailer) so
                            // the closing bar '|' of the parameter list is
                            // not eaten as a pattern alternation.
                            var pattern = ParseBasePattern(res);
                            if (res.Error != null) return res;
                            if (pattern == null) return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                                "a pattern in the lambda parameter list"));

                            string synthName = "$$lambda$" + paramDestructures.Count.ToString();
                            var synthTok = new Token(TokenType.IDENTIFIER, synthName, patStart, patStart);
                            argNameToks.Add(synthTok);
                            paramAnnotations.Add(null);
                            paramDestructures.Add((synthTok, pattern));

                            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

                            TypeDescriptor? patType = null;
                            if (_currentToken.Type == TokenType.COLON)
                            {
                                res.RegisterAdvancement();
                                Advance();
                                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                                var parsedT = ParseTypeAtom(res);
                                if (parsedT == null) return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken,
                                    where: "the lambda destructuring parameter type"));
                                patType = parsedT;
                            }
                            argTypes.Add(patType);
                            isRefParams.Add(false);
                            paramDefaults.Add(null);

                            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                            if (_currentToken.Type == TokenType.COMMA)
                            {
                                res.RegisterAdvancement();
                                Advance();
                                continue;
                            }
                            break;
                        }

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                        {
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                                after: "lambda parameter list `|`",
                                help: "bar-style lambda parameters look like `|x|`, `|x, y|`, or `|x: int|`"));
                        }

                        var paramTok = _currentToken;
                        argNameToks.Add(paramTok);
                        paramAnnotations.Add(null);
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

                            // Bar-lambda parameter types use ParseTypeAtom
                            // rather than the union-aware ParseType: the
                            // closing `|` of the param list would otherwise
                            // be eaten by the union folder. Users who need
                            // a union here parenthesise it — `|x: (int |
                            // string)| body` — which the LPAREN branch in
                            // ParseTypeAtom now treats as grouping (not a
                            // 1-tuple) so the union survives intact.
                            var parsed = ParseTypeAtom(res);
                            if (parsed == null)
                            {
                                return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken,
                                    where: "the lambda parameter type"));
                            }

                            ptype = isRef ? TypeDescriptor.RefType(parsed) : parsed;
                        }

                        argTypes.Add(ptype);
                        isRefParams.Add(isRef);
                        paramDefaults.Add(null);

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }

                        break;
                    }

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type != TokenType.BITWISE_OR)
                    {
                        return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                            "',' (continuing the parameter list) or '|' (closing it)",
                            contextHint: "bar-style lambda parameter lists are comma-separated and terminated by '|'"));
                    }

                    res.RegisterAdvancement();
                    Advance();
                }
            }

            // Optional return-type annotation: `-> Type`.
            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            TypeDescriptor? returnType = null;
            if (_currentToken.Type == TokenType.ARROW_RIGHT)
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
                {
                    return res.Failure(ParserDiagnostics.ExpectedTypeName(_currentToken,
                        after: "'->' in the lambda return type"));
                }
                returnType = parsed;
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            // Body — either `{ stmts }` (block) or a single expression. The
            // expression form auto-returns the body's value, matching the
            // behaviour of the arrow-form `fn(x) => expr`.
            AstNode? bodyNode;
            bool shouldAutoReturn;

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();

                var stmts = res.Register(ParseStatements());
                if (res.Error != null) return res;

                if (_currentToken.Type != TokenType.RBRACKET)
                {
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{',
                        context: "the lambda block body"));
                }

                res.RegisterAdvancement();
                Advance();

                bodyNode = stmts;
                shouldAutoReturn = false;
            }
            else
            {
                var expr = res.Register(ParseExpression());
                if (res.Error != null)
                {
                    // ParseExpression's own diagnostic is preferred — wrapping
                    // it with a generic "expected body" message would erase the
                    // precise inner failure.
                    return res;
                }
                bodyNode = expr;
                shouldAutoReturn = true;
            }

            // Splice destructuring declarations at body head when the
            // lambda uses pattern parameters. Expression-body lambdas
            // get rewritten as a block { let pat = $$lambda; ret expr; }
            // so the destructure can run before evaluating the body.
            AstNode? finalBody = bodyNode;
            bool finalAutoReturn = shouldAutoReturn;
            if (paramDestructures.Count > 0 && finalBody != null)
            {
                finalBody = WrapBodyWithDestructures(finalBody, paramDestructures);
                finalAutoReturn = shouldAutoReturn && paramDestructures.Count == 0;
                if (shouldAutoReturn)
                {
                    // The wrapper produced a ScopeNode; auto-return cannot
                    // propagate through a multi-statement body, so emit an
                    // explicit return on the last statement (the original
                    // expression).
                    if (finalBody is RaLanguage.Parser.Nodes.Special.ScopeNode sc && sc.Nodes.Count > 0)
                    {
                        int lastIdx = sc.Nodes.Count - 1;
                        var lastExpr = sc.Nodes[lastIdx];
                        sc.Nodes[lastIdx] = new RaLanguage.Parser.Nodes.Functions.ReturnNode(
                            lastExpr, lastExpr.PositionStart, lastExpr.PositionEnd);
                    }
                    finalAutoReturn = false;
                }
            }

            var node = new FunctionDefinitionNode(
                varNameTok: null,
                argNameToks: argNameToks,
                argTypes: argTypes,
                isRefParams: isRefParams,
                paramDefaults: paramDefaults,
                hasVarArgs: false,
                varArgNameTok: null,
                varArgType: null,
                returnType: returnType,
                bodyNode: finalBody,
                shouldAutoReturn: finalAutoReturn,
                genericTypeParams: null,
                isPublic: false,
                isConstructor: false,
                isOverride: false,
                isAbstract: false,
                isStatic: false,
                whereConstraints: null,
                paramAnnotations: paramAnnotations,
                captureList: capture);

            // Re-stamp the lambda's source span so diagnostics point at the
            // opening bar, not the body's first token (the base constructor's
            // fallback would otherwise pick the body since there's no name).
            node.PositionStart = posStart;
            node.PositionEnd = bodyNode!.PositionEnd;

            return res.Success(node);
        }
    }
}

using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Types;

namespace RaLanguage.Parser
{
    public partial class Parser
    {
        // Parses a property declaration. Caller has already consumed all
        // applicable modifiers (`pub`, `static`, `abstract`, `override`,
        // `lazy`) and the current token is `prop`.
        //
        // Grammar (see RA_PROPERTIES_DESIGN.md §3):
        //
        //     prop NAME [':' TYPE] ['=' EXPR] [BODY] (NEWLINE | ';')?
        //
        //     BODY := '{' (ACCESSOR (';' | NEWLINE))* '}'
        //           | '=>' EXPRESSION
        //
        //     ACCESSOR := [pub|priv] (get|set|init|observe) ACCESSOR_BODY?
        //
        //     ACCESSOR_BODY := ';' | NEWLINE                  -- auto
        //                    | '=>' EXPRESSION                -- expression body
        //                    | '{' STATEMENT_LIST '}'         -- scope body
        //
        // Returns a fully-populated PropertyDefinitionNode. Caller wraps
        // it in the host type's `Properties` list.
        private ParserResult ParsePropertyDeclaration(
            bool isPublic,
            bool isStatic,
            bool isAbstract,
            bool isOverride,
            bool isLazy)
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Prop))
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "'prop' (property declaration)",
                    contextHint: "ParsePropertyDeclaration must be entered on a 'prop' keyword"));
            }

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "'prop'",
                    help: "a property declaration begins with a name, e.g. 'prop balance: float'"));

            var nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            TypeDescriptor? propType = null;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();

                var parsedType = ParseType(res);
                if (parsedType == null)
                    return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken,
                        where: $"the type of property '{nameTok.Value}'"));

                propType = parsedType;
            }

            // `=> expr` is the readonly-computed shorthand. It must be
            // checked BEFORE the `= expr` default branch, otherwise the
            // `=` half of `=>` would be consumed as a default-value start.
            AstNode? defaultValueNode = null;
            List<PropertyAccessorNode> accessors;

            if (_currentToken.Type == TokenType.ARROW)
            {
                // `prop x: T => expr` is equivalent to `prop x: T { get => expr }`.
                res.RegisterAdvancement();
                Advance();

                var bodyExpr = res.Register(ParseExpression());
                if (res.Error != null) return res;

                accessors = new List<PropertyAccessorNode>
                {
                    new PropertyAccessorNode(
                        nameTok, // pseudo token — kindTok is only used for position; we anchor on the name token
                        PropertyAccessorKind.Get,
                        PropertyAccessorVisibility.Default,
                        bodyExpr)
                };
            }
            else
            {
                if (_currentToken.Type == TokenType.EQ)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var defExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    defaultValueNode = defExpr;
                }

                if (_currentToken.Type == TokenType.LBRACKET)
                {
                    var accRes = ParseAccessorList(out accessors);
                    res.Register(accRes);
                    if (res.Error != null) return res;
                }
                else
                {
                    // No body. Default to auto `{ get; set; }` for stored
                    // properties; `{ get; }` for abstract; nothing for
                    // computed (impossible — no body means stored).
                    accessors = new List<PropertyAccessorNode>();
                    if (!isAbstract)
                    {
                        accessors.Add(new PropertyAccessorNode(
                            nameTok, PropertyAccessorKind.Get, PropertyAccessorVisibility.Default, null));
                        accessors.Add(new PropertyAccessorNode(
                            nameTok, PropertyAccessorKind.Set, PropertyAccessorVisibility.Default, null));
                    }
                    else
                    {
                        // Abstract without explicit body — require `{ get; }`
                        // to be authored. Refuse silently-implicit auto here
                        // since the user is asking for a contract, not a
                        // shorthand.
                        return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                            "'{' (accessor list, e.g. '{ get; }')",
                            contextHint: "abstract properties require an explicit accessor list to declare which operations are required"));
                    }
                }
            }

            // Trailing terminator — accept NEWLINE.
            if (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            // Sanity: at minimum one accessor.
            if (accessors.Count == 0)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(nameTok,
                    "at least one accessor (get/set/init/observe)",
                    contextHint: "every property must declare at least one accessor"));
            }

            // Validate accessor kinds.
            // No duplicates of the same kind. Observer cannot coexist
            // with computed get/set in the same property — handled later
            // in the runtime descriptor build; here we only forbid
            // multiple Observe accessors.
            bool seenGet = false, seenSet = false, seenInit = false, seenObserve = false;
            foreach (var acc in accessors)
            {
                switch (acc.Kind)
                {
                    case PropertyAccessorKind.Get:
                        if (seenGet)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(acc.KindTok,
                                "a single 'get' accessor (duplicate)",
                                contextHint: "a property may declare each accessor at most once"));
                        seenGet = true;
                        break;
                    case PropertyAccessorKind.Set:
                        if (seenSet)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(acc.KindTok,
                                "a single 'set' accessor (duplicate)",
                                contextHint: "a property may declare each accessor at most once"));
                        seenSet = true;
                        break;
                    case PropertyAccessorKind.Init:
                        if (seenInit)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(acc.KindTok,
                                "a single 'init' accessor (duplicate)",
                                contextHint: "a property may declare each accessor at most once"));
                        seenInit = true;
                        break;
                    case PropertyAccessorKind.Observe:
                        if (seenObserve)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(acc.KindTok,
                                "a single 'observe' accessor (duplicate)",
                                contextHint: "a property may declare each accessor at most once"));
                        seenObserve = true;
                        break;
                }
            }

            // `set` and `init` are mutually exclusive — they would
            // contend for the same backing-slot write path with
            // incompatible semantics.
            if (seenSet && seenInit)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(nameTok,
                    "either a 'set' or an 'init' accessor, not both",
                    contextHint: "set is for mutable properties, init is for one-shot constructor-only assignment"));
            }

            // Lazy properties without a default initializer have nothing
            // to evaluate on first access.
            if (isLazy && defaultValueNode == null && !isAbstract)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(nameTok,
                    "'= <initializer>' on a lazy property",
                    contextHint: "lazy properties must declare their initializer expression with '= expr'"));
            }

            // Abstract properties cannot have a default or accessor body.
            if (isAbstract)
            {
                if (defaultValueNode != null)
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(nameTok,
                        "no '= default' on an abstract property",
                        contextHint: "abstract properties have no storage and cannot carry a default value"));
                }

                foreach (var acc in accessors)
                {
                    if (acc.BodyNode != null)
                    {
                        return res.Failure(ParserDiagnostics.UnexpectedToken(acc.KindTok,
                            "an auto accessor (no body) on an abstract property",
                            contextHint: "abstract accessors declare a contract only — drop the body"));
                    }
                }
            }

            // Static + init is meaningless — no constructor scope.
            if (isStatic && seenInit)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(nameTok,
                    "no 'init' accessor on a static property",
                    contextHint: "init-only is bound to an instance constructor; static properties have none"));
            }

            return res.Success(new PropertyDefinitionNode(
                nameTok,
                propType,
                defaultValueNode,
                accessors,
                isPublic,
                isStatic,
                isAbstract,
                isOverride,
                isLazy));
        }

        // Parses the `{ accessor* }` portion of a property body. The
        // current token must be `{`. On return, the closing `}` is
        // already consumed.
        private ParserResult ParseAccessorList(out List<PropertyAccessorNode> accessors)
        {
            var res = new ParserResult();
            accessors = new List<PropertyAccessorNode>();

            if (_currentToken.Type != TokenType.LBRACKET)
            {
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));
            }

            res.RegisterAdvancement();
            Advance();

            SkipNewlinesAndSemicolons(res);

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                if (_currentToken.Type == TokenType.EOF)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

                var accRes = ParseSingleAccessor();
                if (accRes.Error != null) return res.Failure(accRes.Error);
                accessors.Add((PropertyAccessorNode)accRes.Node!);

                SkipNewlinesAndSemicolons(res);
            }

            res.RegisterAdvancement();
            Advance();
            return res.Success(null!);
        }

        private ParserResult ParseSingleAccessor()
        {
            var res = new ParserResult();

            // Per-accessor visibility prefix.
            PropertyAccessorVisibility vis = PropertyAccessorVisibility.Default;
            if (_currentToken.Matches(Keyword.Pub))
            {
                vis = PropertyAccessorVisibility.Public;
                res.RegisterAdvancement();
                Advance();
            }
            else if (_currentToken.Type == TokenType.IDENTIFIER &&
                     string.Equals(_currentToken.Value?.ToString(), "priv", StringComparison.Ordinal))
            {
                vis = PropertyAccessorVisibility.Private;
                res.RegisterAdvancement();
                Advance();
            }

            // Accessor kind keyword. These are contextual: in the lexer
            // they remain plain identifiers (so user code that uses
            // `get`/`set` etc. as method names still works), and we match
            // by string value here.
            if (_currentToken.Type != TokenType.IDENTIFIER)
            {
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "the start of a property accessor",
                    help: "expected one of 'get', 'set', 'init', 'observe'"));
            }

            var kindTok = _currentToken;
            var kindStr = kindTok.Value?.ToString() ?? "";
            PropertyAccessorKind kind;
            switch (kindStr)
            {
                case "get": kind = PropertyAccessorKind.Get; break;
                case "set": kind = PropertyAccessorKind.Set; break;
                case "init": kind = PropertyAccessorKind.Init; break;
                case "observe": kind = PropertyAccessorKind.Observe; break;
                default:
                    return res.Failure(ParserDiagnostics.UnexpectedToken(kindTok,
                        "'get', 'set', 'init' or 'observe'",
                        contextHint: "property accessors are introduced by one of these contextual keywords"));
            }

            res.RegisterAdvancement();
            Advance();

            AstNode? body = null;

            if (_currentToken.Type == TokenType.ARROW)
            {
                // `get => expr`, `set => expr`, etc.
                res.RegisterAdvancement();
                Advance();

                var bodyExpr = res.Register(ParseExpression());
                if (res.Error != null) return res;
                body = bodyExpr;
            }
            else if (_currentToken.Type == TokenType.LBRACKET)
            {
                // `{ statements }` — parse a scope body. We reuse the
                // same block grammar used for function bodies / loops:
                // collect statements until the matching '}'.
                var bodyStart = _currentToken.PositionStart;
                res.RegisterAdvancement();
                Advance();

                var stmts = new List<AstNode?>();
                while (true)
                {
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.RBRACKET)
                    {
                        var bodyEnd = _currentToken.PositionEnd;
                        res.RegisterAdvancement();
                        Advance();
                        body = new ScopeNode(stmts, bodyStart, bodyEnd);
                        break;
                    }

                    if (_currentToken.Type == TokenType.EOF)
                    {
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));
                    }

                    stmts.Add(res.TryRegister(ParseStatements()));
                    if (res.Error != null) return res;
                }
            }
            // else: auto accessor — body stays null.

            // Observe must always have a body — it would be a no-op
            // otherwise and the user almost certainly intended something.
            if (kind == PropertyAccessorKind.Observe && body == null)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(kindTok,
                    "an 'observe' body block",
                    contextHint: "'observe' is a notification hook and must declare its body — use 'observe { ... }'"));
            }

            return res.Success(new PropertyAccessorNode(kindTok, kind, vis, body));
        }

        // Ra has no SEMICOLON token; accessor separation is via NEWLINE.
        // Helper alias kept to make property-specific call sites
        // self-describing.
        private void SkipNewlinesAndSemicolons(ParserResult res) => SkipNewlines(res);
    }
}

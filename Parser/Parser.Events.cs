using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Events;
using RaLanguage.Types;

namespace RaLanguage.Parser
{
    public partial class Parser
    {
        // Parses an event declaration. Caller has already consumed all
        // applicable modifiers (`pub|priv`, `static`, `abstract`,
        // `override`, `cancellable`, `tolerant`, `async`) and the
        // current token is `event`.
        //
        // Grammar (RA_EVENTS_DESIGN.md §3):
        //
        //     event NAME '(' [ payload_param ( ',' payload_param )* ] ')'
        //           [ '{' (accessor (';' | NEWLINE)+)* '}' ]
        //
        // Returns a fully-populated EventDefinitionNode.
        private ParserResult ParseEventDeclaration(
            bool isPublic,
            bool isStatic,
            bool isAbstract,
            bool isOverride,
            bool isCancellable,
            bool isTolerant = false,
            bool isAsync = false)
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Event))
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "'event' (event declaration)",
                    contextHint: "ParseEventDeclaration must be entered on an 'event' keyword"));
            }

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "'event'",
                    help: "an event declaration begins with a name, e.g. 'event Click(x: int, y: int)'"));

            var nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type != TokenType.LPAREN)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '(',
                    context: "the event payload parameter list"));

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            var payloadParams = new List<EventPayloadParam>();

            if (_currentToken.Type != TokenType.RPAREN)
            {
                while (true)
                {
                    SkipNewlines(res);

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                            after: "the start of an event payload parameter",
                            help: "payload parameters are `name: type` pairs, e.g. 'event Click(x: int, y: int)'"));

                    var paramNameTok = _currentToken;
                    res.RegisterAdvancement();
                    Advance();

                    TypeDescriptor? paramType = null;
                    if (_currentToken.Type == TokenType.COLON)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        var parsed = ParseType(res);
                        if (parsed == null)
                            return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken,
                                where: $"the type of event payload parameter '{paramNameTok.Value}'"));
                        paramType = parsed;
                    }

                    payloadParams.Add(new EventPayloadParam(paramNameTok, paramType));

                    SkipNewlines(res);

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
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '('));

            res.RegisterAdvancement();
            Advance();

            // Optional accessor block `{ pub raise; priv subscribe; }`.
            // The block, when present, must open on the same line as the
            // event payload list (matches the property convention so the
            // outer member-loop can treat the trailing NEWLINE as a
            // record-terminator).
            var accessors = new List<EventAccessorNode>();

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                if (isAbstract)
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "no accessor block on an abstract event",
                        contextHint: "abstract events declare a contract only — do not author an accessor block"));
                }

                res.RegisterAdvancement();
                Advance();

                SkipNewlines(res);

                while (_currentToken.Type != TokenType.RBRACKET)
                {
                    if (_currentToken.Type == TokenType.EOF)
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

                    var accRes = ParseSingleEventAccessor();
                    if (accRes.Error != null) return res.Failure(accRes.Error);
                    accessors.Add((EventAccessorNode)accRes.Node!);

                    SkipNewlines(res);
                }

                res.RegisterAdvancement();
                Advance();
            }

            // Validate accessor list: no duplicates of the same kind.
            bool seenSubscribe = false, seenRaise = false;
            foreach (var acc in accessors)
            {
                switch (acc.Kind)
                {
                    case EventAccessorKind.Subscribe:
                        if (seenSubscribe)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(acc.KindTok,
                                "a single 'subscribe' accessor (duplicate)",
                                contextHint: "an event may declare each accessor at most once"));
                        seenSubscribe = true;
                        break;
                    case EventAccessorKind.Raise:
                        if (seenRaise)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(acc.KindTok,
                                "a single 'raise' accessor (duplicate)",
                                contextHint: "an event may declare each accessor at most once"));
                        seenRaise = true;
                        break;
                }
            }

            // Trailing terminator.
            if (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            // Abstract + static is meaningless — same reasoning as
            // properties: static has no instance-override site.
            if (isStatic && isAbstract)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(nameTok,
                    "a non-abstract static event",
                    contextHint: "static events cannot be abstract — they have no instance-override site"));
            }

            return res.Success(new EventDefinitionNode(
                nameTok,
                payloadParams,
                accessors,
                isPublic,
                isStatic,
                isAbstract,
                isOverride,
                isCancellable,
                isTolerant,
                isAsync));
        }

        // Parses a single event accessor inside the `{ ... }` block.
        // Form: [pub|priv] (subscribe|raise) (NEWLINE | ';')?
        private ParserResult ParseSingleEventAccessor()
        {
            var res = new ParserResult();

            EventAccessorVisibility vis = EventAccessorVisibility.Default;
            if (_currentToken.Matches(Keyword.Pub))
            {
                vis = EventAccessorVisibility.Public;
                res.RegisterAdvancement();
                Advance();
            }
            else if (_currentToken.Type == TokenType.IDENTIFIER &&
                     string.Equals(_currentToken.Value?.ToString(), "priv", System.StringComparison.Ordinal))
            {
                vis = EventAccessorVisibility.Private;
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
            {
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "the start of an event accessor",
                    help: "expected 'subscribe' or 'raise'"));
            }

            var kindTok = _currentToken;
            var kindStr = kindTok.Value?.ToString() ?? "";
            EventAccessorKind kind;
            switch (kindStr)
            {
                case "subscribe": kind = EventAccessorKind.Subscribe; break;
                case "raise":     kind = EventAccessorKind.Raise; break;
                default:
                    return res.Failure(ParserDiagnostics.UnexpectedToken(kindTok,
                        "'subscribe' or 'raise'",
                        contextHint: "event accessors are introduced by one of these contextual keywords"));
            }

            res.RegisterAdvancement();
            Advance();

            if (vis == EventAccessorVisibility.Default)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(kindTok,
                    "an explicit 'pub' or 'priv' before the accessor",
                    contextHint: "event accessor entries exist only to override visibility — 'pub raise;' or 'priv subscribe;'"));
            }

            return res.Success(new EventAccessorNode(kindTok, kind, vis));
        }

        // Helper used by container parsers: consumes the optional
        // `cancellable` contextual keyword (an identifier in the lexer).
        // Returns true if consumed; leaves the stream untouched
        // otherwise.
        private bool TryConsumeCancellable(ParserResult res)
        {
            if (_currentToken.Type == TokenType.IDENTIFIER &&
                string.Equals(_currentToken.Value?.ToString(), "cancellable", System.StringComparison.Ordinal))
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                return true;
            }
            return false;
        }

        // Generic helper: consumes optional event modifiers in any
        // order. The set is `cancellable` / `tolerant` / `async`.
        // `async` is a real keyword; `cancellable` and `tolerant` are
        // contextual identifiers. Returns a triple of consumed flags.
        // Stops as soon as a non-modifier token is observed so the
        // caller can dispatch on `event` immediately after.
        private (bool cancellable, bool tolerant, bool isAsync) TryConsumeEventModifiers(ParserResult res)
        {
            bool cancellable = false, tolerant = false, isAsync = false;
            while (true)
            {
                if (_currentToken.Type == TokenType.IDENTIFIER)
                {
                    var v = _currentToken.Value?.ToString();
                    if (string.Equals(v, "cancellable", System.StringComparison.Ordinal))
                    {
                        if (cancellable) break;
                        cancellable = true;
                        res.RegisterAdvancement();
                        Advance();
                        while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                        continue;
                    }
                    if (string.Equals(v, "tolerant", System.StringComparison.Ordinal))
                    {
                        if (tolerant) break;
                        tolerant = true;
                        res.RegisterAdvancement();
                        Advance();
                        while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }
                        continue;
                    }
                }
                if (_currentToken.Matches(Keyword.Async))
                {
                    // Only consume `async` here when followed by an
                    // event-related token. Otherwise leave it for the
                    // existing async-fn / async-stream branches that
                    // come later in the container loop.
                    int save = _tokenIndex;
                    int eaten = 0;
                    res.RegisterAdvancement();
                    Advance();
                    eaten++;
                    while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); eaten++; }
                    bool looksLikeEvent = _currentToken.Matches(Keyword.Event) ||
                        (_currentToken.Type == TokenType.IDENTIFIER &&
                         (string.Equals(_currentToken.Value?.ToString(), "cancellable", System.StringComparison.Ordinal) ||
                          string.Equals(_currentToken.Value?.ToString(), "tolerant", System.StringComparison.Ordinal)));
                    if (looksLikeEvent)
                    {
                        if (isAsync)
                        {
                            // duplicate async — surface a parser error
                            // through the same break-out path; the outer
                            // event branch will surface UnexpectedToken
                            // if Event doesn't follow.
                            break;
                        }
                        isAsync = true;
                        continue;
                    }
                    // Rewind — async wasn't ours.
                    Reverse(eaten);
                    break;
                }
                break;
            }
            return (cancellable, tolerant, isAsync);
        }
    }
}

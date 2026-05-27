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
        // Top-level type entry. Parses one primary type (`ParseTypeAtom`) and
        // then folds any number of `T | U | V` alternatives on the right into a
        // structural union descriptor. `|` (BITWISE_OR) is the lowest-precedence
        // type operator — prefix forms like `&T` or `*T` bind tighter so
        // `&A | B` reads as `(&A) | B`. To request `&(A | B)` users wrap the
        // union in parens. Tuples, generics, fn params, and fn return slots
        // recurse through ParseType, so unions work naturally inside them.
        private TypeDescriptor? ParseType(ParserResult res)
        {
            var first = ParseTypeAtom(res);
            if (first == null) return null;
            if (_currentToken.Type != TokenType.BITWISE_OR) return first;

            var members = new List<TypeDescriptor> { first };
            while (_currentToken.Type == TokenType.BITWISE_OR)
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var next = ParseTypeAtom(res);
                if (next == null) return null;
                members.Add(next);
            }

            return TypeDescriptor.Union(members);
        }

        private TypeDescriptor? ParseTypeAtom(ParserResult res)
        {
            // Reference type syntax: `&T`, `&mut T`, `&'a T`, `&'a mut T`.
            // Mirrors the borrow-expression grammar in ParseFactor. The result is a
            // TypeDescriptor created via RefType so the existing IsAssignable path
            // continues to enforce ref-vs-non-ref checks; the new IsMutableRef +
            // Lifetime fields are consumed by the borrow checker.
            //
            // Ref reads an atom (not a full union) so `&A | B` is `(&A) | B`.
            // Users who want `&(A | B)` write the parens explicitly.
            if (_currentToken.Type == TokenType.BITWISE_AND)
            {
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

                var inner = ParseTypeAtom(res);
                if (inner == null) return null;
                return TypeDescriptor.RefType(inner, isMut, lifetime);
            }

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

                bool sawTrailingComma = false;
                while (true)
                {
                    var elem = ParseType(res);
                    if (elem == null) return null;
                    elements.Add(elem);

                    if (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();
                        if (_currentToken.Type == TokenType.RPAREN)
                        {
                            sawTrailingComma = true;
                            break;
                        }
                        continue;
                    }

                    if (_currentToken.Type != TokenType.RPAREN) return null;
                    break;
                }

                // Consume the `)`.
                res.RegisterAdvancement();
                Advance();

                // Grouping rule: `(T)` is just `T` (parenthesised — handy
                // for forcing precedence around union members inside bar-
                // lambda parameter lists, where the surrounding `|` would
                // otherwise terminate the param list before the union).
                // `(T,)` is the 1-tuple. `(A, B, ...)` is the N-tuple, and
                // `()` (handled above) is the unit / empty tuple.
                if (elements.Count == 1 && !sawTrailingComma)
                    return elements[0];

                return TypeDescriptor.Tuple(elements);
            }

            // Structural function-type literal: `fn(T1, T2, ...) -> R`. Recognised
            // in any type position. The return-type arrow `-> R` is optional —
            // omitting it (or writing `-> void`) yields a delegate whose return
            // type slot is unconstrained ("any"). The `void` keyword is matched
            // textually as an identifier so we don't have to add a token type.
            if (_currentToken.Matches(Keyword.Fn))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.LPAREN)
                {
                    // Treat as the bare nominal "fn" — fall through to the
                    // identifier path with `fn` as the type name. Unlikely
                    // in practice, but keeps parser robustness.
                    return new TypeDescriptor("function");
                }

                res.RegisterAdvancement();
                Advance();

                var paramTypes = new List<TypeDescriptor>();
                if (_currentToken.Type != TokenType.RPAREN)
                {
                    while (true)
                    {
                        var p = ParseType(res);
                        if (p == null) return null;
                        paramTypes.Add(p);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }
                        break;
                    }
                }

                if (_currentToken.Type != TokenType.RPAREN) return null;
                res.RegisterAdvancement();
                Advance();

                TypeDescriptor? retType = null;
                if (_currentToken.Type == TokenType.ARROW_RIGHT)
                {
                    res.RegisterAdvancement();
                    Advance();
                    if (_currentToken.Type == TokenType.IDENTIFIER
                        && string.Equals(_currentToken.Value?.ToString(), "void", System.StringComparison.Ordinal))
                    {
                        // `-> void` is sugar for "no specific return type". Same
                        // shape as omitting the arrow entirely.
                        res.RegisterAdvancement();
                        Advance();
                        retType = null;
                    }
                    else
                    {
                        retType = ParseType(res);
                        if (retType == null) return null;
                    }
                }

                return TypeDescriptor.FunctionType(paramTypes, retType);
            }

            if (!(_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.KEYWORD))
            {
                return null;
            }

            // Most type names are plain identifiers and Token.Value.ToString()
            // already yields the source-form lowercase string. A few keywords
            // are also valid type names (`null` is the canonical one — it
            // shows up in `T | null` for the nullable shortcut). For those we
            // map the PascalCase enum form to the Ra-source lowercase form so
            // the TypeDescriptor name lines up with what IsAssignable /
            // IsRuntimeTypeMatch compare against.
            string baseName;
            if (_currentToken.Type == TokenType.KEYWORD && _currentToken.Value is Keyword kwTy)
            {
                baseName = kwTy switch
                {
                    Keyword.Null => "null",
                    Keyword.True => "bool",
                    Keyword.False => "bool",
                    _ => kwTy.ToString()
                };
            }
            else
            {
                baseName = _currentToken.Value?.ToString() ?? _currentToken.ToString();
            }

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

            // First param: either a type parameter (identifier) or a lifetime ('a).
            // Lifetime params are recognised by the parser so signatures like
            //   fn longest<'a, T>(x: &'a T, y: &'a T) -> &'a T
            // are valid syntax. They are not stored in genericTypeParams (which
            // governs type-name substitution) — the borrow checker reads BorrowNode
            // / RefType lifetimes directly to validate their scopes.
            if (_currentToken.Type == TokenType.LIFETIME)
            {
                res.RegisterAdvancement();
                Advance();
            }
            else if (_currentToken.Type == TokenType.IDENTIFIER)
            {
                genericTypeParams.Add(_currentToken.Value?.ToString() ?? "");
                res.RegisterAdvancement();
                Advance();
            }
            else
            {
                return res.Failure(ParserDiagnostics.ExpectedGenericParamName(_currentToken));
            }

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

                if (_currentToken.Type == TokenType.LIFETIME)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(ParserDiagnostics.ExpectedGenericParamName(_currentToken));

                    var name = _currentToken.Value?.ToString() ?? "";
                    if (genericTypeParams.Contains(name))
                        return res.Failure(ParserDiagnostics.DuplicateGenericParam(name, _currentToken.PositionStart, _currentToken.PositionEnd));

                    genericTypeParams.Add(name);
                    res.RegisterAdvancement();
                    Advance();
                }

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

        // Parses an optional explicit closure-capture list: `[x, &y, &mut z, move w]`.
        // Returns Success(null) with `captureList == null` if no `[` is present —
        // the function then uses the legacy implicit lexical closure. Returns
        // Success(null) with a non-null `captureList` after the closing `]` is
        // consumed when one was present.
        //
        // Spec syntax per entry:
        //   identifier            → CaptureMode.ByValue (snapshot)
        //   '&' identifier        → CaptureMode.ByRef (shared borrow)
        //   '&' 'mut' identifier  → CaptureMode.ByRef with IsMutableBorrow=true
        //   'move' identifier     → CaptureMode.ByMove (transfer ownership)
        private ParserResult ParseOptionalCaptureList(out List<CaptureSpec>? captureList)
        {
            var res = new ParserResult();
            captureList = null;

            if (_currentToken.Type != TokenType.LSQUARE)
                return res.Success(null);

            res.RegisterAdvancement();
            Advance();

            var list = new List<CaptureSpec>();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            // Empty capture list `[]` is a legal, explicit "capture nothing".
            if (_currentToken.Type == TokenType.RSQUARE)
            {
                res.RegisterAdvancement();
                Advance();
                captureList = list;
                return res.Success(null);
            }

            var firstErr = ParseSingleCaptureSpec(res, list);
            if (firstErr != null) return res.Failure(firstErr);

            while (true)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.COMMA) break;

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var nextErr = ParseSingleCaptureSpec(res, list);
                if (nextErr != null) return res.Failure(nextErr);
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.RSQUARE)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ']', '[', context: "the closure capture list"));

            res.RegisterAdvancement();
            Advance();

            captureList = list;
            return res.Success(null);
        }

        // Reads one capture-spec entry and appends it to `list`. Returns a
        // diagnostic Error on shape failure (no identifier where one was
        // required). Caller-style: takes the active ParserResult so token
        // advancements are accounted for in the surrounding helper.
        private RaLanguage.Errors.Error? ParseSingleCaptureSpec(ParserResult res, List<CaptureSpec> list)
        {
            var mode = CaptureMode.ByValue;
            bool isMutBorrow = false;

            if (_currentToken.Type == TokenType.BITWISE_AND)
            {
                mode = CaptureMode.ByRef;
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Matches(Keyword.Mut))
                {
                    isMutBorrow = true;
                    res.RegisterAdvancement();
                    Advance();
                }
            }
            else if (_currentToken.Matches(Keyword.Move))
            {
                mode = CaptureMode.ByMove;
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "capture-list element",
                    help: "each capture must name an outer binding, optionally prefixed with '&', '&mut', or 'move'");

            var nameTok = _currentToken;
            list.Add(new CaptureSpec(nameTok, mode, isMutBorrow));
            res.RegisterAdvancement();
            Advance();
            return null;
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

    }
}

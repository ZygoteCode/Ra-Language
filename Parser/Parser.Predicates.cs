using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser
{
    // Predicates — `pred name(params) => expr`, `pred name(params) { ... }`,
    // and the anonymous literal `pred(params) => expr`. A predicate is a
    // first-class boolean function: it lowers to an ordinary
    // FunctionDefinitionNode (IsPredicate = true, VarNameTok = null for the
    // anonymous form, exactly like a lambda) and reuses the whole function
    // pipeline — params, generics, captures, destructuring, arrow / block
    // bodies. The single difference is the `IsPredicate` marker plus a `bool`
    // return contract enforced in ParseFunctionDefinition.
    //
    // No new AST node kinds, no new opcodes, no new visitors. Composition
    // (`&` / `|` / `!`), the `Pred<T>` type and the narrowing analyzer all key
    // off the PredicateValue that FunctionDefinitionHelper.Apply produces.
    //
    // Grammar:
    //   atom      ::= 'pred' predicate_tail | …
    //   pred_decl ::= 'pred' predicate_tail            -- statement position
    //   predicate_tail ::= [IDENT] [generics] [capture] '(' params? ')'
    //                      [':' 'bool'] (arrow_body | block_body)
    //   arrow_body ::= '=>' expression
    //   block_body ::= '{' statements '}'
    public partial class Parser
    {
        // Consumes the `pred` keyword and delegates to the shared function
        // parser with isPredicate = true. `isPublic` is threaded for the
        // `pub pred …` declaration path.
        internal ParserResult ParsePredicateDefinition(bool isPublic = false)
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Pred))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "pred",
                    context: "to begin a predicate declaration"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var def = res.Register(ParseFunctionDefinition(isPublic: isPublic, isPredicate: true));
            if (res.Error != null) return res;
            return res.Success(def);
        }

        // A predicate is ALSO a type guard when its whole body is exactly
        // `param is T` / `param is not T` testing its sole parameter. We record
        // the refined parameter + tested type on the node so (a) the runtime
        // PredicateValue carries the guard metadata and (b) the
        // NarrowingAnalyzer can treat a call `p(v)` exactly like an inline
        // `v is T`. Anything more complex than a single `is`-test on the lone
        // parameter is deliberately NOT a guard — we stay silent rather than
        // guess, keeping guard-awareness free of false positives.
        internal static void DetectNarrowingGuard(Nodes.Functions.FunctionDefinitionNode node, Nodes.AstNode? body)
        {
            if (node.ArgNameToks.Count != 1) return;
            var paramName = node.ArgNameToks[0].Value?.ToString();
            if (string.IsNullOrEmpty(paramName)) return;

            var expr = UnwrapGuardExpression(body);
            if (expr is not Nodes.Operations.IsTypeNode isNode) return;
            if (isNode.Expression is not Nodes.Variables.VariableAccessNode va) return;
            if (!string.Equals(va.VarNameTok.Value?.ToString(), paramName, System.StringComparison.Ordinal)) return;

            node.NarrowsParamName = paramName;
            node.NarrowsToType = isNode.TestedType;
            node.NarrowsNegated = isNode.Negated;
        }

        // Reduce a predicate body to its single guard expression: an arrow
        // body is the expression itself; a block body counts only when it is
        // exactly one `ret <expr>` statement (a bare expression statement is
        // not returned, so it cannot be the guard).
        private static Nodes.AstNode? UnwrapGuardExpression(Nodes.AstNode? body)
        {
            switch (body)
            {
                case null: return null;
                case Nodes.Operations.IsTypeNode: return body;
                case Nodes.Functions.ReturnNode rn: return rn.NodeToReturn;
                case Nodes.Special.ScopeNode sc:
                    return sc.Nodes.Count == 1 ? UnwrapGuardExpression(sc.Nodes[0]) : null;
                default: return null;
            }
        }
    }
}

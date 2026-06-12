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
    }
}

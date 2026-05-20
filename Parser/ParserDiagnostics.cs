using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser
{
    /// <summary>
    /// Centralized factory of parser-side <see cref="InvalidSyntaxError"/> instances.
    /// Every method returns an error stamped with the most specific diagnostic code
    /// available, plus a primary label and (when useful) an actionable help hint.
    ///
    /// Keep call sites in <see cref="Parser"/> short and intent-revealing by using
    /// these helpers — they describe what was expected, where, and why.
    /// </summary>
    internal static class ParserDiagnostics
    {
        // ---------------------------------------------------------------
        // Generic primitives
        // ---------------------------------------------------------------

        internal static InvalidSyntaxError TrailingToken(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"unexpected {Parser.DescribeToken(current)} after a complete top-level statement",
                DiagnosticCode.ParserTrailingInput,
                help: "did you forget a newline, ';' or an operator before this token?",
                primaryLabel: "this token has nowhere to attach");

        internal static InvalidSyntaxError UnexpectedToken(Token current, string expectedDescription, string? contextHint = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected {expectedDescription} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserUnexpectedToken,
                help: contextHint,
                primaryLabel: $"expected {expectedDescription} here");

        // ---------------------------------------------------------------
        // Identifiers
        // ---------------------------------------------------------------

        internal static InvalidSyntaxError ExpectedIdentifier(Token current, string? after = null, string? help = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                after == null
                    ? $"expected an identifier but found {Parser.DescribeToken(current)}"
                    : $"expected an identifier after {after} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedIdentifier,
                help: help,
                primaryLabel: "identifier expected here");

        internal static InvalidSyntaxError ExpectedMemberName(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected a member name after '.' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedIdentifier,
                help: "the right-hand side of '.' must be a field, property or method name",
                primaryLabel: "member name expected here");

        internal static InvalidSyntaxError ExpectedParameterName(Token current, string? hostingConstruct = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected a parameter name but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedIdentifier,
                help: hostingConstruct == null ? null : $"each parameter of a {hostingConstruct} must start with an identifier",
                primaryLabel: "parameter name expected here");

        internal static InvalidSyntaxError ExpectedTypeName(Token current, string? after = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                after == null
                    ? $"expected a type name but found {Parser.DescribeToken(current)}"
                    : $"expected a type name after {after} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedType,
                primaryLabel: "type name expected here");

        // ---------------------------------------------------------------
        // Delimiters
        // ---------------------------------------------------------------

        internal static InvalidSyntaxError ExpectedClosing(Token current, char closer, char opener, string? context = null)
        {
            string help = $"check for an unbalanced opening '{opener}'";
            string ctxStr = context == null ? "" : $" of {context}";
            return new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected '{closer}'{ctxStr} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                help: help,
                primaryLabel: $"expected '{closer}' here");
        }

        internal static InvalidSyntaxError ExpectedOpening(Token current, char opener, string? context = null)
        {
            string ctxStr = context == null ? "" : $" to start {context}";
            return new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected '{opener}'{ctxStr} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                primaryLabel: $"expected '{opener}' here");
        }

        internal static InvalidSyntaxError ExpectedComma(Token current, string? listKind = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                listKind == null
                    ? $"expected ',' but found {Parser.DescribeToken(current)}"
                    : $"expected ',' between {listKind} items but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                help: "separate list items with a comma",
                primaryLabel: "expected ',' here");

        internal static InvalidSyntaxError ExpectedColon(Token current, string? context = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                context == null
                    ? $"expected ':' but found {Parser.DescribeToken(current)}"
                    : $"expected ':' {context} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                primaryLabel: "expected ':' here");

        internal static InvalidSyntaxError ExpectedSemicolon(Token current, string? what = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                what == null
                    ? $"expected ';' or end of line but found {Parser.DescribeToken(current)}"
                    : $"expected ';' or end of line after {what} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                primaryLabel: "expected ';' or newline here");

        // ---------------------------------------------------------------
        // Keywords / operators
        // ---------------------------------------------------------------

        internal static InvalidSyntaxError ExpectedKeyword(Token current, string keyword, string? context = null, string? help = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                context == null
                    ? $"expected keyword '{keyword}' but found {Parser.DescribeToken(current)}"
                    : $"expected keyword '{keyword}' {context} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedKeyword,
                help: help,
                primaryLabel: $"expected '{keyword}' here");

        internal static InvalidSyntaxError ExpectedOneOfKeywords(Token current, string[] keywords, string? context = null)
        {
            string list = keywords.Length switch
            {
                0 => "",
                1 => $"'{keywords[0]}'",
                2 => $"'{keywords[0]}' or '{keywords[1]}'",
                _ => string.Join(", ", System.Linq.Enumerable.Select(keywords, k => $"'{k}'"))
            };
            return new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                context == null
                    ? $"expected one of {list} but found {Parser.DescribeToken(current)}"
                    : $"expected one of {list} {context} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedKeyword,
                primaryLabel: "one of the listed keywords expected here");
        }

        // ---------------------------------------------------------------
        // Expressions
        // ---------------------------------------------------------------

        internal static InvalidSyntaxError ExpectedExpression(Token current, string? after = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                after == null
                    ? $"expected an expression but found {Parser.DescribeToken(current)}"
                    : $"expected an expression after {after} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedExpression,
                help: "an expression may start with a literal, an identifier, '+', '-', '(', '[', '{' or a keyword such as 'if', 'for', 'while', 'fn', 'not'",
                primaryLabel: "expression expected here");

        internal static InvalidSyntaxError ExpectedStatement(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected a statement but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedExpression,
                help: "a statement may start with 'var', 'let', 'const', 'final', 'if', 'for', 'while', 'fn', 'return', 'continue', 'break' or any expression",
                primaryLabel: "statement expected here");

        internal static InvalidSyntaxError InvalidAssignmentTarget(Position start, Position end, string? note = null) =>
            new InvalidSyntaxError(
                start, end,
                "invalid assignment target",
                DiagnosticCode.ParserInvalidSyntax,
                help: note ?? "only variables, member accesses and indexed expressions can appear on the left of '='",
                primaryLabel: "cannot assign to this expression");

        // ---------------------------------------------------------------
        // Generics
        // ---------------------------------------------------------------

        internal static InvalidSyntaxError ExpectedGenericParamName(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected a generic type parameter name but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedIdentifier,
                help: "generic parameters are identifiers such as 'T', 'U' or 'K, V'",
                primaryLabel: "generic parameter name expected here");

        internal static InvalidSyntaxError DuplicateGenericParam(string name, Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                $"duplicate generic type parameter '{name}'",
                DiagnosticCode.ParserInvalidSyntax,
                help: "each generic parameter name must be unique within its declaration",
                primaryLabel: "previous declaration already defined this name");

        internal static InvalidSyntaxError UnknownGenericParam(string name, Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                $"'where' clause references unknown generic parameter '{name}'",
                DiagnosticCode.ParserInvalidSyntax,
                help: "the parameter must be declared in the surrounding '<...>' generic parameter list",
                primaryLabel: "unknown generic parameter");

        internal static InvalidSyntaxError DuplicateWhereConstraint(string name, Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                $"duplicate 'where' constraint for '{name}'",
                DiagnosticCode.ParserInvalidSyntax,
                help: "merge the constraints into a single 'where' clause for this parameter",
                primaryLabel: "constraint already exists for this parameter");

        internal static InvalidSyntaxError WhereClauseRequiresGeneric(Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                "'where' clause requires generic type parameters",
                DiagnosticCode.ParserInvalidSyntax,
                help: "declare generic parameters with '<...>' before the 'where' clause",
                primaryLabel: "'where' here has no generic parameters to constrain");

        // ---------------------------------------------------------------
        // Members / declarations
        // ---------------------------------------------------------------

        internal static InvalidSyntaxError DuplicateModifier(string modifier, Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                $"member is already marked '{modifier}'",
                DiagnosticCode.ParserInvalidSyntax,
                help: $"remove the duplicate '{modifier}' modifier",
                primaryLabel: "duplicate modifier");

        internal static InvalidSyntaxError ExtensionConstructorNotAllowed(Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                "extensions cannot declare constructors",
                DiagnosticCode.ParserInvalidSyntax,
                help: "extension blocks add methods to an existing type; declare the constructor on the original type",
                primaryLabel: "constructor not allowed inside an extension");

        internal static InvalidSyntaxError ExtensionMethodNeedsBody(Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                "extension methods must have a body",
                DiagnosticCode.ParserInvalidSyntax,
                help: "provide a body with '{ ... }' or '=> expr' — abstract extensions are not allowed",
                primaryLabel: "missing method body");

        internal static InvalidSyntaxError DefaultParameterMustBeTrailing(Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                "non-default parameters cannot follow default parameters",
                DiagnosticCode.ParserInvalidSyntax,
                help: "reorder parameters so all default-valued ones appear at the end of the list",
                primaryLabel: "default parameter precedes a required parameter");

        internal static InvalidSyntaxError VariadicMustBeLast(Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                "variadic parameter must be the last parameter",
                DiagnosticCode.ParserInvalidSyntax,
                help: "move the '...' variadic parameter to the end of the parameter list",
                primaryLabel: "variadic parameter is not in trailing position");

        internal static InvalidSyntaxError InterfaceFieldHasDefault(Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                "interface fields cannot declare a default value",
                DiagnosticCode.ParserInvalidSyntax,
                help: "interfaces only describe the shape; provide default values in classes / structs that implement the interface",
                primaryLabel: "default value not allowed here");

        internal static InvalidSyntaxError MapAndSetCannotMix(Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                "cannot mix map (key: value) and set (value-only) entries in the same literal",
                DiagnosticCode.ParserInvalidSyntax,
                help: "use either 'value' for set entries or 'key: value' for map entries — not both",
                primaryLabel: "incompatible literal entries");

        internal static InvalidSyntaxError AnnotationBodyMustBeEmpty(Position start, Position end) =>
            new InvalidSyntaxError(
                start, end,
                "annotation declarations cannot have a body",
                DiagnosticCode.ParserInvalidSyntax,
                help: "remove the '{ ... }' body — annotations only declare parameters",
                primaryLabel: "unexpected body");

        internal static InvalidSyntaxError InvalidOperatorOverload(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"'{Parser.DescribeToken(current)}' is not a valid overloadable operator keyword",
                DiagnosticCode.ParserInvalidSyntax,
                help: "only 'and' and 'or' keywords can be overloaded as logical operators",
                primaryLabel: "not overloadable");

        internal static InvalidSyntaxError ExpectedOperatorBody(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected operator body '{{ ... }}' or '=> expr' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserInvalidSyntax,
                help: "every operator overload must have an implementation",
                primaryLabel: "missing operator body");

        // ---------------------------------------------------------------
        // Constructs (catch, switch, for, etc.)
        // ---------------------------------------------------------------

        internal static InvalidSyntaxError ExpectedRetryFor(Token current) =>
            ExpectedKeyword(current, "for", context: "after 'retry'",
                help: "retry blocks have the form 'retry for N times { ... }'");

        internal static InvalidSyntaxError ExpectedRetryTimes(Token current) =>
            ExpectedKeyword(current, "times", context: "after the retry count",
                help: "retry blocks have the form 'retry for N times { ... }'");

        internal static InvalidSyntaxError ExpectedFromAfterImport(Token current) =>
            ExpectedKeyword(current, "from", context: "after import target list",
                help: "imports have the form 'import { a, b } from \"module\"' or 'import * from \"module\"'");

        internal static InvalidSyntaxError ExpectedImportSource(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected a module path string or identifier after 'from' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                help: "use \"./relative/path\" or a bare identifier such as 'std::io'",
                primaryLabel: "module source expected here");

        internal static InvalidSyntaxError ExpectedCaseOrDefault(Token current) =>
            ExpectedOneOfKeywords(current, new[] { "case", "default" }, context: "inside the 'switch' body");

        internal static InvalidSyntaxError ExpectedRangeTo(Token current) =>
            ExpectedKeyword(current, "to", context: "after the start of a 'for' range",
                help: "for-range loops have the form 'for i = a to b { ... }'");

        internal static InvalidSyntaxError ExpectedForLoopBinder(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected '=' (range loop) or 'in' (each loop) but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedKeyword,
                help: "use 'for i = a to b' for ranges or 'for x in collection' for iteration",
                primaryLabel: "loop binder operator expected here");

        internal static InvalidSyntaxError ExpectedTypeAfterColon(Token current, string? where = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                where == null
                    ? $"expected a type after ':' but found {Parser.DescribeToken(current)}"
                    : $"expected a type after ':' in {where} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedType,
                primaryLabel: "type expected here");

        internal static InvalidSyntaxError ExpectedReturnType(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected a return type after '->' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedType,
                help: "use '-> int', '-> string', '-> void' or omit the arrow for an inferred return type",
                primaryLabel: "return type expected here");

        internal static InvalidSyntaxError ExpectedVarArgsType(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected a type for the variadic parameter after ':' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedType,
                help: "variadic parameters need a type, e.g. 'values: ...int'",
                primaryLabel: "variadic type expected here");

        // ---------------------------------------------------------------
        // Misc one-shots
        // ---------------------------------------------------------------

        internal static InvalidSyntaxError ExpectedAtSign(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected '@' to start an annotation but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                primaryLabel: "expected '@' here");

        internal static InvalidSyntaxError ExpectedAnnotationName(Token current, string? after = null) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                after == null
                    ? $"expected an annotation name but found {Parser.DescribeToken(current)}"
                    : $"expected an annotation name after {after} but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedIdentifier,
                primaryLabel: "annotation name expected here");

        internal static InvalidSyntaxError ExpectedExprAfterEllipsis(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected an expression after '...' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedExpression,
                help: "the spread operator '...' must be followed by an iterable expression",
                primaryLabel: "expression expected here");

        internal static InvalidSyntaxError ExpectedInterpClose(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected '}}' to close the string interpolation '${{ ... }}' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                help: "make sure every '${' has a matching '}' inside the same string literal",
                primaryLabel: "interpolation never closed");

        internal static InvalidSyntaxError ExpectedAsmInterpClose(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected '}}' to close the asm interpolation '%{{ ... }}' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                primaryLabel: "asm interpolation never closed");

        internal static InvalidSyntaxError ExpectedCsharpInterpClose(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected '}}' to close the csharp interpolation '%{{ ... }}' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                primaryLabel: "csharp interpolation never closed");

        internal static InvalidSyntaxError ExpectedCsharpReferencePath(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected a string literal with an assembly path after 'ref' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedToken,
                help: "csharp 'ref' references take literal string paths, e.g. ref \"System.Text.Json.dll\"",
                primaryLabel: "string literal expected here");

        internal static InvalidSyntaxError ExpectedCsharpUsingNamespace(Token current) =>
            new InvalidSyntaxError(
                current.PositionStart, current.PositionEnd,
                $"expected a namespace identifier after 'using' but found {Parser.DescribeToken(current)}",
                DiagnosticCode.ParserExpectedIdentifier,
                help: "csharp 'using' takes dotted namespace identifiers, e.g. using System.Text",
                primaryLabel: "namespace expected here");
    }
}

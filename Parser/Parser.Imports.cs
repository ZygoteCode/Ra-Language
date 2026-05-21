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
        private ParserResult ParseImportStatement()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                return ParseImportSelective(res, positionStart);
            }

            var spec = ParseModuleSpecifier(res);
            if (res.Error != null) return res;
            if (spec == null)
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentToken.PositionStart, _currentToken.PositionEnd,
                    $"expected a string path, '{{' selector or dotted module name after 'import' but found {DescribeToken(_currentToken)}",
                    DiagnosticCode.ParserExpectedToken,
                    help: "imports look like 'import \"./mod.ra\"', 'import std.io' or 'import { a, b } from \"./mod.ra\"'",
                    primaryLabel: "module specifier expected here"));
            }

            if (_currentToken.Type == TokenType.KEYWORD && _currentToken.Matches(Keyword.As))
            {
                res.RegisterAdvancement();
                Advance();

                SkipNewlines(res);

                if (_currentToken.Type != TokenType.IDENTIFIER)
                {
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'as'",
                        help: "the alias must be a single identifier, e.g. 'import std.io as IO'"));
                }

                var aliasTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                return res.Success(new ImportAliasNode(spec, aliasTok, positionStart, _currentToken.PositionEnd));
            }

            return res.Success(new ImportAllNode(spec, positionStart, _currentToken.PositionEnd));
        }

        private ParserResult ParseImportSelective(ParserResult res, Position positionStart)
        {
            res.RegisterAdvancement();
            Advance();

            var symbolNames = new List<Token>();
            while (_currentToken.Type != TokenType.RBRACKET)
            {
                SkipNewlines(res);

                if (_currentToken.Type != TokenType.IDENTIFIER)
                {
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'{'",
                        help: "the selective import list contains comma-separated symbol names"));
                }

                symbolNames.Add(_currentToken);
                res.RegisterAdvancement();
                Advance();

                SkipNewlines(res);

                if (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else if (_currentToken.Type != TokenType.RBRACKET)
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "',' or '}'",
                        contextHint: "the selective import list is comma-separated and ends with '}'"));
                }
            }

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            if (!_currentToken.Matches(Keyword.From))
            {
                return res.Failure(ParserDiagnostics.ExpectedFromAfterImport(_currentToken));
            }

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            var spec = ParseModuleSpecifier(res);
            if (res.Error != null) return res;
            if (spec == null)
            {
                return res.Failure(ParserDiagnostics.ExpectedImportSource(_currentToken));
            }

            return res.Success(new ImportSelectiveNode(spec, symbolNames, positionStart, _currentToken.PositionEnd));
        }

        private ParserResult ParseNamespaceDeclaration()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            var segments = ParseQualifiedNameSegments(res);
            if (res.Error != null) return res;
            if (segments == null || segments.Count == 0)
            {
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "'namespace'",
                    help: "namespace names are dotted identifiers, e.g. 'namespace system.io { ... }'"));
            }

            SkipNewlines(res);

            if (_currentToken.Type != TokenType.LBRACKET)
            {
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the namespace body"));
            }

            var bodyStart = _currentToken.PositionStart;
            res.RegisterAdvancement();
            Advance();

            var body = res.Register(ParseStatements());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RBRACKET)
            {
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the namespace body"));
            }

            var bodyEnd = _currentToken.PositionEnd;
            res.RegisterAdvancement();
            Advance();

            return res.Success(new NamespaceDeclarationNode(
                segments,
                body!,
                isFileScoped: false,
                positionStart,
                bodyEnd));
        }

        private ParserResult ParseUsingStatement()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            res.RegisterAdvancement();
            Advance();

            SkipNewlines(res);

            var segments = ParseQualifiedNameSegments(res);
            if (res.Error != null) return res;
            if (segments == null || segments.Count == 0)
            {
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "'using'",
                    help: "using takes a dotted namespace path, e.g. 'using system.io'"));
            }

            Token? aliasTok = null;
            if (_currentToken.Type == TokenType.KEYWORD && _currentToken.Matches(Keyword.As))
            {
                res.RegisterAdvancement();
                Advance();
                SkipNewlines(res);

                if (_currentToken.Type != TokenType.IDENTIFIER)
                {
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'as'",
                        help: "the using-alias must be a single identifier, e.g. 'using system.io as IO'"));
                }
                aliasTok = _currentToken;
                res.RegisterAdvancement();
                Advance();
            }

            return res.Success(new UsingNamespaceNode(
                segments,
                aliasTok,
                positionStart,
                _currentToken.PositionEnd));
        }

        private List<Token>? ParseQualifiedNameSegments(ParserResult res)
        {
            if (_currentToken.Type != TokenType.IDENTIFIER)
            {
                res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    help: "qualified names are dotted identifiers (e.g. 'system.io.console')"));
                return null;
            }

            var segments = new List<Token> { _currentToken };
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.DOT)
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentToken.Type != TokenType.IDENTIFIER)
                {
                    res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'.'",
                        help: "each dotted segment must be an identifier"));
                    return null;
                }

                segments.Add(_currentToken);
                res.RegisterAdvancement();
                Advance();
            }

            return segments;
        }

        private Interpreter.Modules.ModuleSpecifier? ParseModuleSpecifier(ParserResult res)
        {
            if (_currentToken.Type == TokenType.STRING_TEXT)
            {
                string rawPath = _currentToken.Value?.ToString() ?? "";
                res.RegisterAdvancement();
                Advance();
                return Interpreter.Modules.ModuleSpecifier.FromStringLiteral(rawPath);
            }

            if (_currentToken.Type == TokenType.IDENTIFIER)
            {
                var segments = new List<string>();
                segments.Add(_currentToken.Value?.ToString() ?? "");
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.DOT)
                {
                    res.RegisterAdvancement();
                    Advance();

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                    {
                        res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                            after: "'.' in module path",
                            help: "module paths are dotted identifiers, e.g. 'std.io.file'"));
                        return null;
                    }

                    segments.Add(_currentToken.Value?.ToString() ?? "");
                    res.RegisterAdvancement();
                    Advance();
                }

                return Interpreter.Modules.ModuleSpecifier.FromDotted(segments);
            }

            return null;
        }
    }
}

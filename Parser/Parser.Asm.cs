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
        private ParserResult ParseAsmBlock()
        {
            var res = new ParserResult();
            var positionStart = _currentToken.PositionStart;

            if (!_currentToken.Matches(Keyword.Asm))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "asm", context: "to start an inline assembly block"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            var returnTypes = new List<string>();

            if (_currentToken.Type == TokenType.ARROW_RIGHT)
            {
                res.RegisterAdvancement();
                Advance();
                while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

                if (_currentToken.Type == TokenType.LPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();
                    while (_currentToken.Type != TokenType.RPAREN && _currentToken.Type != TokenType.EOF)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE || _currentToken.Type == TokenType.COMMA) { res.RegisterAdvancement(); Advance(); }
                        if (_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.KEYWORD)
                        {
                            returnTypes.Add(_currentToken.Value?.ToString() ?? "");
                            res.RegisterAdvancement();
                            Advance();
                        }
                        else break;
                    }
                    if (_currentToken.Type != TokenType.RPAREN)
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(', context: "the asm return type list"));
                    res.RegisterAdvancement();
                    Advance();
                }
                else if (_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.KEYWORD)
                {
                    returnTypes.Add(_currentToken.Value?.ToString() ?? "");
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    return res.Failure(ParserDiagnostics.ExpectedReturnType(_currentToken));
                }
            }

            while (_currentToken.Type == TokenType.NEWLINE) { res.RegisterAdvancement(); Advance(); }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the asm block"));

            res.RegisterAdvancement();
            Advance();

            var parts = new List<AstNode>();

            while (_currentToken.Type == TokenType.ASM_TEXT || _currentToken.Type == TokenType.INTERP_START)
            {
                if (_currentToken.Type == TokenType.ASM_TEXT)
                {
                    var textTok = _currentToken;
                    parts.Add(new RaLanguage.Parser.Nodes.Asm.AsmTextPartNode(textTok.Value?.ToString() ?? "", textTok.PositionStart, textTok.PositionEnd));
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    var interpStartPos = _currentToken.PositionStart;
                    res.RegisterAdvancement();
                    Advance();

                    var expr = res.Register(ParseExpression());
                    if (res.Error != null) return res;

                    if (_currentToken.Type != TokenType.INTERP_END)
                        return res.Failure(ParserDiagnostics.ExpectedAsmInterpClose(_currentToken));

                    string? typeHint = _currentToken.Value as string;
                    var interpEndPos = _currentToken.PositionEnd;
                    res.RegisterAdvancement();
                    Advance();

                    parts.Add(new RaLanguage.Parser.Nodes.Asm.AsmInterpPartNode(expr, typeHint, interpStartPos, interpEndPos));
                }
            }

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the asm block"));

            var positionEnd = _currentToken.PositionEnd;
            res.RegisterAdvancement();
            Advance();

            var node = new RaLanguage.Parser.Nodes.Asm.AsmBlockNode(parts, positionStart, positionEnd);
            node.ReturnTypes = returnTypes;
            return res.Success(node);
        }
    }
}

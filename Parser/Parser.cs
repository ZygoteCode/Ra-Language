using RaLanguage.Errors.Types;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Parser
{
    public class Parser
    {
        private readonly List<Token> _tokens;
        private int _tokIdx;
        private Token _currentTok;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
            _tokIdx = -1;
            Advance();
        }

        private Token Advance()
        {
            _tokIdx++;
            UpdateCurrentTok();
            return _currentTok;
        }

        private Token Reverse(int amount = 1)
        {
            _tokIdx -= amount;
            UpdateCurrentTok();
            return _currentTok;
        }

        private void UpdateCurrentTok()
        {
            if (_tokIdx >= 0 && _tokIdx < _tokens.Count)
                _currentTok = _tokens[_tokIdx];
        }

        public ParseResult Parse()
        {
            var res = Statements();
            if (res.Error == null && _currentTok.Type != TokenType.EOF)
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentTok.PosStart, _currentTok.PosEnd,
                    "Token cannot appear after previous tokens"
                ));
            }
            return res;
        }

        private ParseResult Statements()
        {
            var res = new ParseResult();
            var statements = new List<AstNode>();
            var positionStart = _currentTok.PosStart.Copy();

            while (_currentTok.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var statement = res.Register(Statement());
            if (res.Error != null) return res;
            statements.Add(statement);

            bool moreStatements = true;

            while (true)
            {
                int newlineCount = 0;
                while (_currentTok.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                    newlineCount++;
                }

                if (newlineCount == 0) moreStatements = false;
                if (!moreStatements) break;

                var stmt = res.TryRegister(Statement());
                if (stmt == null)
                {
                    Reverse(res.ToReverseCount);
                    moreStatements = false;
                    continue;
                }
                statements.Add(stmt);
            }

            return res.Success(new ListNode(
                statements,
                positionStart,
                _currentTok.PosEnd.Copy()
            ));
        }

        private ParseResult Statement()
        {
            var res = new ParseResult();
            var positionStart = _currentTok.PosStart.Copy();

            if (_currentTok.Matches(TokenType.KEYWORD, "RETURN"))
            {
                res.RegisterAdvancement();
                Advance();

                var expr = res.TryRegister(Expr());
                if (expr == null) Reverse(res.ToReverseCount);
                return res.Success(new ReturnNode(expr, positionStart, _currentTok.PosStart.Copy()));
            }

            if (_currentTok.Matches(TokenType.KEYWORD, "CONTINUE"))
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new ContinueNode(positionStart, _currentTok.PosStart.Copy()));
            }

            if (_currentTok.Matches(TokenType.KEYWORD, "BREAK"))
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new BreakNode(positionStart, _currentTok.PosStart.Copy()));
            }

            var expression = res.Register(Expr());
            if (res.Error != null)
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentTok.PosStart, _currentTok.PosEnd,
                    "Expected 'RETURN', 'CONTINUE', 'BREAK', 'VAR', 'IF', 'FOR', 'WHILE', 'FUN', int, float, identifier, '+', '-', '(', '[' or 'NOT'"
                ));
            }
            return res.Success(expression);
        }

        private ParseResult Expr()
        {
            var res = new ParseResult();

            if (_currentTok.Matches(TokenType.KEYWORD, "VAR"))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentTok.Type != TokenType.IDENTIFIER)
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected identifier"));

                var varName = _currentTok;
                res.RegisterAdvancement();
                Advance();

                if (_currentTok.Type != TokenType.EQ)
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected '='"));

                res.RegisterAdvancement();
                Advance();
                var expr = res.Register(Expr());
                if (res.Error != null) return res;
                return res.Success(new VarAssignNode(varName, expr));
            }

            var node = res.Register(BinOp(CompExpr, new List<(TokenType, string?)> { (TokenType.KEYWORD, "AND"), (TokenType.KEYWORD, "OR") }));

            if (res.Error != null)
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentTok.PosStart, _currentTok.PosEnd,
                    "Expected 'VAR', 'IF', 'FOR', 'WHILE', 'FUN', int, float, identifier, '+', '-', '(', '[' or 'NOT'"
                ));
            }

            return res.Success(node);
        }

        private ParseResult CompExpr()
        {
            var res = new ParseResult();

            if (_currentTok.Matches(TokenType.KEYWORD, "NOT"))
            {
                var opTok = _currentTok;
                res.RegisterAdvancement();
                Advance();

                var node = res.Register(CompExpr());
                if (res.Error != null) return res;
                return res.Success(new UnaryOpNode(opTok, node));
            }

            var b_node = res.Register(BinOp(ArithExpr, new List<(TokenType, string?)>
            {
                (TokenType.EE, null), (TokenType.NE, null), (TokenType.LT, null),
                (TokenType.GT, null), (TokenType.LTE, null), (TokenType.GTE, null)
            }));

            if (res.Error != null)
            {
                return res.Failure(new InvalidSyntaxError(
                   _currentTok.PosStart, _currentTok.PosEnd,
                   "Expected int, float, identifier, '+', '-', '(', '[', 'IF', 'FOR', 'WHILE', 'FUN' or 'NOT'"
               ));
            }
            return res.Success(b_node);
        }

        private ParseResult ArithExpr()
        {
            return BinOp(Term, new List<(TokenType, string?)> { (TokenType.PLUS, null), (TokenType.MINUS, null) });
        }

        private ParseResult Term()
        {
            return BinOp(Factor, new List<(TokenType, string?)> { (TokenType.MUL, null), (TokenType.DIV, null) });
        }

        private ParseResult Factor()
        {
            var res = new ParseResult();
            var tok = _currentTok;

            if (tok.Type == TokenType.PLUS || tok.Type == TokenType.MINUS)
            {
                res.RegisterAdvancement();
                Advance();
                var factor = res.Register(Factor());
                if (res.Error != null) return res;
                return res.Success(new UnaryOpNode(tok, factor));
            }

            return Power();
        }

        private ParseResult Power()
        {
            return BinOp(Call, new List<(TokenType, string?)> { (TokenType.POW, null) }, Factor);
        }

        private ParseResult Call()
        {
            var res = new ParseResult();
            var atom = res.Register(Atom());
            if (res.Error != null) return res;

            if (_currentTok.Type == TokenType.LPAREN)
            {
                res.RegisterAdvancement();
                Advance();
                var argNodes = new List<AstNode>();

                if (_currentTok.Type == TokenType.RPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    argNodes.Add(res.Register(Expr()));
                    if (res.Error != null)
                        return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected ')', 'VAR', 'IF', 'FOR', 'WHILE', 'FUN', int, float, identifier, '+', '-', '(', '[' or 'NOT'"));

                    while (_currentTok.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();
                        argNodes.Add(res.Register(Expr()));
                        if (res.Error != null) return res;
                    }

                    if (_currentTok.Type != TokenType.RPAREN)
                        return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected ',' or ')'"));

                    res.RegisterAdvancement();
                    Advance();
                }
                return res.Success(new CallNode(atom, argNodes));
            }
            return res.Success(atom);
        }

        private ParseResult Atom()
        {
            var res = new ParseResult();
            var tok = _currentTok;

            if (tok.Type == TokenType.INT || tok.Type == TokenType.FLOAT)
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new NumberNode(tok));
            }
            else if (tok.Type == TokenType.STRING)
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new StringNode(tok));
            }
            else if (tok.Type == TokenType.IDENTIFIER)
            {
                res.RegisterAdvancement();
                Advance();
                return res.Success(new VarAccessNode(tok));
            }
            else if (tok.Type == TokenType.LPAREN)
            {
                res.RegisterAdvancement();
                Advance();
                var expr = res.Register(Expr());
                if (res.Error != null) return res;
                if (_currentTok.Type == TokenType.RPAREN)
                {
                    res.RegisterAdvancement();
                    Advance();
                    return res.Success(expr);
                }
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected ')'"));
            }
            else if (tok.Type == TokenType.LSQUARE)
            {
                var listExpr = res.Register(ListExpr());
                if (res.Error != null) return res;
                return res.Success(listExpr);
            }
            else if (tok.Matches(TokenType.KEYWORD, "IF"))
            {
                var ifExpr = res.Register(IfExpr());
                if (res.Error != null) return res;
                return res.Success(ifExpr);
            }
            else if (tok.Matches(TokenType.KEYWORD, "FOR"))
            {
                var forExpr = res.Register(ForExpr());
                if (res.Error != null) return res;
                return res.Success(forExpr);
            }
            else if (tok.Matches(TokenType.KEYWORD, "WHILE"))
            {
                var whileExpr = res.Register(WhileExpr());
                if (res.Error != null) return res;
                return res.Success(whileExpr);
            }
            else if (tok.Matches(TokenType.KEYWORD, "FUN"))
            {
                var funcDef = res.Register(FuncDef());
                if (res.Error != null) return res;
                return res.Success(funcDef);
            }

            return res.Failure(new InvalidSyntaxError(tok.PosStart, tok.PosEnd, "Expected int, float, identifier, '+', '-', '(', '[', IF', 'FOR', 'WHILE', 'FUN'"));
        }

        private ParseResult ListExpr()
        {
            var res = new ParseResult();
            var elementNodes = new List<AstNode>();
            var positionStart = _currentTok.PosStart.Copy();

            if (_currentTok.Type != TokenType.LSQUARE)
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected '['"));

            res.RegisterAdvancement();
            Advance();

            if (_currentTok.Type == TokenType.RSQUARE)
            {
                res.RegisterAdvancement();
                Advance();
            }
            else
            {
                elementNodes.Add(res.Register(Expr()));
                if (res.Error != null)
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected ']', 'VAR', 'IF', 'FOR', 'WHILE', 'FUN', int, float, identifier, '+', '-', '(', '[' or 'NOT'"));

                while (_currentTok.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                    elementNodes.Add(res.Register(Expr()));
                    if (res.Error != null) return res;
                }

                if (_currentTok.Type != TokenType.RSQUARE)
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected ',' or ']'"));

                res.RegisterAdvancement();
                Advance();
            }

            return res.Success(new ListNode(elementNodes, positionStart, _currentTok.PosEnd.Copy()));
        }

        private ParseResult IfExpr()
        {
            var res = new ParseResult();

            // 1. Otteniamo il risultato grezzo che è, per nostra costruzione, un IfCasesWrapperNode
            var allCasesNode = res.Register(IfExprCases("IF"));
            if (res.Error != null) return res;

            // 2. Eseguiamo il cast esplicito. Qui siamo sicuri del tipo perché IfExprCases ritorna sempre questo wrapper.
            // Questo risolve l'errore di casting che avevi prima.
            var wrapper = (IfCasesWrapperNode)allCasesNode;

            // 3. Costruiamo il nodo AST finale pulito
            return res.Success(new IfNode(wrapper.Cases, wrapper.ElseCase));
        }

        private ParseResult IfExprCases(string caseKeyword)
        {
            var res = new ParseResult();
            var cases = new List<(AstNode, AstNode, bool)>();
            (AstNode, bool)? elseCase = null;

            // Verifica KEYWORD (IF o ELIF)
            if (!_currentTok.Matches(TokenType.KEYWORD, caseKeyword))
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, $"Expected '{caseKeyword}'"));

            res.RegisterAdvancement();
            Advance();

            // Parsing della Condizione
            var condition = res.Register(Expr());
            if (res.Error != null) return res;

            // Verifica THEN
            if (!_currentTok.Matches(TokenType.KEYWORD, "THEN"))
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'THEN'"));

            res.RegisterAdvancement();
            Advance();

            // Gestione Logica: Blocco Multilinea vs Singola Linea
            if (_currentTok.Type == TokenType.NEWLINE)
            {
                // --- Caso Multilinea ---
                res.RegisterAdvancement();
                Advance();

                var statements = res.Register(Statements());
                if (res.Error != null) return res;

                // Aggiungiamo il caso corrente alla lista
                cases.Add((condition, statements, true));

                // Dopo il blocco di statement, controlliamo se finisce qui (END) o continua (ELIF/ELSE)
                if (_currentTok.Matches(TokenType.KEYWORD, "END"))
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    // Ricorsione per ELIF o ELSE
                    var chainNode = res.Register(IfExprBOrC());
                    if (res.Error != null) return res;

                    // Unboxing del wrapper ritornato dalla ricorsione
                    var wrapper = (IfCasesWrapperNode)chainNode;
                    cases.AddRange(wrapper.Cases);
                    elseCase = wrapper.ElseCase;
                }
            }
            else
            {
                // --- Caso Singola Linea ---
                var expr = res.Register(Statement());
                if (res.Error != null) return res;

                // Aggiungiamo il caso corrente
                cases.Add((condition, expr, false));

                // Ricorsione immediata per ELIF/ELSE (che in singola linea non richiedono END prima)
                var chainNode = res.Register(IfExprBOrC());
                if (res.Error != null) return res;

                // Unboxing del wrapper
                var wrapper = (IfCasesWrapperNode)chainNode;
                cases.AddRange(wrapper.Cases);
                elseCase = wrapper.ElseCase;
            }

            // Ritorniamo il Wrapper invece del nodo AST finale, perché potremmo essere dentro un ELIF ricorsivo
            return res.Success(new IfCasesWrapperNode(cases, elseCase));
        }

        private ParseResult IfExprBOrC()
        {
            var res = new ParseResult();
            var cases = new List<(AstNode, AstNode, bool)>();
            (AstNode, bool)? elseCase = null;

            if (_currentTok.Matches(TokenType.KEYWORD, "ELIF"))
            {
                // Se è ELIF, chiamiamo ricorsivamente IfExprCases
                var node = res.Register(IfExprCases("ELIF"));
                if (res.Error != null) return res;

                // Il risultato è un wrapper che contiene i casi dell'ELIF e i successivi
                var wrapper = (IfCasesWrapperNode)node;
                cases = wrapper.Cases;
                elseCase = wrapper.ElseCase;
            }
            else
            {
                // Se non è ELIF, proviamo ELSE
                var node = res.Register(IfExprC());
                if (res.Error != null) return res;

                // Il risultato è un wrapper che contiene solo l'eventuale ElseCase
                var wrapper = (IfCasesWrapperNode)node;
                elseCase = wrapper.ElseCase;
            }

            return res.Success(new IfCasesWrapperNode(cases, elseCase));
        }

        private ParseResult IfExprC()
        {
            var res = new ParseResult();
            (AstNode, bool)? elseCase = null;

            if (_currentTok.Matches(TokenType.KEYWORD, "ELSE"))
            {
                res.RegisterAdvancement();
                Advance();

                if (_currentTok.Type == TokenType.NEWLINE)
                {
                    // ELSE Multilinea
                    res.RegisterAdvancement();
                    Advance();

                    var statements = res.Register(Statements());
                    if (res.Error != null) return res;
                    elseCase = (statements, true);

                    if (_currentTok.Matches(TokenType.KEYWORD, "END"))
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                    else
                    {
                        return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'END'"));
                    }
                }
                else
                {
                    // ELSE Singola linea
                    var expr = res.Register(Statement());
                    if (res.Error != null) return res;
                    elseCase = (expr, false);
                }
            }

            // Ritorniamo un wrapper con lista casi vuota (perché siamo nell'ELSE) e l'eventuale body dell'ELSE
            return res.Success(new IfCasesWrapperNode(new List<(AstNode, AstNode, bool)>(), elseCase));
        }

        // Classe di supporto interna per trasportare i dati intermedi del parsing degli IF/ELIF/ELSE.
        // Eredita da AstNode per essere compatibile con ParseResult.Register.
        private class IfCasesWrapperNode : AstNode
        {
            public List<(AstNode Condition, AstNode Body, bool ShouldReturnNull)> Cases { get; }
            public (AstNode Body, bool ShouldReturnNull)? ElseCase { get; }

            public IfCasesWrapperNode(List<(AstNode, AstNode, bool)> cases, (AstNode, bool)? elseCase)
            {
                Cases = cases;
                ElseCase = elseCase;
            }
        }

        private ParseResult ForExpr()
        {
            var res = new ParseResult();

            if (!_currentTok.Matches(TokenType.KEYWORD, "FOR"))
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'FOR'"));

            res.RegisterAdvancement();
            Advance();

            if (_currentTok.Type != TokenType.IDENTIFIER)
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected identifier"));

            var varName = _currentTok;
            res.RegisterAdvancement();
            Advance();

            if (_currentTok.Type != TokenType.EQ)
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected '='"));

            res.RegisterAdvancement();
            Advance();

            var startValue = res.Register(Expr());
            if (res.Error != null) return res;

            if (!_currentTok.Matches(TokenType.KEYWORD, "TO"))
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'TO'"));

            res.RegisterAdvancement();
            Advance();

            var endValue = res.Register(Expr());
            if (res.Error != null) return res;

            AstNode? stepValue = null;
            if (_currentTok.Matches(TokenType.KEYWORD, "STEP"))
            {
                res.RegisterAdvancement();
                Advance();
                stepValue = res.Register(Expr());
                if (res.Error != null) return res;
            }

            if (!_currentTok.Matches(TokenType.KEYWORD, "THEN"))
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'THEN'"));

            res.RegisterAdvancement();
            Advance();

            if (_currentTok.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();

                var body = res.Register(Statements());
                if (res.Error != null) return res;

                if (!_currentTok.Matches(TokenType.KEYWORD, "END"))
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'END'"));

                res.RegisterAdvancement();
                Advance();
                return res.Success(new ForNode(varName, startValue, endValue, stepValue, body, true));
            }

            var bodyInline = res.Register(Statement());
            if (res.Error != null) return res;
            return res.Success(new ForNode(varName, startValue, endValue, stepValue, bodyInline, false));
        }

        private ParseResult WhileExpr()
        {
            var res = new ParseResult();
            if (!_currentTok.Matches(TokenType.KEYWORD, "WHILE"))
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'WHILE'"));

            res.RegisterAdvancement();
            Advance();

            var condition = res.Register(Expr());
            if (res.Error != null) return res;

            if (!_currentTok.Matches(TokenType.KEYWORD, "THEN"))
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'THEN'"));

            res.RegisterAdvancement();
            Advance();

            if (_currentTok.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();

                var body = res.Register(Statements());
                if (res.Error != null) return res;

                if (!_currentTok.Matches(TokenType.KEYWORD, "END"))
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'END'"));

                res.RegisterAdvancement();
                Advance();
                return res.Success(new WhileNode(condition, body, true));
            }

            var bodyInline = res.Register(Statement());
            if (res.Error != null) return res;
            return res.Success(new WhileNode(condition, bodyInline, false));
        }

        private ParseResult FuncDef()
        {
            var res = new ParseResult();

            if (!_currentTok.Matches(TokenType.KEYWORD, "FUN"))
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'FUN'"));

            res.RegisterAdvancement();
            Advance();

            Token? varNameTok = null;
            if (_currentTok.Type == TokenType.IDENTIFIER)
            {
                varNameTok = _currentTok;
                res.RegisterAdvancement();
                Advance();
                if (_currentTok.Type != TokenType.LPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected '('"));
            }
            else
            {
                if (_currentTok.Type != TokenType.LPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected identifier or '('"));
            }

            res.RegisterAdvancement();
            Advance();
            var argNameToks = new List<Token>();

            if (_currentTok.Type == TokenType.IDENTIFIER)
            {
                argNameToks.Add(_currentTok);
                res.RegisterAdvancement();
                Advance();

                while (_currentTok.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                    if (_currentTok.Type != TokenType.IDENTIFIER)
                        return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected identifier"));

                    argNameToks.Add(_currentTok);
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentTok.Type != TokenType.RPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected ',' or ')'"));
            }
            else
            {
                if (_currentTok.Type != TokenType.RPAREN)
                    return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected identifier or ')'"));
            }

            res.RegisterAdvancement();
            Advance();

            if (_currentTok.Type == TokenType.ARROW)
            {
                res.RegisterAdvancement();
                Advance();
                var body = res.Register(Expr());
                if (res.Error != null) return res;
                return res.Success(new FuncDefNode(varNameTok, argNameToks, body, true));
            }

            if (_currentTok.Type != TokenType.NEWLINE)
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected '->' or NEWLINE"));

            res.RegisterAdvancement();
            Advance();

            var bodyStmts = res.Register(Statements());
            if (res.Error != null) return res;

            if (!_currentTok.Matches(TokenType.KEYWORD, "END"))
                return res.Failure(new InvalidSyntaxError(_currentTok.PosStart, _currentTok.PosEnd, "Expected 'END'"));

            res.RegisterAdvancement();
            Advance();
            return res.Success(new FuncDefNode(varNameTok, argNameToks, bodyStmts, false));
        }

        private ParseResult BinOp(Func<ParseResult> funcA, List<(TokenType, string?)> ops, Func<ParseResult>? funcB = null)
        {
            if (funcB == null) funcB = funcA;
            var res = new ParseResult();
            var left = res.Register(funcA());
            if (res.Error != null) return res;

            while (ops.Any(op => op.Item1 == _currentTok.Type && (op.Item2 == null || op.Item2 == _currentTok.Value?.ToString())))
            {
                var opTok = _currentTok;
                res.RegisterAdvancement();
                Advance();
                var right = res.Register(funcB());
                if (res.Error != null) return res;
                left = new BinOpNode(left, opTok, right);
            }
            return res.Success(left);
        }
    }
}
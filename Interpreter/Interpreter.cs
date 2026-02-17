using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter
{
    public class Interpreter
    {
        public RTResult Visit(AstNode node, Context context)
        {
            return node switch
            {
                NumberNode n => VisitNumberNode(n, context),
                StringNode s => VisitStringNode(s, context),
                ListNode l => VisitListNode(l, context),
                VarAccessNode v => VisitVarAccessNode(v, context),
                VarAssignNode v => VisitVarAssignNode(v, context),
                BinOpNode b => VisitBinOpNode(b, context),
                UnaryOpNode u => VisitUnaryOpNode(u, context),
                IfNode i => VisitIfNode(i, context),
                ForNode f => VisitForNode(f, context),
                WhileNode w => VisitWhileNode(w, context),
                FuncDefNode f => VisitFuncDefNode(f, context),
                CallNode c => VisitCallNode(c, context),
                ReturnNode r => VisitReturnNode(r, context),
                ContinueNode c => VisitContinueNode(c, context),
                BreakNode b => VisitBreakNode(b, context),
                _ => throw new Exception($"No visit method for {node.GetType().Name}")
            };
        }

        private RTResult VisitNumberNode(NumberNode node, Context context)
        {
            return new RTResult().Success(
                new Number(Convert.ToDouble(node.Tok.Value)).SetContext(context).SetPos(node.PosStart, node.PosEnd)
            );
        }

        private RTResult VisitStringNode(StringNode node, Context context)
        {
            return new RTResult().Success(
                new StringVal(node.Tok.Value?.ToString() ?? "").SetContext(context).SetPos(node.PosStart, node.PosEnd)
            );
        }

        private RTResult VisitListNode(ListNode node, Context context)
        {
            var res = new RTResult();
            var elements = new List<RuntimeValue>();

            foreach (var elementNode in node.ElementNodes)
            {
                var val = res.Register(Visit(elementNode, context));
                if (res.ShouldReturn()) return res;
                elements.Add(val);
            }

            return res.Success(
                new ListVal(elements).SetContext(context).SetPos(node.PosStart, node.PosEnd)
            );
        }

        private RTResult VisitVarAccessNode(VarAccessNode node, Context context)
        {
            var res = new RTResult();
            var varName = node.VarNameTok.Value?.ToString();
            var value = context.SymbolTable.Get(varName);

            if (value == null)
            {
                return res.Failure(new RuntimeError(node.PosStart, node.PosEnd, $"'{varName}' is not defined", context));
            }

            value = value.Copy().SetPos(node.PosStart, node.PosEnd).SetContext(context);
            return res.Success(value);
        }

        private RTResult VisitVarAssignNode(VarAssignNode node, Context context)
        {
            var res = new RTResult();
            var varName = node.VarNameTok.Value?.ToString();
            var value = res.Register(Visit(node.ValueNode, context));
            if (res.ShouldReturn()) return res;

            context.SymbolTable.Set(varName, value);
            return res.Success(value);
        }

        private RTResult VisitBinOpNode(BinOpNode node, Context context)
        {
            var res = new RTResult();
            var left = res.Register(Visit(node.LeftNode, context));
            if (res.ShouldReturn()) return res;
            var right = res.Register(Visit(node.RightNode, context));
            if (res.ShouldReturn()) return res;

            (RuntimeValue? result, Error? error) = (null, null);

            switch (node.OpTok.Type)
            {
                case TokenType.PLUS: (result, error) = left.AddedTo(right); break;
                case TokenType.MINUS: (result, error) = left.SubbedBy(right); break;
                case TokenType.MUL: (result, error) = left.MultedBy(right); break;
                case TokenType.DIV: (result, error) = left.DivedBy(right); break;
                case TokenType.POW: (result, error) = left.PowedBy(right); break;
                case TokenType.EE: (result, error) = left.GetComparisonEq(right); break;
                case TokenType.NE: (result, error) = left.GetComparisonNe(right); break;
                case TokenType.LT: (result, error) = left.GetComparisonLt(right); break;
                case TokenType.GT: (result, error) = left.GetComparisonGt(right); break;
                case TokenType.LTE: (result, error) = left.GetComparisonLte(right); break;
                case TokenType.GTE: (result, error) = left.GetComparisonGte(right); break;
                case TokenType.KEYWORD when node.OpTok.Value?.ToString() == "AND": (result, error) = left.AndedBy(right); break;
                case TokenType.KEYWORD when node.OpTok.Value?.ToString() == "OR": (result, error) = left.OredBy(right); break;
            }

            if (error != null) return res.Failure(error);
            return res.Success(result!.SetPos(node.PosStart, node.PosEnd));
        }

        private RTResult VisitUnaryOpNode(UnaryOpNode node, Context context)
        {
            var res = new RTResult();
            var number = res.Register(Visit(node.Node, context));
            if (res.ShouldReturn()) return res;

            Error? error = null;

            if (node.OpTok.Type == TokenType.MINUS)
                (number, error) = number.MultedBy(new Number(-1));
            else if (node.OpTok.Matches(TokenType.KEYWORD, "NOT"))
                (number, error) = number.Notted();

            if (error != null) return res.Failure(error);
            return res.Success(number!.SetPos(node.PosStart, node.PosEnd));
        }

        private RTResult VisitIfNode(IfNode node, Context context)
        {
            var res = new RTResult();

            foreach (var (condition, expr, shouldReturnNull) in node.Cases)
            {
                var conditionValue = res.Register(Visit(condition, context));
                if (res.ShouldReturn()) return res;

                if (conditionValue.IsTrue())
                {
                    var exprValue = res.Register(Visit(expr, context));
                    if (res.ShouldReturn()) return res;
                    return res.Success(shouldReturnNull ? Number.Null : exprValue);
                }
            }

            if (node.ElseCase != null)
            {
                var (expr, shouldReturnNull) = node.ElseCase.Value;
                var exprValue = res.Register(Visit(expr, context));
                if (res.ShouldReturn()) return res;
                return res.Success(shouldReturnNull ? Number.Null : exprValue);
            }

            return res.Success(Number.Null);
        }

        private RTResult VisitForNode(ForNode node, Context context)
        {
            var res = new RTResult();
            var elements = new List<RuntimeValue>();

            var startValue = res.Register(Visit(node.StartValueNode, context));
            if (res.ShouldReturn()) return res;

            var endValue = res.Register(Visit(node.EndValueNode, context));
            if (res.ShouldReturn()) return res;

            RuntimeValue stepValue;
            if (node.StepValueNode != null)
            {
                stepValue = res.Register(Visit(node.StepValueNode, context));
                if (res.ShouldReturn()) return res;
            }
            else
            {
                stepValue = new Number(1);
            }

            double i = ((Number)startValue).Value;
            double end = ((Number)endValue).Value;
            double step = ((Number)stepValue).Value;

            Func<bool> condition = (step >= 0) ? () => i < end : () => i > end;

            while (condition())
            {
                context.SymbolTable.Set(node.VarNameTok.Value.ToString(), new Number(i));
                i += step;

                var value = res.Register(Visit(node.BodyNode, context));
                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;

                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;

                elements.Add(value);
            }

            return res.Success(
                node.ShouldReturnNull ? Number.Null : new ListVal(elements).SetContext(context).SetPos(node.PosStart, node.PosEnd)
            );
        }

        private RTResult VisitWhileNode(WhileNode node, Context context)
        {
            var res = new RTResult();
            var elements = new List<RuntimeValue>();

            while (true)
            {
                var condition = res.Register(Visit(node.ConditionNode, context));
                if (res.ShouldReturn()) return res;

                if (!condition.IsTrue()) break;

                var value = res.Register(Visit(node.BodyNode, context));
                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;

                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;

                elements.Add(value);
            }

            return res.Success(
               node.ShouldReturnNull ? Number.Null : new ListVal(elements).SetContext(context).SetPos(node.PosStart, node.PosEnd)
           );
        }

        private RTResult VisitFuncDefNode(FuncDefNode node, Context context)
        {
            var res = new RTResult();

            string funcName = node.VarNameTok != null ? node.VarNameTok.Value.ToString() : null;
            var argNames = node.ArgNameToks.Select(t => t.Value.ToString()).ToList();
            var funcValue = new Function(funcName, node.BodyNode, argNames, node.ShouldAutoReturn)
                .SetContext(context)
                .SetPos(node.PosStart, node.PosEnd);

            if (node.VarNameTok != null)
            {
                context.SymbolTable.Set(funcName, funcValue);
            }

            return res.Success(funcValue);
        }

        private RTResult VisitCallNode(CallNode node, Context context)
        {
            var res = new RTResult();
            var args = new List<RuntimeValue>();

            var valueToCall = res.Register(Visit(node.NodeToCall, context));
            if (res.ShouldReturn()) return res;
            valueToCall = valueToCall.Copy().SetPos(node.PosStart, node.PosEnd);

            foreach (var argNode in node.ArgNodes)
            {
                args.Add(res.Register(Visit(argNode, context)));
                if (res.ShouldReturn()) return res;
            }

            var returnValue = res.Register(valueToCall.Execute(args));
            if (res.ShouldReturn()) return res;

            returnValue = returnValue.Copy().SetPos(node.PosStart, node.PosEnd).SetContext(context);
            return res.Success(returnValue);
        }

        private RTResult VisitReturnNode(ReturnNode node, Context context)
        {
            var res = new RTResult();
            RuntimeValue value = Number.Null;

            if (node.NodeToReturn != null)
            {
                value = res.Register(Visit(node.NodeToReturn, context));
                if (res.ShouldReturn()) return res;
            }
            return res.SuccessReturn(value);
        }

        private RTResult VisitContinueNode(ContinueNode node, Context context) => new RTResult().SuccessContinue();

        private RTResult VisitBreakNode(BreakNode node, Context context) => new RTResult().SuccessBreak();
    }
}
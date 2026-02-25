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
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter
{
    public class Interpreter
    {
        private bool AreCallsBlocked { get; set; } = false;

        public RuntimeResult Visit(AstNode node, Context context)
        {
            return node switch
            {
                NumberNode n => VisitNumberNode(n, context),
                StringNode s => VisitStringNode(s, context),
                ListNode l => VisitListNode(l, context),
                VariableAccessNode v => VisitVariableAccessNode(v, context),
                VariableDeclarationNode v => VisitVariableDeclarationNode(v, context),
                VariableAssignmentNode v => VisitVariableAssignmentNode(v, context),
                VariableDeleteNode v => VisitVariableDeleteNode(v, context),
                BinaryOperationNode b => VisitBinaryOperationNode(b, context),
                UnaryOperationNode u => VisitUnaryOperationNode(u, context),
                IfNode i => VisitIfNode(i, context),
                ForNode f => VisitForNode(f, context),
                WhileNode w => VisitWhileNode(w, context),
                FunctionDefinitionNode f => VisitFunctionDefinitionNode(f, context),
                FunctionCallNode c => VisitFunctionCallNode(c, context),
                ReturnNode r => VisitReturnNode(r, context),
                ContinueNode c => VisitContinueNode(c, context),
                BreakNode b => VisitBreakNode(b, context),
                PassNode p => VisitPassNode(p, context),
                DoWhileNode d => VisitDoWhileNode(d, context),
                TypeofNode t => VisitTypeofNode(t, context),
                NameofNode n => VisitNameofNode(n, context),
                NullNode n => VisitNullNode(n, context),
                _ => throw new Exception($"No visit method for {node.GetType().Name}")
            };
        }

        private RuntimeResult VisitNullNode(NullNode node, Context context)
        {
            var res = new RuntimeResult();
            return res.Success(new NullValue().SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }

        private RuntimeResult VisitNameofNode(NameofNode node, Context context)
        {
            var res = new RuntimeResult();
            string varName = node.Token.Value.ToString();
            var value = context.SymbolTable.Get(varName);

            if (value == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Variable {varName} not defined", context));
            }

            return res.Success(new StringValue(varName).SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }

        private RuntimeResult VisitTypeofNode(TypeofNode node, Context context)
        {
            var res = new RuntimeResult();
            var value = res.Register(Visit(node.Node, context));

            if (res.Error != null)
            {
                return res;
            }

            string type = "";

            if (value.GetType() == typeof(NumberValue))
            {
                type = "number";
            }
            else if (value.GetType() == typeof(StringValue))
            {
                type = "string";
            }
            else if (value.GetType() == typeof(ListValue))
            {
                type = "list";
            }
            else if (value.GetType() == typeof(FunctionValue))
            {
                type = "function";
            }
            else if (value.GetType() == typeof(NullValue))
            {
                type = "null";
            }

            return res.Success(new StringValue(type).SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }

        private RuntimeResult VisitDoWhileNode(DoWhileNode node, Context context)
        {
            var res = new RuntimeResult();
            bool firstTime = true;
            List<RuntimeValue> elements = new List<RuntimeValue>();
            Context newContext = context.Copy();

            while (true)
            {
                var condition = res.Register(Visit(node.ConditionNode, newContext));

                if (res.ShouldReturn())
                {
                    return res;
                }

                if (!firstTime && !condition.IsTrue())
                {
                    break;
                }
                else
                {
                    firstTime = false;
                }

                Context iterationContext = newContext.Copy();
                var value = res.Register(Visit(node.BodyNode, iterationContext));
                newContext.ApplyChangesFrom(iterationContext);
                context.ApplyChangesFrom(newContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak)
                {
                    return res;
                }

                if (res.LoopShouldContinue)
                {
                    continue;
                }

                if (res.LoopShouldBreak)
                {
                    break;
                }

                elements.Add(value);
            }

            return res.Success(
                node.ShouldReturnNull ? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : new ListValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }

        private RuntimeResult VisitPassNode(PassNode node, Context context)
        {
            return new RuntimeResult().Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitNumberNode(NumberNode node, Context context)
        {
            return new RuntimeResult().Success(
                new NumberValue(BigNumber.Parse(node.Tok.Value.ToString())).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }

        private RuntimeResult VisitStringNode(StringNode node, Context context)
        {
            return new RuntimeResult().Success(
                new StringValue(node.Tok.Value?.ToString() ?? "").SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }

        private RuntimeResult VisitListNode(ListNode node, Context context)
        {
            var res = new RuntimeResult();
            var elements = new List<RuntimeValue>();

            if (node.IsNewContext)
            {
                Context newContext = context.Copy();

                foreach (var elementNode in node.ElementNodes)
                {
                    var val = res.Register(Visit(elementNode, newContext));
                    if (res.ShouldReturn()) return res;
                    elements.Add(val);
                }

                context.ApplyChangesFrom(newContext);
            }
            else
            {
                foreach (var elementNode in node.ElementNodes)
                {
                    var val = res.Register(Visit(elementNode, context));
                    if (res.ShouldReturn()) return res;
                    elements.Add(val);
                }
            }

            return res.Success(
                new ListValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }

        private RuntimeResult VisitVariableAccessNode(VariableAccessNode node, Context context)
        {
            var res = new RuntimeResult();
            var varName = node.VarNameTok.Value?.ToString();
            var value = context.SymbolTable.Get(varName);

            if (value == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is not defined", context));
            }

            value = value.Copy().SetPos(node.PositionStart, node.PositionEnd).SetContext(context);
            return res.Success(value);
        }

        private RuntimeResult VisitVariableDeclarationNode(VariableDeclarationNode node, Context context)
        {
            var res = new RuntimeResult();
            var varName = node.VarNameTok.Value?.ToString();

            if (context.SymbolTable.Get(varName) != null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is already defined", context));
            }

            if (node.DeclarationType.Equals(VariableDeclarationType.VARIABLE))
            {
                RuntimeValue value = new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

                if (node.ValueNode != null)
                {
                    value = res.Register(Visit(node.ValueNode, context));

                    if (res.ShouldReturn())
                    {
                        return res;
                    }
                }

                context.SymbolTable.Set(varName, value);
                return res.Success(value);
            }
            else if (node.DeclarationType.Equals(VariableDeclarationType.CONST))
            {
                if (node.ValueNode == null)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Value should be a constant value", context));
                }

                AreCallsBlocked = true;
                RuntimeValue value = res.Register(Visit(node.ValueNode, context));

                if (res.ShouldReturn())
                {
                    return res;
                }

                value.VariableDeclarationType = VariableDeclarationType.CONST;
                context.SymbolTable.Set(varName, value);
                AreCallsBlocked = false;
                return res.Success(value);
            }
            else if (node.DeclarationType.Equals(VariableDeclarationType.FINAL))
            {
                RuntimeValue value = new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

                if (node.ValueNode != null)
                {
                    value = res.Register(Visit(node.ValueNode, context));

                    if (res.ShouldReturn())
                    {
                        return res;
                    }
                }

                value.VariableDeclarationType = VariableDeclarationType.FINAL;
                context.SymbolTable.Set(varName, value);
                return res.Success(value);
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid variable declaration method", context));
        }

        private RuntimeResult VisitVariableAssignmentNode(VariableAssignmentNode node, Context context)
        {
            var res = new RuntimeResult();
            var varName = node.VarNameTok.Value?.ToString();
            var currentValue = context.SymbolTable.Get(varName);

            if (currentValue == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is not defined", context));
            }

            if (currentValue.VariableDeclarationType.Equals(VariableDeclarationType.CONST))
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is a constant variable and cannot be modified at runtime", context));
            }
            else if (currentValue.VariableDeclarationType.Equals(VariableDeclarationType.FINAL))
            {
                bool valid = false;

                if (currentValue.GetType() == typeof(NumberValue))
                {
                    NumberValue theValue = (NumberValue) currentValue;

                    if (theValue.Value == 0)
                    {
                        valid = true;
                    }
                }

                if (!valid)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is a final variable and cannot be modified at runtime", context));
                }
            }

            var operation = node.AssignmentToken;
            var value = res.Register(Visit(node.ValueNode, context));

            if (res.ShouldReturn())
            {
                return res;
            }

            (RuntimeValue? result, Error? error) = (null, null);

            switch (operation.Type)
            {
                case TokenType.EQ: (result, error) = (value, null); break;
                case TokenType.PLUS_EQ: (result, error) = currentValue.AddedTo(value); break;
                case TokenType.MINUS_EQ: (result, error) = currentValue.SubbedBy(value); break;
                case TokenType.MUL_EQ: (result, error) = currentValue.MultedBy(value); break;
                case TokenType.DIV_EQ: (result, error) = currentValue.DivedBy(value); break;
                case TokenType.MODULO_EQ: (result, error) = currentValue.ModuledBy(value); break;
                case TokenType.BITWISE_AND_EQ: (result, error) = currentValue.BitwiseAndedBy(value); break;
                case TokenType.BITWISE_OR_EQ: (result, error) = currentValue.BitwiseOredBy(value); break;
                case TokenType.BITWISE_LEFT_SHIFT_EQ: (result, error) = currentValue.BitwiseLeftShiftedBy(value); break;
                case TokenType.BITWISE_RIGHT_SHIFT_EQ: (result, error) = currentValue.BitwiseRightShiftedBy(value); break;
                case TokenType.POW_EQ: (result, error) = currentValue.PowedBy(value); break;
                case TokenType.AND_EQ: (result, error) = currentValue.AndedBy(value); break;
                case TokenType.OR_EQ: (result, error) = currentValue.OredBy(value); break;
            }

            if (error != null)
            {
                return res.Failure(error);
            }

            context.SymbolTable.Set(varName, result.SetDeclarationType(currentValue.VariableDeclarationType));
            return res.Success(result!.SetPos(node.PositionStart, node.PositionEnd).SetDeclarationType(currentValue.VariableDeclarationType));
        }

        private RuntimeResult VisitVariableDeleteNode(VariableDeleteNode node, Context context)
        {
            var res = new RuntimeResult();

            foreach (Token token in node.Tokens)
            {
                string varName = token.Value.ToString();
                var value = context.SymbolTable.Get(varName);

                if (value == null)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' variable does not exist", context));
                }

                context.SymbolTable.Remove(varName);
            }

            return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitBinaryOperationNode(BinaryOperationNode node, Context context)
        {
            var res = new RuntimeResult();
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
                case TokenType.KEYWORD when node.OpTok.Value?.ToString() == "and": (result, error) = left.AndedBy(right); break;
                case TokenType.KEYWORD when node.OpTok.Value?.ToString() == "or": (result, error) = left.OredBy(right); break;
                case TokenType.BITWISE_LEFT_SHIFT: (result, error) = left.BitwiseLeftShiftedBy(right); break;
                case TokenType.BITWISE_RIGHT_SHIFT: (result, error) = left.BitwiseRightShiftedBy(right); break;
                case TokenType.MODULO: (result, error) = left.ModuledBy(right); break;
                case TokenType.BITWISE_AND: (result, error) = left.BitwiseAndedBy(right); break;
                case TokenType.BITWISE_OR: (result, error) = left.BitwiseOredBy(right); break;
            }

            if (error != null) return res.Failure(error);
            return res.Success(result!.SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitUnaryOperationNode(UnaryOperationNode node, Context context)
        {
            var res = new RuntimeResult();
            var value = res.Register(Visit(node.Node, context));
            if (res.ShouldReturn()) return res;

            Error? error = null;

            if (node.OpTok.Type == TokenType.DOUBLE_PLUS || node.OpTok.Type == TokenType.DOUBLE_MINUS)
            {
                if (node.Node is not VariableAccessNode varAccessNode)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Operator ++/-- can only be applied to variables", context));
                }

                if (value is not NumberValue number)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Operator ++/-- can only be applied to numbers", context));
                }

                RuntimeValue? newValue = null;
                if (node.OpTok.Type == TokenType.DOUBLE_PLUS)
                {
                    (newValue, error) = number.AddedTo(NumberValue.True);
                }
                else
                {
                    (newValue, error) = number.SubbedBy(NumberValue.True);
                }

                if (error != null) return res.Failure(error);

                newValue = newValue!.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

                var varName = varAccessNode.VarNameTok.Value?.ToString() ?? throw new InvalidOperationException("Variable name missing");
                context.SymbolTable.Set(varName, newValue);

                if (node.IsLeft)
                {
                    return res.Success(newValue);
                }
                else
                {
                    var oldCopy = number.Copy().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                    return res.Success(oldCopy);
                }
            }

            if (node.OpTok.Type == TokenType.MINUS)
                (value, error) = value.MultedBy(new NumberValue(BigNumber.Parse("-1")));
            else if (node.OpTok.Matches(TokenType.KEYWORD, "not"))
                (value, error) = value.Notted();
            else if (node.OpTok.Type == TokenType.BITWISE_NOT)
                (value, error) = value.BitwiseNotted();

            if (error != null) return res.Failure(error);
            return res.Success(value!.SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitIfNode(IfNode node, Context context)
        {
            var res = new RuntimeResult();
            var newContext = context.Copy();

            foreach (var (condition, expr, shouldReturnNull) in node.Cases)
            {
                Context caseContext = newContext.Copy();
                var conditionValue = res.Register(Visit(condition, caseContext));

                if (res.ShouldReturn())
                {
                    context.ApplyChangesFrom(caseContext);
                    return res;
                }

                if (conditionValue.IsTrue())
                {
                    Context realCaseContext = caseContext.Copy();
                    var exprValue = res.Register(Visit(expr, realCaseContext));
                    context.ApplyChangesFrom(realCaseContext);

                    if (res.ShouldReturn())
                    {
                        return res;
                    }

                    return res.Success(shouldReturnNull ? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : exprValue);
                }
                else
                {
                    context.ApplyChangesFrom(caseContext);
                }
            }

            if (node.ElseCase != null)
            {
                Context elseCaseContext = newContext.Copy();
                var (expr, shouldReturnNull) = node.ElseCase.Value;
                var exprValue = res.Register(Visit(expr, elseCaseContext));
                context.ApplyChangesFrom(elseCaseContext);
                if (res.ShouldReturn()) return res;
                return res.Success(shouldReturnNull ? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : exprValue);
            }

            return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitForNode(ForNode node, Context context)
        {
            var res = new RuntimeResult();
            var elements = new List<RuntimeValue>();
            var initializationContext = context.Copy();
            var startValue = res.Register(Visit(node.StartValueNode, initializationContext));
            context.ApplyChangesFrom(initializationContext);

            if (res.ShouldReturn())
            {
                return res;
            }

            var endValue = res.Register(Visit(node.EndValueNode, initializationContext));
            context.ApplyChangesFrom(initializationContext);

            if (res.ShouldReturn())
            {
                return res;
            }

            RuntimeValue stepValue;

            if (node.StepValueNode != null)
            {
                stepValue = res.Register(Visit(node.StepValueNode, initializationContext));
                context.ApplyChangesFrom(initializationContext);

                if (res.ShouldReturn())
                {
                    return res;
                }
            }
            else
            {
                stepValue = new NumberValue(1);
            }

            BigNumber i = ((NumberValue)startValue).Value;
            BigNumber end = ((NumberValue)endValue).Value;
            BigNumber step = ((NumberValue)stepValue).Value;

            Func<bool> condition = (step >= 0) ? () => i < end : () => i > end;
            var newContext = initializationContext.Copy();

            while (condition())
            {
                newContext.SymbolTable.Set(node.VarNameTok.Value.ToString(), new NumberValue(i));
                i += step;
                Context actualContext = newContext.Copy();
                var value = res.Register(Visit(node.BodyNode, actualContext));
                context.ApplyChangesFrom(actualContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak)
                {
                    return res;
                }

                if (res.LoopShouldContinue)
                {
                    continue;
                }

                if (res.LoopShouldBreak)
                {
                    break;
                }

                elements.Add(value);
            }

            return res.Success(
                node.ShouldReturnNull ? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : new ListValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }

        private RuntimeResult VisitWhileNode(WhileNode node, Context context)
        {
            var res = new RuntimeResult();
            var elements = new List<RuntimeValue>();
            Context newContext = context.Copy();

            while (true)
            {
                var condition = res.Register(Visit(node.ConditionNode, newContext));

                if (res.ShouldReturn())
                {
                    return res;
                }

                if (!condition.IsTrue())
                {
                    break;
                }

                Context actualContext = newContext.Copy();
                var value = res.Register(Visit(node.BodyNode, actualContext));
                context.ApplyChangesFrom(actualContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak)
                {
                    return res;
                }

                if (res.LoopShouldContinue)
                {
                    continue;
                }

                if (res.LoopShouldBreak)
                {
                    break;
                }

                elements.Add(value);
            }

            return res.Success(
               node.ShouldReturnNull ? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : new ListValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
           );
        }

        private RuntimeResult VisitFunctionDefinitionNode(FunctionDefinitionNode node, Context context)
        {
            var res = new RuntimeResult();

            string funcName = node.VarNameTok != null ? node.VarNameTok.Value.ToString() : null;
            var argNames = node.ArgNameToks.Select(t => t.Value.ToString()).ToList();
            var funcValue = new FunctionValue(funcName, node.BodyNode, argNames, node.ShouldAutoReturn)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            if (node.VarNameTok != null)
            {
                context.SymbolTable.Set(funcName, funcValue);
            }

            return res.Success(funcValue);
        }

        private RuntimeResult VisitFunctionCallNode(FunctionCallNode node, Context context)
        {
            var res = new RuntimeResult();

            if (AreCallsBlocked)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Function calls are blocked in this context", context));
            }

            var args = new List<RuntimeValue>();

            var valueToCall = res.Register(Visit(node.NodeToCall, context));

            if (res.ShouldReturn())
            {
                return res;
            }

            valueToCall = valueToCall.Copy().SetPos(node.PositionStart, node.PositionEnd);

            foreach (var argNode in node.ArgNodes)
            {
                args.Add(res.Register(Visit(argNode, context)));

                if (res.ShouldReturn())
                {
                    return res;
                }
            }

            var returnValue = res.Register(valueToCall.Execute(args));

            if (res.ShouldReturn())
            {
                return res;
            }

            returnValue = returnValue.Copy().SetPos(node.PositionStart, node.PositionEnd).SetContext(context);
            return res.Success(returnValue);
        }

        private RuntimeResult VisitReturnNode(ReturnNode node, Context context)
        {
            var res = new RuntimeResult();
            RuntimeValue value = new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

            if (node.NodeToReturn != null)
            {
                value = res.Register(Visit(node.NodeToReturn, context));
                if (res.ShouldReturn()) return res;
            }
            return res.SuccessReturn(value);
        }

        private RuntimeResult VisitContinueNode(ContinueNode node, Context context) => new RuntimeResult().SuccessContinue();

        private RuntimeResult VisitBreakNode(BreakNode node, Context context) => new RuntimeResult().SuccessBreak();
    }
}
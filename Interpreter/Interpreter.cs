using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter
{
    public class Interpreter
    {
        private bool AreCallsBlocked { get; set; } = false;

        private readonly Func<AstNode, Context, RuntimeResult>[] _visitors;
        private List<(string, AstNode)> _labels;

        public Interpreter()
        {
            var typesCount = Enum.GetValues<AstNodeType>().Length;
            _visitors = new Func<AstNode, Context, RuntimeResult>[typesCount];
            _labels = new List<(string, AstNode)>();

            _visitors[(int)AstNodeType.Number] = (node, ctx) => VisitNumberNode((NumberNode)node, ctx);
            _visitors[(int)AstNodeType.String] = (node, ctx) => VisitStringNode((StringNode)node, ctx);
            _visitors[(int)AstNodeType.List] = (node, ctx) => VisitListNode((ListNode)node, ctx);
            _visitors[(int)AstNodeType.VariableAccess] = (node, ctx) => VisitVariableAccessNode((VariableAccessNode)node, ctx);
            _visitors[(int)AstNodeType.VariableDeclaration] = (node, ctx) => VisitVariableDeclarationNode((VariableDeclarationNode)node, ctx);
            _visitors[(int)AstNodeType.VariableAssignment] = (node, ctx) => VisitVariableAssignmentNode((VariableAssignmentNode)node, ctx);
            _visitors[(int)AstNodeType.VariableDelete] = (node, ctx) => VisitVariableDeleteNode((VariableDeleteNode)node, ctx);
            _visitors[(int)AstNodeType.BinaryOperation] = (node, ctx) => VisitBinaryOperationNode((BinaryOperationNode)node, ctx);
            _visitors[(int)AstNodeType.UnaryOperation] = (node, ctx) => VisitUnaryOperationNode((UnaryOperationNode)node, ctx);
            _visitors[(int)AstNodeType.If] = (node, ctx) => VisitIfNode((IfNode)node, ctx);
            _visitors[(int)AstNodeType.For] = (node, ctx) => VisitForNode((ForNode)node, ctx);
            _visitors[(int)AstNodeType.While] = (node, ctx) => VisitWhileNode((WhileNode)node, ctx);
            _visitors[(int)AstNodeType.FunctionDefinition] = (node, ctx) => VisitFunctionDefinitionNode((FunctionDefinitionNode)node, ctx);
            _visitors[(int)AstNodeType.FunctionCall] = (node, ctx) => VisitFunctionCallNode((FunctionCallNode)node, ctx);
            _visitors[(int)AstNodeType.Return] = (node, ctx) => VisitReturnNode((ReturnNode)node, ctx);
            _visitors[(int)AstNodeType.Continue] = (node, ctx) => VisitContinueNode((ContinueNode)node, ctx);
            _visitors[(int)AstNodeType.Break] = (node, ctx) => VisitBreakNode((BreakNode)node, ctx);
            _visitors[(int)AstNodeType.Pass] = (node, ctx) => VisitPassNode((PassNode)node, ctx);
            _visitors[(int)AstNodeType.DoWhile] = (node, ctx) => VisitDoWhileNode((DoWhileNode)node, ctx);
            _visitors[(int)AstNodeType.Typeof] = (node, ctx) => VisitTypeofNode((TypeofNode)node, ctx);
            _visitors[(int)AstNodeType.Nameof] = (node, ctx) => VisitNameofNode((NameofNode)node, ctx);
            _visitors[(int)AstNodeType.Null] = (node, ctx) => VisitNullNode((NullNode)node, ctx);
            _visitors[(int)AstNodeType.Boolean] = (node, ctx) => VisitBooleanNode((BooleanNode)node, ctx);
            _visitors[(int)AstNodeType.ListAccess] = (node, ctx) => VisitListAccessNode((ListAccessNode)node, ctx);
            _visitors[(int)AstNodeType.Set] = (node, ctx) => VisitSetNode((SetNode)node, ctx);
            _visitors[(int)AstNodeType.ListAssignment] = (node, ctx) => VisitListAssignmentNode((ListAssignmentNode)node, ctx);
            _visitors[(int)AstNodeType.ForEach] = (node, ctx) => VisitForEachNode((ForEachNode)node, ctx);
            _visitors[(int)AstNodeType.Range] = (node, ctx) => VisitRangeNode((RangeNode)node, ctx);
            _visitors[(int)AstNodeType.NullCoalescing] = (node, ctx) => VisitNullCoalescingNode((NullCoalescingNode)node, ctx);
            _visitors[(int)AstNodeType.Ternary] = (node, ctx) => VisitTernaryNode((TernaryNode)node, ctx);
            _visitors[(int)AstNodeType.Map] = (node, ctx) => VisitMapNode((MapNode)node, ctx);
            _visitors[(int)AstNodeType.Yield] = (node, ctx) => VisitYieldNode((YieldNode)node, ctx);
            _visitors[(int)AstNodeType.Switch] = (node, ctx) => VisitSwitchNode((SwitchNode)node, ctx);
            _visitors[(int)AstNodeType.Tuple] = (node, ctx) => VisitTupleNode((TupleNode)node, ctx);
            _visitors[(int)AstNodeType.Label] = (node, ctx) => VisitLabelNode((LabelNode)node, ctx);
            _visitors[(int)AstNodeType.Goto] = (node, ctx) => VisitGotoNode((GotoNode)node, ctx);
            _visitors[(int)AstNodeType.Cast] = (node, ctx) => VisitCastNode((CastNode)node, ctx);
            _visitors[(int)AstNodeType.Try] = (node, ctx) => VisitTryNode((TryNode)node, ctx);
        }

        public RuntimeResult Visit(AstNode node, Context context)
        {
            var index = (int)node.NodeType;
            if (index < 0 || index >= _visitors.Length || _visitors[index] == null)
                throw new Exception($"No visit method for {node.NodeType}");
            return _visitors[index](node, context);
        }

        private RuntimeResult VisitTryNode(TryNode node, Context context)
        {
            var res = new RuntimeResult();
            var tryRes = Visit(node.TryBody, context);

            if (tryRes.Error == null)
            {
                if (node.FinallyBody != null)
                {
                    var finallyRes = Visit(node.FinallyBody, context);
                    if (finallyRes.Error != null) return res.Failure(finallyRes.Error);
                    if (finallyRes.FuncReturnValue != null) return res.SuccessReturn(finallyRes.FuncReturnValue);
                    if (finallyRes.LoopShouldContinue) return res.SuccessContinue();
                    if (finallyRes.LoopShouldBreak) return res.SuccessBreak();
                }

                if (tryRes.FuncReturnValue != null) return res.SuccessReturn(tryRes.FuncReturnValue);
                if (tryRes.LoopShouldContinue) return res.SuccessContinue();
                if (tryRes.LoopShouldBreak) return res.SuccessBreak();

                return res.Success(tryRes.Value);
            }

            var originalError = tryRes.Error;

            if (node.CatchBody != null)
            {
                var catchCtx = context.Copy();
                string errMsg = originalError.ToString();
                var errVal = new StringValue(errMsg).SetContext(catchCtx).SetPos(node.PositionStart, node.PositionEnd);

                if (node.CatchVarTok != null)
                {
                    catchCtx.SymbolTable.Set(node.CatchVarTok.Value.ToString(), errVal);
                }

                var catchRes = Visit(node.CatchBody, catchCtx);

                if (catchRes.Error != null)
                {
                    if (node.FinallyBody != null)
                    {
                        var finallyRes2 = Visit(node.FinallyBody, context);
                        if (finallyRes2.Error != null) return res.Failure(finallyRes2.Error);
                        if (finallyRes2.FuncReturnValue != null) return res.SuccessReturn(finallyRes2.FuncReturnValue);
                        if (finallyRes2.LoopShouldContinue) return res.SuccessContinue();
                        if (finallyRes2.LoopShouldBreak) return res.SuccessBreak();
                    }
                    return res.Failure(catchRes.Error);
                }

                if (node.FinallyBody != null)
                {
                    var finallyRes3 = Visit(node.FinallyBody, context);
                    if (finallyRes3.Error != null) return res.Failure(finallyRes3.Error);
                    if (finallyRes3.FuncReturnValue != null) return res.SuccessReturn(finallyRes3.FuncReturnValue);
                    if (finallyRes3.LoopShouldContinue) return res.SuccessContinue();
                    if (finallyRes3.LoopShouldBreak) return res.SuccessBreak();
                }

                if (catchRes.FuncReturnValue != null) return res.SuccessReturn(catchRes.FuncReturnValue);
                if (catchRes.LoopShouldContinue) return res.SuccessContinue();
                if (catchRes.LoopShouldBreak) return res.SuccessBreak();

                return res.Success(catchRes.Value);
            }
            else
            {
                if (node.FinallyBody != null)
                {
                    var finallyRes4 = Visit(node.FinallyBody, context);
                    if (finallyRes4.Error != null) return res.Failure(finallyRes4.Error);
                    if (finallyRes4.FuncReturnValue != null) return res.SuccessReturn(finallyRes4.FuncReturnValue);
                    if (finallyRes4.LoopShouldContinue) return res.SuccessContinue();
                    if (finallyRes4.LoopShouldBreak) return res.SuccessBreak();
                }

                return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }
        }

        private RuntimeResult VisitCastNode(CastNode node, Context context)
        {
            var res = new RuntimeResult();
            var val = res.Register(Visit(node.Expression, context));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            if (val == null) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Cannot cast null value", context));
            (RuntimeValue? casted, Error? error) = val.CastTo(node.TargetType);
            if (error != null) return res.Failure(error);   
            if (casted == null) return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            return res.Success(casted.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitGotoNode(GotoNode node, Context context)
        {
            var res = new RuntimeResult();
            string varName = node.VarName.Value.ToString();

            for (int i = 0; i < _labels.Count; i++)
            {
                var label = _labels[i];

                if (label.Item1.Equals(varName))
                {
                    res.Register(Visit(label.Item2, context));
                    if (res.Error != null) return res;
                    return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
                }
            }

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' label is not defined", context));
        }

        private RuntimeResult VisitLabelNode(LabelNode node, Context context)
        {
            var res = new RuntimeResult();
            string varName = node.Token.Value.ToString();
            bool alreadyExists = false;
            var index = -1;

            for (int i = 0; i < _labels.Count; i++)
            {
                var label = _labels[i];

                if (label.Item1.Equals(varName))
                {
                    alreadyExists = true;
                    index = i;
                    break;
                }
            }

            if (alreadyExists) _labels.RemoveAt(index);
            _labels.Add((varName, node.Statements));
            res.Register(Visit(node.Statements, context));
            if (res.Error != null) return res;
            return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitTupleNode(TupleNode node, Context context)
        {
            var res = new RuntimeResult();
            var elements = new List<RuntimeValue>();

            foreach (var elementNode in node.ElementNodes)
            {
                var val = res.Register(Visit(elementNode, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
                elements.Add(val);
            }

            return res.Success(new TupleValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitSwitchNode(SwitchNode node, Context context)
        {
            var res = new RuntimeResult();

            var ctrlRes = Visit(node.Expression, context);
            var switchVal = res.Register(ctrlRes, propagateLoopControl: false);
            if (ctrlRes.Error != null) return res.Failure(ctrlRes.Error);
            if (ctrlRes.FuncReturnValue != null) return res.SuccessReturn(ctrlRes.FuncReturnValue);
            if (ctrlRes.LoopShouldContinue) return res.SuccessContinue();
            if (ctrlRes.LoopShouldBreak) return res.SuccessBreak();

            bool matched = false;

            for (int ci = 0; ci < node.Cases.Count; ci++)
            {
                var c = node.Cases[ci];
                bool thisCaseMatches = false;

                if (c.IsDefault)
                {
                    if (!matched) thisCaseMatches = true;
                }
                else
                {
                    foreach (var labelExpr in c.Labels)
                    {
                        var labelRes = Visit(labelExpr, context);
                        var labelVal = res.Register(labelRes, propagateLoopControl: false);
                        if (labelRes.Error != null) return res.Failure(labelRes.Error);
                        if (labelRes.FuncReturnValue != null) return res.SuccessReturn(labelRes.FuncReturnValue);
                        if (labelRes.LoopShouldContinue) return res.SuccessContinue();
                        if (labelRes.LoopShouldBreak) return res.SuccessBreak();

                        var (cmpResVal, cmpError) = switchVal.GetComparisonEq(labelVal);
                        if (cmpError != null) return res.Failure(cmpError);
                        if (cmpResVal != null && cmpResVal.IsTrue())
                        {
                            thisCaseMatches = true;
                            break;
                        }
                    }
                }

                if (!matched && !thisCaseMatches)
                {
                    continue;
                }
                if (thisCaseMatches) matched = true;

                if (c.Separator == SwitchCaseSeparator.Arrow)
                {
                    if (c.Body == null)
                        return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                    if (c.Body.NodeType == AstNodeType.List)
                    {
                        ListNode arrowBlock = (ListNode)c.Body;

                        foreach (var stmt in arrowBlock.ElementNodes)
                        {
                            var childRes = Visit(stmt, context);
                            res.Register(childRes, propagateLoopControl: false);

                            if (childRes.Error != null) return res.Failure(childRes.Error);
                            if (childRes.FuncReturnValue != null) return res.SuccessReturn(childRes.FuncReturnValue);

                            if (childRes.LoopShouldBreak)
                            {
                                childRes.LoopShouldBreak = false;
                                return res.Success(new NullValue().SetContext(context));
                            }

                            if (childRes.LoopShouldContinue)
                                return res.SuccessContinue();

                            if (childRes.ShouldYield)
                                return res.SuccessYield(childRes.YieldValue ?? new NullValue().SetContext(context));
                        }

                        return res.Success(new NullValue().SetContext(context));
                    }
                    else
                    {
                        var exprRes = Visit(c.Body, context);
                        var exprVal = res.Register(exprRes, propagateLoopControl: false);

                        if (exprRes.Error != null) return res.Failure(exprRes.Error);
                        if (exprRes.FuncReturnValue != null) return res.SuccessReturn(exprRes.FuncReturnValue);
                        if (exprRes.LoopShouldBreak) return res.SuccessBreak();
                        if (exprRes.LoopShouldContinue) return res.SuccessContinue();
                        if (exprRes.ShouldYield) return res.SuccessYield(exprRes.YieldValue ?? new NullValue().SetContext(context));

                        return res.Success(exprVal);
                    }
                }
                else
                {
                    for (int k = ci; k < node.Cases.Count; k++)
                    {
                        var caseToExec = node.Cases[k];

                        if (caseToExec.Separator == SwitchCaseSeparator.Arrow)
                        {
                            if (caseToExec.Body.NodeType == AstNodeType.List)
                            {
                                ListNode arrowBlock2 = (ListNode)caseToExec.Body;

                                foreach (var stmt in arrowBlock2.ElementNodes)
                                {
                                    var childRes = Visit(stmt, context);
                                    res.Register(childRes, propagateLoopControl: false);

                                    if (childRes.Error != null) return res.Failure(childRes.Error);
                                    if (childRes.FuncReturnValue != null) return res.SuccessReturn(childRes.FuncReturnValue);

                                    if (childRes.LoopShouldBreak)
                                    {
                                        childRes.LoopShouldBreak = false;
                                        return res.Success(new NullValue().SetContext(context));
                                    }

                                    if (childRes.LoopShouldContinue)
                                        return res.SuccessContinue();

                                    if (childRes.ShouldYield)
                                        return res.SuccessYield(childRes.YieldValue ?? new NullValue().SetContext(context));
                                }
                            }
                            else if (caseToExec.Body != null)
                            {
                                var exprRes = Visit(caseToExec.Body, context);
                                var exprVal = res.Register(exprRes, propagateLoopControl: false);

                                if (exprRes.Error != null) return res.Failure(exprRes.Error);
                                if (exprRes.FuncReturnValue != null) return res.SuccessReturn(exprRes.FuncReturnValue);
                                if (exprRes.LoopShouldBreak) return res.SuccessBreak();
                                if (exprRes.LoopShouldContinue) return res.SuccessContinue();
                                if (exprRes.ShouldYield) return res.SuccessYield(exprRes.YieldValue ?? new NullValue().SetContext(context));

                                return res.Success(exprVal);
                            }
                        }
                        else
                        {
                            if (caseToExec.Body.NodeType == AstNodeType.List)
                            {
                                ListNode colonBlock = (ListNode)caseToExec.Body;

                                foreach (var stmt in colonBlock.ElementNodes)
                                {
                                    var childRes = Visit(stmt, context);
                                    res.Register(childRes, propagateLoopControl: false);

                                    if (childRes.Error != null) return res.Failure(childRes.Error);
                                    if (childRes.FuncReturnValue != null) return res.SuccessReturn(childRes.FuncReturnValue);

                                    if (childRes.LoopShouldBreak)
                                    {
                                        childRes.LoopShouldBreak = false;
                                        return res.Success(new NullValue().SetContext(context));
                                    }

                                    if (childRes.LoopShouldContinue)
                                        return res.SuccessContinue();

                                    if (childRes.ShouldYield)
                                        return res.SuccessYield(childRes.YieldValue ?? new NullValue().SetContext(context));
                                }
                            }
                        }
                    }

                    return res.Success(new NullValue().SetContext(context));
                }
            }

            return res.Success(new NullValue().SetContext(context));
        }

        private RuntimeResult VisitYieldNode(YieldNode node, Context context)
        {
            var res = new RuntimeResult();

            var childRes = Visit(node.Expression, context);
            var val = res.Register(childRes, propagateLoopControl: false);
            if (childRes.Error != null) return res.Failure(childRes.Error);
            if (childRes.FuncReturnValue != null) return res.SuccessReturn(childRes.FuncReturnValue);
            if (childRes.LoopShouldContinue) return res.SuccessContinue();
            if (childRes.LoopShouldBreak) return res.SuccessBreak();

            return res.SuccessYield(val ?? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitMapNode(MapNode node, Context context)
        {
            var res = new RuntimeResult();
            var map = new MapValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

            foreach (var (keyNode, valueNode) in node.Pairs)
            {
                var keyVal = res.Register(Visit(keyNode, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                keyVal.SetContext(context).SetPos(keyNode.PositionStart, keyNode.PositionEnd);
                var valueVal = res.Register(Visit(valueNode, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                valueVal.SetContext(context).SetPos(valueNode.PositionStart, valueNode.PositionEnd);

                var (setResult, setError) = map.ListSet(keyVal, valueVal);
                if (setError != null) return res.Failure(setError);
            }

            return res.Success(map);
        }

        private RuntimeResult VisitTernaryNode(TernaryNode node, Context context)
        {
            var res = new RuntimeResult();

            var condVal = res.Register(Visit(node.Condition, context));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            bool condIsTrue;
            if (condVal is BooleanValue bv)
                condIsTrue = bv.Value;
            else
                condIsTrue = condVal.IsTrue();

            if (condIsTrue)
            {
                var trueVal = res.Register(Visit(node.TrueExpression, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
                return res.Success(trueVal);
            }
            else
            {
                var falseVal = res.Register(Visit(node.FalseExpression, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
                return res.Success(falseVal);
            }
        }

        private RuntimeResult VisitNullCoalescingNode(NullCoalescingNode node, Context context)
        {
            var res = new RuntimeResult();
            
            if (node.Operator.Type != TokenType.NULL_COALESCE)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Expected '??' operator", context));
            }

            var left = res.Register(Visit(node.Left, context));
            if (res.Error != null) return res;

            var right = res.Register(Visit(node.Right, context));
            if (res.Error != null) return res;

            if (left.Type == RuntimeValueType.Null)
                return res.Success(right.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

            return res.Success(left.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitRangeNode(RangeNode node, Context context)
        {
            var res = new RuntimeResult();
            var start = res.Register(Visit(node.Start, context));
            if (res.Error != null) return res;

            if (start.Type != RuntimeValueType.Number)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Start value should be a number", context));

            var end = res.Register(Visit(node.End, context));
            if (res.Error != null) return res;

            if (end.Type != RuntimeValueType.Number)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "End value should be a number", context));

            RuntimeValue? step = null;
            
            if (node.Step != null)
            {
                step = res.Register(Visit(node.Step, context));
                if (res.Error != null) return res;

                if (step.Type != RuntimeValueType.Number)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Step value should be a number", context));
            }

            NumberValue startValue = (NumberValue)start, endValue = (NumberValue)end;
            NumberValue stepValue = step != null ? (NumberValue)step : NumberValue.One;

            if (startValue.Value > endValue.Value)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Start value should not be higher than the end value", context));

            List<RuntimeValue> values = new List<RuntimeValue>();

            if (node.Operator.Type == TokenType.DOUBLE_DOT)
            {
                for (BigNumber i = startValue.Value; i < endValue.Value; i += stepValue.Value)
                    values.Add(new NumberValue(i).SetContext(context));
            }
            else if (node.Operator.Type == TokenType.DOUBLE_DOT_EQ)
            {
                for (BigNumber i = startValue.Value; i <= endValue.Value; i += stepValue.Value)
                    values.Add(new NumberValue(i).SetContext(context));
            }

            return res.Success(new ListValue(values).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitForEachNode(ForEachNode node, Context context)
        {
            var res = new RuntimeResult();
            string varName = node.VarNameToken.Value?.ToString();

            if (context.SymbolTable.Get(varName) != null)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart, node.PositionEnd,
                    $"Variable '{varName}' is already defined", context
                ));
            }

            var elements = new List<RuntimeValue>();
            var newContext = context.Copy();
            var collection = res.Register(Visit(node.CollectionNode, newContext));
            if (res.Error != null) return res;

            if (collection.Type != RuntimeValueType.List && collection.Type != RuntimeValueType.Set && collection.Type != RuntimeValueType.Map && collection.Type != RuntimeValueType.Tuple)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart, node.PositionEnd,
                    $"Must iter onto a collection", context
                ));
            }

            List<RuntimeValue> iterElements = new List<RuntimeValue>();

            if (collection.Type == RuntimeValueType.List)
            {
                iterElements = ((ListValue)collection).Elements;
            }
            else if (collection.Type == RuntimeValueType.Set)
            {
                iterElements = ((SetValue)collection).Elements.ToList();
            }
            else if (collection.Type == RuntimeValueType.Tuple)
            {
                iterElements = ((TupleValue)collection).Elements;
            }
            else if (collection.Type == RuntimeValueType.Map)
            {
                MapValue m = (MapValue)collection;

                foreach (var pair in m.Pairs)
                {
                    List<RuntimeValue> values = new List<RuntimeValue>();

                    values.Add(pair.Key);
                    values.Add(pair.Value);

                    iterElements.Add(new TupleValue(values).SetContext(context));
                }
            }

            foreach (RuntimeValue runtimeValue in iterElements)
            {
                newContext.SymbolTable.Set(varName, runtimeValue);
                Context actualContext = newContext.Copy();
                var value = res.Register(Visit(node.BodyNode, actualContext));
                if (res.Error != null) return res;
                context.ApplyChangesFrom(actualContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;
                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;

                elements.Add(value);
            }

            return res.Success(
                node.ShouldReturnNull ? new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd) : new ListValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }

        private RuntimeResult VisitListAssignmentNode(ListAssignmentNode node, Context context)
        {
            var res = new RuntimeResult();

            if (node.Target.NodeType != AstNodeType.ListAccess)
            {
                return res.Failure(new RuntimeError(
                    node.PositionStart, node.PositionEnd,
                    "Invalid assignment target. Target must be a list element.", context
                ));
            }

            ListAccessNode listAccessNode = (ListAccessNode)node.Target;
            var targetList = res.Register(Visit(listAccessNode.Target, context));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            var indexValue = res.Register(Visit(listAccessNode.Index, context));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            var valueToAssign = res.Register(Visit(node.Value, context));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            RuntimeValue finalValue = valueToAssign;

            if (node.AssignmentToken.Type != TokenType.EQ)
            {
                var accessResult = targetList.ListAccess(indexValue);
                if (accessResult.Item2 != null) return res.Failure(accessResult.Item2);

                var currentValue = accessResult.Item1!;
                (RuntimeValue? result, Error? error) = (null, null);

                switch (node.AssignmentToken.Type)
                {
                    case TokenType.PLUS_EQ: (result, error) = currentValue.AddedTo(valueToAssign); break;
                    case TokenType.MINUS_EQ: (result, error) = currentValue.SubbedBy(valueToAssign); break;
                    case TokenType.MUL_EQ: (result, error) = currentValue.MultedBy(valueToAssign); break;
                    case TokenType.DIV_EQ: (result, error) = currentValue.DivedBy(valueToAssign); break;
                    case TokenType.MODULO_EQ: (result, error) = currentValue.ModuledBy(valueToAssign); break;
                    case TokenType.BITWISE_AND_EQ: (result, error) = currentValue.BitwiseAndedBy(valueToAssign); break;
                    case TokenType.BITWISE_OR_EQ: (result, error) = currentValue.BitwiseOredBy(valueToAssign); break;
                    case TokenType.BITWISE_LEFT_SHIFT_EQ: (result, error) = currentValue.BitwiseLeftShiftedBy(valueToAssign); break;
                    case TokenType.BITWISE_RIGHT_SHIFT_EQ: (result, error) = currentValue.BitwiseRightShiftedBy(valueToAssign); break;
                    case TokenType.POW_EQ: (result, error) = currentValue.PowedBy(valueToAssign); break;
                    case TokenType.AND_EQ: (result, error) = currentValue.AndedBy(valueToAssign); break;
                    case TokenType.OR_EQ: (result, error) = currentValue.OredBy(valueToAssign); break;
                    case TokenType.NULL_COALESCE_EQ:
                        if (currentValue.Type == RuntimeValueType.Null) (result, error) = (valueToAssign.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                        else (result, error) = (currentValue.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                        break;
                }

                if (error != null) return res.Failure(error);
                finalValue = result!;
            }

            var (result1, error1) = targetList.ListSet(indexValue, finalValue);
            if (error1 != null) return res.Failure(error1);
            return res.Success(finalValue.SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }

        private RuntimeResult VisitSetNode(SetNode node, Context context)
        {
            var res = new RuntimeResult();
            var elements = new HashSet<RuntimeValue>();

            foreach (var elementNode in node.ElementNodes)
            {
                var val = res.Register(Visit(elementNode, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                bool exists = false;

                foreach (var value in elements)
                {
                    if (val.Equals(value))
                    {
                        exists = true;
                        break;
                    }
                }

                if (exists) continue;
                elements.Add(val);
            }

            return res.Success(
                new SetValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }

        private RuntimeResult VisitListAccessNode(ListAccessNode node, Context context)
        {
            var res = new RuntimeResult();
            var target = res.Register(Visit(node.Target, context));
            if (res.Error != null) return res;

            var index = res.Register(Visit(node.Index, context));
            if (res.Error != null) return res;

            (RuntimeValue?, Error?) result = target.ListAccess(index);
            if (result.Item2 != null) return res.Failure(result.Item2);
            return res.Success(result.Item1!.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitBooleanNode(BooleanNode node, Context context)
        {
            var res = new RuntimeResult();

            if (((Keyword)node.Token.Value) == Keyword.True)
                return res.Success(new BooleanValue(true).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            else if (((Keyword)node.Token.Value) == Keyword.False)
                return res.Success(new BooleanValue(false).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid boolean value", context));
        }

        private RuntimeResult VisitNullNode(NullNode node, Context context)
        {
            return new RuntimeResult().Success(new NullValue().SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }

        private RuntimeResult VisitNameofNode(NameofNode node, Context context)
        {
            var res = new RuntimeResult();
            string varName = node.Token.Value.ToString();
            var value = context.SymbolTable.Get(varName);

            if (value == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Variable {varName} not defined", context));

            return res.Success(new StringValue(varName).SetPos(node.PositionStart, node.PositionEnd).SetContext(context));
        }

        private RuntimeResult VisitTypeofNode(TypeofNode node, Context context)
        {
            var res = new RuntimeResult();
            var value = res.Register(Visit(node.Node, context));
            if (res.Error != null) return res;

            string type = value.Type switch
            {
                RuntimeValueType.Number => "number",
                RuntimeValueType.String => "string",
                RuntimeValueType.List => "list",
                RuntimeValueType.Function => "function",
                RuntimeValueType.Null => "null",
                RuntimeValueType.Boolean => "boolean",
                RuntimeValueType.Set => "set",
                RuntimeValueType.Map => "map",
                RuntimeValueType.Tuple => "tuple",
                _ => ""
            };

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
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                if (!firstTime && !condition.IsTrue()) break;
                else firstTime = false;

                Context iterationContext = newContext.Copy();
                var value = res.Register(Visit(node.BodyNode, iterationContext));
                if (res.Error != null) return res;
                newContext.ApplyChangesFrom(iterationContext);
                context.ApplyChangesFrom(newContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;
                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;

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
            var res = new RuntimeResult();
            var sb = new System.Text.StringBuilder();

            foreach (var part in node.Parts)
            {
                if (part.NodeType == AstNodeType.StringPart)
                {
                    sb.Append(((StringTextNode)part).Text);
                }
                else
                {
                    var val = res.Register(Visit(part, context));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;

                    if (val.Type == RuntimeValueType.String)
                        sb.Append(((StringValue) val).Value ?? "");
                    else if (val == null)
                        sb.Append("null");
                    else
                        sb.Append(val.ToString() ?? "");
                }
            }

            return res.Success(new StringValue(sb.ToString()).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
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
                    if (elementNode.NodeType == AstNodeType.Spread)
                    {
                        SpreadNode spread = (SpreadNode)elementNode;
                        var val = res.Register(Visit(spread.Expression, newContext));
                        if (res.Error != null) return res;
                        if (res.ShouldReturn()) return res;

                        if (val.Type != RuntimeValueType.List)
                        {
                            return res.Failure(new RuntimeError(
                                spread.PositionStart,
                                spread.PositionEnd,
                                "Spread target must be an iterable (e.g. list)",
                                context));
                        }

                        ListValue l = (ListValue)val;
                        elements.AddRange(l.Elements);
                    }
                    else
                    {
                        var val = res.Register(Visit(elementNode, newContext));
                        if (res.Error != null) return res;
                        if (res.ShouldReturn()) return res;
                        elements.Add(val);
                    }
                }

                context.ApplyChangesFrom(newContext);
            }
            else
            {
                foreach (var elementNode in node.ElementNodes)
                {
                    if (elementNode.NodeType == AstNodeType.Spread)
                    {
                        SpreadNode spread = (SpreadNode)elementNode;
                        var val = res.Register(Visit(spread.Expression, context));
                        if (res.Error != null) return res;
                        if (res.ShouldReturn()) return res;

                        if (val.Type != RuntimeValueType.List)
                        {
                            return res.Failure(new RuntimeError(
                                spread.PositionStart,
                                spread.PositionEnd,
                                "Spread target must be an iterable (e.g. list)",
                                context));
                        }

                        ListValue l = (ListValue)val;
                        elements.AddRange(l.Elements);
                    }
                    else
                    {
                        var val = res.Register(Visit(elementNode, context));
                        if (res.Error != null) return res;
                        if (res.ShouldReturn()) return res;
                        elements.Add(val);
                    }
                }
            }

            return res.Success(
                new ListValue(elements).SetContext(context).SetPos(node.PositionStart, node.PositionEnd)
            );
        }

        private RuntimeResult VisitVariableAccessNode(VariableAccessNode node, Context context)
        {
            var res = new RuntimeResult();

            var name = node.VarNameTok.Value?.ToString();

            if (string.IsNullOrEmpty(name))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid variable name", context));

            var entry = context.SymbolTable.GetEntry(name);
            if (entry == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{name}' is not defined", context));

            if (entry.IsMoved)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Variable '{name}' was moved", context));

            var valueToReturn = entry.Value.IsCopy ? entry.Value.Copy() : entry.Value;
            return res.Success(valueToReturn.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private (RuntimeValue? value, Error? error) ExtractVariableValueByName(string name, Position posStart, Position posEnd, Context context)
        {
            var entry = context.SymbolTable.GetEntry(name);
            if (entry == null)
                return (null, new RuntimeError(posStart, posEnd, $"'{name}' is not defined", context));

            if (entry.IsMoved)
                return (null, new RuntimeError(posStart, posEnd, $"Variable '{name}' was moved", context));

            if (entry.IsLet && !entry.Value.IsCopy)
            {
                entry.IsMoved = true;
                return (entry.Value.SetContext(context).SetPos(posStart, posEnd), null);
            }

            return (entry.Value.Copy().SetContext(context).SetPos(posStart, posEnd), null);
        }

        private RuntimeResult VisitVariableDeclarationNode(VariableDeclarationNode node, Context context)
        {
            var res = new RuntimeResult();
            var values = new List<RuntimeValue>();

            foreach ((Token, AstNode?, TypeDescriptor?) declaration in node.Declarations)
            {
                var varName = declaration.Item1.Value?.ToString();

                if (string.IsNullOrEmpty(varName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid identifier", context));

                if (context.SymbolTable.Get(varName) != null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is already defined", context));

                RuntimeValue value = new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                TypeDescriptor? declaredType = declaration.Item3;

                if (declaration.Item2 != null)
                {
                    value = res.Register(Visit(declaration.Item2, context));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) continue;
                }

                if (declaredType != null)
                {
                    if (declaredType.IsBuiltIn && declaredType.BuiltIn == BuiltInType.Any)
                    {
                        if (declaration.Item2 == null)
                            return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "'auto' requires an initializer to infer type", context));

                        var inferred = TypeDescriptor.FromRuntimeValue(value);
                        declaredType = inferred;
                    }
                    else
                    {
                        if (declaration.Item2 != null)
                        {
                            if (!TypeSystem.IsAssignable(declaredType, value))
                                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Type mismatch: cannot initialize variable '{varName}' of type '{declaredType}' with value of type '{value.Type}'", context));
                        }
                    }
                }

                bool isLetFlag = node.DeclarationType == VariableDeclarationType.LET;
                bool isStaticallyTyped = declaredType != null;

                if (node.DeclarationType == VariableDeclarationType.CONST)
                    value.VariableDeclarationType = VariableDeclarationType.CONST;
                else if (node.DeclarationType == VariableDeclarationType.FINAL)
                    value.VariableDeclarationType = VariableDeclarationType.FINAL;
                else if (node.DeclarationType == VariableDeclarationType.LET)
                    value.VariableDeclarationType = VariableDeclarationType.LET;
                else
                    value.VariableDeclarationType = VariableDeclarationType.VARIABLE;

                context.SymbolTable.Set(varName, value, isLetFlag, declaredType, isStaticallyTyped);
                values.Add(value);
            }

            return res.Success(new ListValue(values).SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitVariableAssignmentNode(VariableAssignmentNode node, Context context)
        {
            var res = new RuntimeResult();
            var varName = node.VarNameTok.Value?.ToString();

            if (string.IsNullOrEmpty(varName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid assignment target", context));

            var currentValue = context.SymbolTable.Get(varName);

            if (currentValue == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is not defined", context));

            if (currentValue.VariableDeclarationType == VariableDeclarationType.CONST)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is a constant variable and cannot be modified at runtime", context));
            else if (currentValue.VariableDeclarationType == VariableDeclarationType.FINAL && currentValue.Type != RuntimeValueType.Null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is a final variable and cannot be modified at runtime", context));

            var operation = node.AssignmentToken;

            RuntimeValue value;
            if (node.ValueNode.NodeType == AstNodeType.VariableAccess)
            {
                VariableAccessNode rhsVarAccess = (VariableAccessNode)node.ValueNode;
                string srcName = rhsVarAccess.VarNameTok.Value?.ToString() ?? "";
                var (extracted, err) = ExtractVariableValueByName(srcName, rhsVarAccess.PositionStart, rhsVarAccess.PositionEnd, context);
                if (err != null) return res.Failure(err);
                value = extracted!;
            }
            else
            {
                value = res.Register(Visit(node.ValueNode, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
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
                case TokenType.NULL_COALESCE_EQ:
                    if (currentValue.Type == RuntimeValueType.Null) (result, error) = (value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                    else (result, error) = (currentValue.SetContext(context).SetPos(node.PositionStart, node.PositionEnd), null);
                    break;
            }

            if (error != null) return res.Failure(error);
            var declType = currentValue.VariableDeclarationType;
            var entry = context.SymbolTable.GetEntry(varName);

            if (entry == null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is not defined", context));

            if (entry.IsStaticallyTyped && entry.DeclaredType != null)
            {
                if (!TypeSystem.IsAssignable(entry.DeclaredType, result!))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Type mismatch: cannot assign value of type '{result.Type.ToString().ToLower()}' to variable '{varName}' of type '{entry.DeclaredType}'", context));
            }

            var declType2 = entry.Value?.VariableDeclarationType ?? VariableDeclarationType.VARIABLE;
            context.SymbolTable.Set(varName, result!.SetDeclarationType(declType2));
            return res.Success(result!.SetPos(node.PositionStart, node.PositionEnd).SetDeclarationType(declType2));
        }

        private RuntimeResult VisitVariableDeleteNode(VariableDeleteNode node, Context context)
        {
            var res = new RuntimeResult();

            foreach (Token token in node.Tokens)
            {
                string varName = token.Value.ToString();
                var value = context.SymbolTable.Get(varName);
                if (value == null) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' variable does not exist", context));
                context.SymbolTable.Remove(varName);
            }

            return res.Success(new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitBinaryOperationNode(BinaryOperationNode node, Context context)
        {
            var res = new RuntimeResult();
            var left = res.Register(Visit(node.LeftNode, context));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            var right = res.Register(Visit(node.RightNode, context));
            if (res.Error != null) return res;
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
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.And: (result, error) = left.AndedBy(right); break;
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.Or: (result, error) = left.OredBy(right); break;
                case TokenType.BITWISE_LEFT_SHIFT: (result, error) = left.BitwiseLeftShiftedBy(right); break;
                case TokenType.BITWISE_RIGHT_SHIFT: (result, error) = left.BitwiseRightShiftedBy(right); break;
                case TokenType.MODULO: (result, error) = left.ModuledBy(right); break;
                case TokenType.BITWISE_AND: (result, error) = left.BitwiseAndedBy(right); break;
                case TokenType.BITWISE_OR: (result, error) = left.BitwiseOredBy(right); break;
                case TokenType.STRICT_EE: (result, error) = left.GetComparisonStrictEq(right); break;
                case TokenType.STRICT_NE: (result, error) = left.GetComparisonStrictNe(right); break;
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.In: (result, error) = left.InCollection(right); break;
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.NotIn:
                    (result, error) = left.InCollection(right);
                    if (error != null) return res;
                    result = result?.Notted().Item1!;
                    break;
            }

            if (error != null) return res.Failure(error);
            return res.Success(result!.SetPos(node.PositionStart, node.PositionEnd));
        }

        private RuntimeResult VisitUnaryOperationNode(UnaryOperationNode node, Context context)
        {
            var res = new RuntimeResult();
            var value = res.Register(Visit(node.Node, context));
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

            Error? error = null;

            switch (node.OpTok.Type)
            {
                case TokenType.DOUBLE_PLUS:
                case TokenType.DOUBLE_MINUS:
                    if (node.Node.NodeType != AstNodeType.VariableAccess) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Operator ++/-- can only be applied to variables", context));
                    if (value.Type != RuntimeValueType.Number) return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Operator ++/-- can only be applied to numbers", context));

                    VariableAccessNode varAccessNode = (VariableAccessNode)node.Node;
                    NumberValue number = (NumberValue)value;

                    RuntimeValue? newValue = null;
                    if (node.OpTok.Type == TokenType.DOUBLE_PLUS) (newValue, error) = number.AddedTo(NumberValue.One);
                    else (newValue, error) = number.SubbedBy(NumberValue.One);

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
                case TokenType.MINUS:
                    (value, error) = value.MultedBy(new NumberValue(BigNumber.Parse("-1")));
                    break;
                case TokenType.KEYWORD when ((Keyword)node.OpTok.Value) == Keyword.Not:
                    if (node.IsLeft) (value, error) = value.Notted();
                    else (value, error) = value.Factorial();
                    break;
                case TokenType.BITWISE_NOT:
                    (value, error) = value.BitwiseNotted();
                    break;
            }

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
                if (res.Error != null) return res;

                if (res.ShouldReturn())
                {
                    context.ApplyChangesFrom(caseContext);
                    return res;
                }

                if (conditionValue.IsTrue())
                {
                    Context realCaseContext = caseContext.Copy();
                    var exprValue = res.Register(Visit(expr, realCaseContext));
                    if (res.Error != null) return res;
                    context.ApplyChangesFrom(realCaseContext);

                    if (res.ShouldReturn()) return res;
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
                if (res.Error != null) return res;
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
            if (res.Error != null) return res;
            context.ApplyChangesFrom(initializationContext);
            if (res.ShouldReturn()) return res;

            var endValue = res.Register(Visit(node.EndValueNode, initializationContext));
            if (res.Error != null) return res;
            context.ApplyChangesFrom(initializationContext);
            if (res.ShouldReturn()) return res;

            RuntimeValue stepValue;

            if (node.StepValueNode != null)
            {
                stepValue = res.Register(Visit(node.StepValueNode, initializationContext));
                if (res.Error != null) return res;
                context.ApplyChangesFrom(initializationContext);
                if (res.ShouldReturn()) return res;
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
                if (res.Error != null) return res;
                context.ApplyChangesFrom(actualContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;
                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;

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
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;
                if (!condition.IsTrue()) break;

                Context actualContext = newContext.Copy();
                var value = res.Register(Visit(node.BodyNode, actualContext));
                if (res.Error != null) return res;
                context.ApplyChangesFrom(actualContext);

                if (res.ShouldReturn() && !res.LoopShouldContinue && !res.LoopShouldBreak) return res;
                if (res.LoopShouldContinue) continue;
                if (res.LoopShouldBreak) break;
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
            var funcValue = new FunctionValue(
                funcName,
                node.BodyNode,
                argNames,
                node.ArgTypes,
                node.ParamDefaults,
                node.HasVarArgs,
                node.VarArgNameTok,
                node.VarArgType,
                node.ReturnType,
                node.ShouldAutoReturn
            )
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            if (node.VarNameTok != null) context.SymbolTable.Set(funcName, funcValue);
            return res.Success(funcValue);
        }

        private RuntimeResult VisitFunctionCallNode(FunctionCallNode node, Context context)
        {
            var res = new RuntimeResult();

            if (AreCallsBlocked)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Function calls are blocked in this context", context));
            }

            var valueToCallRes = Visit(node.NodeToCall, context);
            var valRes = valueToCallRes;
            var valueToCall = res.Register(valueToCallRes);
            if (res.ShouldReturn()) return res;

            var positionalArgs = new List<RuntimeValue>();
            var namedArgs = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);

            foreach (var argNode in node.ArgNodes)
            {
                var evaluated = res.Register(Visit(argNode.Expr, context));
                if (res.Error != null) return res;
                if (res.ShouldReturn()) return res;

                if (argNode.NameTok != null)
                {
                    string name = argNode.NameTok.Value?.ToString() ?? "";
                    if (namedArgs.ContainsKey(name))
                        return res.Failure(new RuntimeError(argNode.PositionStart, argNode.PositionEnd, $"Duplicate named argument '{name}'", context));
                    namedArgs[name] = evaluated;
                }
                else
                {
                    positionalArgs.Add(evaluated);
                }
            }

            if (valueToCall is BaseFunctionValue func)
            {
                var ret = res.Register(func.ExecuteWithNamedArgs(positionalArgs, namedArgs));
                if (res.ShouldReturn()) return res;

                var returnValue = ret.Copy().SetPos(node.PositionStart, node.PositionEnd).SetContext(context);
                return res.Success(returnValue);
            }

            {
                var returnValue = res.Register(valueToCall.Execute(positionalArgs));
                if (res.ShouldReturn()) return res;
                returnValue = returnValue.Copy().SetPos(node.PositionStart, node.PositionEnd).SetContext(context);
                return res.Success(returnValue);
            }
        }

        private RuntimeResult VisitReturnNode(ReturnNode node, Context context)
        {
            var res = new RuntimeResult();
            RuntimeValue value = new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

            if (node.NodeToReturn != null)
            {
                if (node.NodeToReturn is VariableAccessNode varAccess)
                {
                    string srcName = varAccess.VarNameTok.Value?.ToString() ?? "";
                    var (extracted, err) = ExtractVariableValueByName(srcName, varAccess.PositionStart, varAccess.PositionEnd, context);
                    if (err != null) return res.Failure(err);
                    value = extracted!;
                }
                else
                {
                    value = res.Register(Visit(node.NodeToReturn, context));
                    if (res.Error != null) return res;
                    if (res.ShouldReturn()) return res;
                }
            }

            return res.SuccessReturn(value);
        }

        private RuntimeResult VisitContinueNode(ContinueNode node, Context context) => new RuntimeResult().SuccessContinue();

        private RuntimeResult VisitBreakNode(BreakNode node, Context context) => new RuntimeResult().SuccessBreak();
    }
}
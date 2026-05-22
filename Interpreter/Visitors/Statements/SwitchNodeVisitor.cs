using RaLanguage.Interpreter.Architecture;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Statements;

namespace RaLanguage.Interpreter.Visitors.Statements
{
    public class SwitchNodeVisitor : NodeVisitor<SwitchNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(SwitchNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var ctrlRes = await interpreter.Visit(node.Expression, context);
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
                        var labelRes = await interpreter.Visit(labelExpr, context);
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
                        return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));

                    if (c.Body.NodeType == AstNodeType.List)
                    {
                        ListNode arrowBlock = (ListNode)c.Body;

                        foreach (var stmt in arrowBlock.ElementNodes)
                        {
                            var childRes = await interpreter.Visit(stmt, context);
                            res.Register(childRes, propagateLoopControl: false);

                            if (childRes.Error != null) return res.Failure(childRes.Error);
                            if (childRes.FuncReturnValue != null) return res.SuccessReturn(childRes.FuncReturnValue);

                            if (childRes.LoopShouldBreak)
                            {
                                childRes.LoopShouldBreak = false;
                                return res.Success(NullValue.Null.SetContext(context));
                            }

                            if (childRes.LoopShouldContinue)
                                return res.SuccessContinue();

                            // `yield X;` inside a switch arm sets the value of
                            // the switch expression and exits the arm. It does
                            // NOT propagate up to an enclosing fn / generator.
                            if (childRes.ShouldYield)
                            {
                                var yv = childRes.YieldValue ?? NullValue.Null.SetContext(context);
                                return res.Success(yv);
                            }
                        }

                        return res.Success(NullValue.Null.SetContext(context));
                    }
                    else
                    {
                        var exprRes = await interpreter.Visit(c.Body, context);
                        var exprVal = res.Register(exprRes, propagateLoopControl: false);

                        if (exprRes.Error != null) return res.Failure(exprRes.Error);
                        if (exprRes.FuncReturnValue != null) return res.SuccessReturn(exprRes.FuncReturnValue);
                        if (exprRes.LoopShouldBreak) return res.SuccessBreak();
                        if (exprRes.LoopShouldContinue) return res.SuccessContinue();
                        if (exprRes.ShouldYield) return res.SuccessYield(exprRes.YieldValue ?? NullValue.Null.SetContext(context));

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
                                    var childRes = await interpreter.Visit(stmt, context);
                                    res.Register(childRes, propagateLoopControl: false);

                                    if (childRes.Error != null) return res.Failure(childRes.Error);
                                    if (childRes.FuncReturnValue != null) return res.SuccessReturn(childRes.FuncReturnValue);

                                    if (childRes.LoopShouldBreak)
                                    {
                                        childRes.LoopShouldBreak = false;
                                        return res.Success(NullValue.Null.SetContext(context));
                                    }

                                    if (childRes.LoopShouldContinue)
                                        return res.SuccessContinue();

                                    if (childRes.ShouldYield)
                                        return res.SuccessYield(childRes.YieldValue ?? NullValue.Null.SetContext(context));
                                }
                            }
                            else if (caseToExec.Body != null)
                            {
                                var exprRes = await interpreter.Visit(caseToExec.Body, context);
                                var exprVal = res.Register(exprRes, propagateLoopControl: false);

                                if (exprRes.Error != null) return res.Failure(exprRes.Error);
                                if (exprRes.FuncReturnValue != null) return res.SuccessReturn(exprRes.FuncReturnValue);
                                if (exprRes.LoopShouldBreak) return res.SuccessBreak();
                                if (exprRes.LoopShouldContinue) return res.SuccessContinue();
                                if (exprRes.ShouldYield) return res.SuccessYield(exprRes.YieldValue ?? NullValue.Null.SetContext(context));

                                return res.Success(exprVal);
                            }
                        }
                        else
                        {
                            // Classic `case X: stmt; stmt; break;` builds a ScopeNode
                            // (one branch per statement), not a ListNode. Earlier
                            // versions checked for AstNodeType.List, which silently
                            // skipped every classic-case body.
                            IEnumerable<AstNode>? bodyStmts = null;
                            if (caseToExec.Body is RaLanguage.Parser.Nodes.Special.ScopeNode colonScope)
                            {
                                bodyStmts = colonScope.Nodes.Where(s => s != null);
                            }
                            else if (caseToExec.Body is ListNode colonList)
                            {
                                bodyStmts = colonList.ElementNodes;
                            }
                            else if (caseToExec.Body != null)
                            {
                                bodyStmts = new[] { caseToExec.Body };
                            }

                            if (bodyStmts != null)
                            {
                                foreach (var stmt in bodyStmts)
                                {
                                    var childRes = await interpreter.Visit(stmt, context);
                                    res.Register(childRes, propagateLoopControl: false);

                                    if (childRes.Error != null) return res.Failure(childRes.Error);
                                    if (childRes.FuncReturnValue != null) return res.SuccessReturn(childRes.FuncReturnValue);

                                    if (childRes.LoopShouldBreak)
                                    {
                                        childRes.LoopShouldBreak = false;
                                        return res.Success(NullValue.Null.SetContext(context));
                                    }

                                    if (childRes.LoopShouldContinue)
                                        return res.SuccessContinue();

                                    if (childRes.ShouldYield)
                                        return res.SuccessYield(childRes.YieldValue ?? NullValue.Null.SetContext(context));
                                }
                            }
                        }
                    }

                    return res.Success(NullValue.Null.SetContext(context));
                }
            }

            return res.Success(NullValue.Null.SetContext(context));
        }
    }
}
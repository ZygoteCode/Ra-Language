using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    public class FunctionCallNodeVisitor : NodeVisitor<FunctionCallNode>
    {
        protected sealed override RuntimeResult VisitNode(FunctionCallNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (context.AreCallsBlocked)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Function calls are blocked in this context", context));
            }

            var calleeVal = res.Register(interpreter.Visit(node.NodeToCall, context));
            if (res.ShouldReturn()) return res;
            if (calleeVal == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Attempted to call a null value", context));
            }

            var positionalArgs = new List<RuntimeValue>();
            var namedArgs = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);

            if (node.ArgNodes != null)
            {
                foreach (var argNode in node.ArgNodes)
                {
                    RuntimeValue evaluated;
                    
                    if (argNode.IsRef)
                    {
                        var refResult = CreateReferenceFromNode(argNode.Expr, context, interpreter);
                        if (refResult.Error != null)
                            return res.Failure(refResult.Error);
                        evaluated = refResult.Value!;
                    }
                    else
                    {
                        evaluated = res.Register(interpreter.Visit(argNode.Expr, context));
                        if (res.ShouldReturn()) return res;
                    }

                    if (argNode.NameTok != null)
                    {
                        string name = argNode.NameTok.Value.ToString() ?? "";
                        if (namedArgs.ContainsKey(name))
                        {
                            return res.Failure(new RuntimeError(argNode.PositionStart, argNode.PositionEnd, $"Duplicate named argument '{name}'", context));
                        }
                        namedArgs[name] = evaluated;
                    }
                    else
                    {
                        positionalArgs.Add(evaluated);
                    }
                }
            }

            if (calleeVal.Type == RuntimeValueType.BaseFunction || calleeVal.Type == RuntimeValueType.Function)
            {
                var func = (BaseFunctionValue)calleeVal;
                RuntimeValue? callResult = null;
                var fnExecRes = func.ExecuteWithNamedArgs(positionalArgs, namedArgs);
                var fnReturn = res.Register(fnExecRes);
                if (res.ShouldReturn()) return res;

                if (fnReturn == null)
                {
                    callResult = new NullValue()
                        .SetContext(context)
                        .SetPos(node.PositionStart, node.PositionEnd);
                }
                else
                {
                    callResult = fnReturn;
                }

                var outVal = callResult.Copy().SetPos(node.PositionStart, node.PositionEnd).SetContext(context);
                return res.Success(outVal);
            }

            var execRes = calleeVal.Execute(positionalArgs);
            var execReturn = res.Register(execRes);
            if (res.ShouldReturn()) return res;

            if (execReturn == null)
            {
                var nullVal = new NullValue()
                    .SetContext(context)
                    .SetPos(node.PositionStart, node.PositionEnd);
                return res.Success(nullVal);
            }

            var finalVal = execReturn.Copy().SetPos(node.PositionStart, node.PositionEnd).SetContext(context);
            return res.Success(finalVal);
        }

        private RuntimeResult CreateReferenceFromNode(AstNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (node is RaLanguage.Parser.Nodes.Variables.VariableAccessNode varAccess)
            {
                var varName = varAccess.VarNameTok.Value?.ToString();
                if (string.IsNullOrEmpty(varName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid variable name", context));

                var entry = context.SymbolTable.GetEntry(varName);
                if (entry == null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{varName}' is not defined", context));

                var refValue = new ReferenceValue(context.SymbolTable, varName)
                    .SetContext(context)
                    .SetPos(node.PositionStart, node.PositionEnd);

                return res.Success(refValue);
            }

            if (node is RaLanguage.Parser.Nodes.Structs.MemberAccessNode memberAccess)
            {
                var owner = res.Register(interpreter.Visit(memberAccess.TargetNode, context));
                if (res.ShouldReturn()) return res;

                var memberName = memberAccess.MemberTok.Value?.ToString();
                if (string.IsNullOrEmpty(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid member name", context));

                if (owner.Type == RuntimeValueType.ClassInstance)
                {
                    var instance = (RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue)owner;
                    var classType = instance.Definition;
                    
                    if (classType.HasField(memberName))
                    {
                        var refValue = new ClassFieldReferenceValue(instance, memberName)
                            .SetContext(context)
                            .SetPos(node.PositionStart, node.PositionEnd);
                        return res.Success(refValue);
                    }
                }

                if (owner.Type == RuntimeValueType.StructInstance)
                {
                    var instance = (RaLanguage.Interpreter.Values.Structs.StructInstanceValue)owner;
                    
                    if (instance.HasField(memberName))
                    {
                        var refValue = new StructFieldReferenceValue(instance, memberName)
                            .SetContext(context)
                            .SetPos(node.PositionStart, node.PositionEnd);
                        return res.Success(refValue);
                    }
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Cannot take reference of '{memberName}'", context));
            }

            if (node is RaLanguage.Parser.Nodes.Variables.ListAccessNode listAccess)
            {
                var listVal = res.Register(interpreter.Visit(listAccess.Target, context));
                if (res.ShouldReturn()) return res;

                var indexVal = res.Register(interpreter.Visit(listAccess.Index, context));
                if (res.ShouldReturn()) return res;

                if (listVal.Type == RuntimeValueType.List)
                {
                    var list = (RaLanguage.Interpreter.Values.Primitives.ListValue)listVal;
                    var index = GetIntegerFromValue(indexVal, node, context);
                    
                    if (index < 0 || index >= list.Elements.Count)
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "List index out of range", context));

                    var refValue = new ListElementReferenceValue(list, index)
                        .SetContext(context)
                        .SetPos(node.PositionStart, node.PositionEnd);
                    return res.Success(refValue);
                }

                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Can only take reference of list elements", context));
            }

            return res.Failure(new RuntimeError(
                node.PositionStart, node.PositionEnd,
                "Can only take reference of variables, fields, or list elements", context));
        }

        private int GetIntegerFromValue(RuntimeValue value, AstNode sourceNode, Context context)
        {
            if (value.Type == RuntimeValueType.Integer)
                return (int)((RaLanguage.Interpreter.Values.Primitives.IntegerValue)value).Value;
            if (value.Type == RuntimeValueType.Number)
                return (int)((RaLanguage.Interpreter.Values.Primitives.NumberValue)value).Value.ToBigInteger();
            if (value.Type == RuntimeValueType.Long)
                return (int)((RaLanguage.Interpreter.Values.Primitives.LongValue)value).Value;

            return -1;
        }
    }
}
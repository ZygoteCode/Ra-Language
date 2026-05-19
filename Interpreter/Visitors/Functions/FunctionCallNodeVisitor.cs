using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    public class FunctionCallNodeVisitor : NodeVisitor<FunctionCallNode>
    {
        protected sealed override RuntimeResult VisitNode(FunctionCallNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (context.AreCallsBlocked)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "function calls are not allowed in this context",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "calls disabled here",
                    help: "this expression runs in a context (e.g. an annotation argument) where calls are forbidden"));
            }

            var calleeVal = res.Register(interpreter.Visit(node.NodeToCall, context));
            if (res.ShouldReturn()) return res;
            if (calleeVal == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    "attempted to call a null value",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "callee is null",
                    help: "check that the variable holds a function / closure before invoking it"));
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
                            return res.Failure(new RuntimeError(argNode.PositionStart, argNode.PositionEnd,
                                $"duplicate named argument '{name}'",
                                context,
                                code: DiagnosticCode.RuntimeGeneric,
                                primaryLabel: "this name was already supplied",
                                help: "named arguments must be unique within a single call"));
                        }
                        namedArgs[name] = evaluated;
                    }
                    else
                    {
                        positionalArgs.Add(evaluated);
                    }
                }
            }

            if (calleeVal is BaseFunctionValue bfunc)
            {
                var metaKey = AnnotationInterceptors.ResolveCalleeMetadataKey(calleeVal);
                var calleeName = AnnotationInterceptors.ResolveCalleeName(calleeVal);

                if (metaKey != null)
                {
                    var beforeErr = AnnotationInterceptors.RunBefore(metaKey, calleeName, positionalArgs, context);
                    if (beforeErr != null) return res.Failure(beforeErr);
                }

                var resolvedTypeArgs = ResolveTypeArgs(node.GenericTypeArgs, context);
                RuntimeValue? callResult = null;
                var fnExecRes = bfunc.ExecuteWithNamedArgs(positionalArgs, namedArgs, resolvedTypeArgs);
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

                if (metaKey != null)
                {
                    var afterErr = AnnotationInterceptors.RunAfter(metaKey, calleeName, callResult, context);
                    if (afterErr != null) return res.Failure(afterErr);
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
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"'{varName}' is not defined",
                        context,
                        code: DiagnosticCode.RuntimeUndefinedSymbol,
                        primaryLabel: "no such symbol in scope",
                        help: $"declare '{varName}' before passing it by reference, or check the spelling"));

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

        private static List<TypeDescriptor?>? ResolveTypeArgs(List<TypeDescriptor?>? args, Context context)
        {
            if (args == null) return null;
            var result = new List<TypeDescriptor?>(args.Count);
            foreach (var a in args)
                result.Add(ResolveTypeDescriptor(a, context));
            return result;
        }

        private static TypeDescriptor? ResolveTypeDescriptor(TypeDescriptor? td, Context context)
        {
            if (td == null) return null;
            if (td.IsTypeParameter)
            {
                var v = context?.SymbolTable?.Get(td.TypeParameterName);
                if (v is GenericTypeValue gtv) return ResolveTypeDescriptor(gtv.BoundType, context);
                return td;
            }
            if (td.GenericArgs.Count == 0) return td;
            var newArgs = td.GenericArgs.Select(a => ResolveTypeDescriptor(a, context) ?? a).ToList();
            return new TypeDescriptor(td.Name, newArgs);
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
using System.Threading.Tasks;
using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Calls
{
    // Shared callable-invocation engine, factored out of FunctionCallNodeVisitor
    // so that other call sites (pipeline operator, future placeholder pipelines
    // and partial-application sites) can dispatch through the exact same
    // annotation-interceptor / generic-resolution / null-result pipeline
    // without duplicating that logic.
    //
    // Two responsibilities live here:
    //   * EvaluateArguments: turn an ArgumentNode list into the parallel
    //     (positional, named) value lists the call protocol expects.
    //   * Invoke: take an already-resolved callable plus already-evaluated args
    //     and run the call through the standard interceptor pipeline.
    //
    // Keeping these surface area public-static means new call sites cannot
    // accidentally bypass the annotation / borrow / null-handling rules.
    public static class FunctionCallExecutor
    {
        // PERF: shared empty named-args dictionary for positional-only calls.
        // The call sinks (Invoke → ExecuteWithNamedArgs / Construct /
        // PrepareExecutionContextForCall) only READ namedArgs (iterate / count);
        // none mutate the passed-in dictionary — every dictionary that IS
        // written is freshly built in EvaluatedArguments / SpawnNodeVisitor. So
        // a single never-mutated instance can stand in for the per-call
        // `new Dictionary<string,RuntimeValue>()` at every positional call site.
        public static readonly Dictionary<string, RuntimeValue> EmptyNamedArgs =
            new(System.StringComparer.Ordinal);

        public readonly struct EvaluatedArguments
        {
            public readonly RuntimeResult Result;
            public readonly List<RuntimeValue> Positional;
            public readonly Dictionary<string, RuntimeValue> Named;

            public EvaluatedArguments(RuntimeResult r, List<RuntimeValue> p, Dictionary<string, RuntimeValue> n)
            {
                Result = r; Positional = p; Named = n;
            }
        }

        // Async-friendly argument evaluator. The previous sync version
        // exposed `out` parameters which are incompatible with async return
        // shapes, so the tuple-style EvaluatedArguments struct replaces them.
        // Callers destructure: `var ea = await EvaluateArguments(...); ... ea.Positional ...`.
        public static async ValueTask<EvaluatedArguments> EvaluateArguments(
            IList<ArgumentNode>? argNodes,
            Context context,
            IInterpreter interpreter)
        {
            var positionalArgs = new List<RuntimeValue>(argNodes?.Count ?? 0);
            var namedArgs = new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal);

            var res = new RuntimeResult();
            if (argNodes == null) return new EvaluatedArguments(res, positionalArgs, namedArgs);

            foreach (var argNode in argNodes)
            {
                RuntimeValue evaluated;

                if (argNode.IsRef)
                {
                    var refRes = await CreateReferenceFromNode(argNode.Expr, context, interpreter);
                    if (refRes.Error != null) return new EvaluatedArguments(res.Failure(refRes.Error), positionalArgs, namedArgs);
                    evaluated = refRes.Value!;
                }
                else
                {
                    evaluated = res.Register(await IrExpressionEvaluator.Evaluate(argNode.Expr, context, interpreter))!;
                    if (res.ShouldReturn()) return new EvaluatedArguments(res, positionalArgs, namedArgs);
                }

                if (argNode.NameTok != null)
                {
                    string name = argNode.NameTok.Value.ToString() ?? "";
                    if (namedArgs.ContainsKey(name))
                    {
                        return new EvaluatedArguments(res.Failure(new RuntimeError(argNode.PositionStart, argNode.PositionEnd,
                            $"duplicate named argument '{name}'",
                            context,
                            code: DiagnosticCode.RuntimeGeneric,
                            primaryLabel: "this name was already supplied",
                            help: "named arguments must be unique within a single call")), positionalArgs, namedArgs);
                    }
                    namedArgs[name] = evaluated;
                }
                else
                {
                    positionalArgs.Add(evaluated);
                }
            }

            return new EvaluatedArguments(res, positionalArgs, namedArgs);
        }

        // Async version. Invoke is on the call hot path: anything inside the
        // body (including a user `await x`) must be able to suspend without
        // the host worker being pinned. Keep this method async ValueTask so
        // the suspension propagates.
        public static async ValueTask<RuntimeResult> Invoke(
            RuntimeValue calleeVal,
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? genericTypeArgs,
            Position posStart,
            Position posEnd,
            Context context)
        {
            var res = new RuntimeResult();

            if (calleeVal == null)
            {
                return res.Failure(new RuntimeError(posStart, posEnd,
                    "attempted to call a null value",
                    context,
                    code: DiagnosticCode.RuntimeGeneric,
                    primaryLabel: "callee is null",
                    help: "check that the variable holds a function / closure before invoking it"));
            }

            if (calleeVal is BaseFunctionValue bfunc)
            {
                // PERF: @intercept hooks live in the metadata registry. When it
                // is empty no interceptor can exist, so skip resolving the
                // callee's metadata key / name (string work) on every call.
                bool anyAnnotations = !MetadataRegistry.Global.IsEmpty;
                var metaKey = anyAnnotations ? AnnotationInterceptors.ResolveCalleeMetadataKey(calleeVal) : null;
                var calleeName = anyAnnotations ? AnnotationInterceptors.ResolveCalleeName(calleeVal) : string.Empty;

                if (metaKey != null)
                {
                    var beforeErr = AnnotationInterceptors.RunBefore(metaKey, calleeName, positionalArgs, context);
                    if (beforeErr != null) return res.Failure(beforeErr);
                }

                var resolvedTypeArgs = ResolveTypeArgs(genericTypeArgs, context);
                RuntimeResult fnExecRes;
                // Unnamed construction `T(args)`: route through Construct with
                // the live CALL-SITE context, so private-constructor visibility
                // is judged where the call happens — not at the class's
                // definition site (which is all ExecuteWithNamedArgs could see).
                if (bfunc is ClassTypeValue ctv)
                    fnExecRes = await ctv.Construct(positionalArgs, namedArgs, resolvedTypeArgs, null, context, posStart, posEnd);
                else
                    fnExecRes = await bfunc.ExecuteWithNamedArgs(positionalArgs, namedArgs, resolvedTypeArgs);
                var fnReturn = res.Register(fnExecRes);
                if (res.ShouldReturn()) return res;

                RuntimeValue callResult;
                if (fnReturn == null)
                {
                    callResult = NullValue.Null
                        .SetContext(context)
                        .SetPos(posStart, posEnd);
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

                var outVal = callResult.Aliased().SetPos(posStart, posEnd).SetContext(context);
                return res.Success(outVal);
            }

            var execRes = await calleeVal.Execute(positionalArgs);
            var execReturn = res.Register(execRes);
            if (res.ShouldReturn()) return res;

            if (execReturn == null)
            {
                var nullVal = NullValue.Null
                    .SetContext(context)
                    .SetPos(posStart, posEnd);
                return res.Success(nullVal);
            }

            var finalVal = execReturn.Aliased().SetPos(posStart, posEnd).SetContext(context);
            return res.Success(finalVal);
        }

        public static async ValueTask<RuntimeResult> CreateReferenceFromNode(AstNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (node is VariableAccessNode varAccess)
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

            if (node is MemberAccessNode memberAccess)
            {
                var owner = res.Register(await IrExpressionEvaluator.Evaluate(memberAccess.TargetNode, context, interpreter));
                if (res.ShouldReturn()) return res;

                var memberName = memberAccess.MemberTok.Value?.ToString();
                if (string.IsNullOrEmpty(memberName))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid member name", context));

                if (owner!.Type == RuntimeValueType.ClassInstance)
                {
                    var instance = (Values.Primitives.ClassInstanceValue)owner;
                    var classType = instance.Definition;

                    if (classType.HasField(memberName))
                    {
                        var refValue = new ClassFieldReferenceValue(instance, memberName)
                            .SetContext(context)
                            .SetPos(node.PositionStart, node.PositionEnd);
                        return res.Success(refValue);
                    }
                }

                if (owner.Type == RuntimeValueType.StructInstance || owner.Type == RuntimeValueType.RecordInstance)
                {
                    var instance = (Values.Structs.StructInstanceValue)owner;

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

            if (node is ListAccessNode listAccess)
            {
                var listVal = res.Register(await IrExpressionEvaluator.Evaluate(listAccess.Target, context, interpreter));
                if (res.ShouldReturn()) return res;

                var indexVal = res.Register(await IrExpressionEvaluator.Evaluate(listAccess.Index, context, interpreter));
                if (res.ShouldReturn()) return res;

                if (listVal!.Type == RuntimeValueType.List)
                {
                    var list = (Values.Primitives.ListValue)listVal;
                    var index = GetIntegerFromValue(indexVal!);

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

        public static List<TypeDescriptor?>? ResolveTypeArgs(List<TypeDescriptor?>? args, Context context)
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
            var newArgs = new List<TypeDescriptor>(td.GenericArgs.Count);
            foreach (var a in td.GenericArgs) newArgs.Add(ResolveTypeDescriptor(a, context) ?? a);
            return new TypeDescriptor(td.Name, newArgs);
        }

        private static int GetIntegerFromValue(RuntimeValue value)
        {
            if (value.Type == RuntimeValueType.Integer)
                return (int)((Values.Primitives.IntegerValue)value).Value;
            if (value.Type == RuntimeValueType.Number)
                return (int)((Values.Primitives.NumberValue)value).Value.ToBigInteger();
            if (value.Type == RuntimeValueType.Long)
                return (int)((Values.Primitives.LongValue)value).Value;
            return -1;
        }
    }
}

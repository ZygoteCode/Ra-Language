using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Interop;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    public class FunctionDefinitionNodeVisitor : NodeVisitor<FunctionDefinitionNode>
    {
        protected sealed override async ValueTask<RuntimeResult> VisitNode(FunctionDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            string funcName = node.VarNameTok != null ? node.VarNameTok.Value.ToString() : null;
            var argNames = node.ArgNames;
            var funcValue = (FunctionValue)new FunctionValue(
                funcName,
                node.BodyNode,
                argNames,
                node.ArgTypes,
                node.IsRefParams,
                node.ParamDefaults,
                node.HasVarArgs,
                node.VarArgNameTok,
                node.VarArgType,
                node.ReturnType,
                node.ShouldAutoReturn,
                node.GenericTypeParams,
                node.WhereConstraints
            )
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            funcValue.IsAsync = node.IsAsync;
            funcValue.IsAsyncStream = node.IsAsyncStream;

            // Freeze the lexical binding context at definition time. Without
            // this, each variable-access lookup of the function would
            // SetContext(callSite) on the copy returned by ExtractVariable,
            // and GenerateNewContext would then parent the call's exec scope
            // under the call site instead of the lexical scope — leaking
            // bindings (e.g. match-arm pattern variables) into recursive
            // calls.
            funcValue.FreezeBindingContext(context);

            // Explicit closure capture list (e.g. `fn[x, &y, move z](...)`).
            // If the AST node carried one, propagate it onto the function
            // value and materialise the captures against the definition-time
            // scope. Borrow-check failures, missing names, or moved bindings
            // surface here as runtime errors instead of silent miscaptures.
            if (node.CaptureList != null)
            {
                funcValue.CaptureList = node.CaptureList;
                var capErr = funcValue.FreezeCaptures(context);
                if (capErr != null) return res.Failure(capErr);
            }

            if (node.VarNameTok != null)
            {
                context.SymbolTable.Set(funcName, funcValue, isPublic: node.IsPublic);

                if (node.HasAnnotations)
                {
                    var target = new MetadataTarget(AnnotationTargetKind.Function, null, funcName);
                    funcValue.MetadataKey = target.Key;
                    var annErr = AnnotationProcessor.Process(node.Annotations, target, context, interpreter);
                    if (annErr != null) return res.Failure(annErr);

                    var (nativeFn, dllErr) = DllImportBinder.TryBind(node, funcName, target.Key, context);
                    if (dllErr != null) return res.Failure(dllErr);
                    if (nativeFn != null)
                    {
                        context.SymbolTable.Set(funcName, nativeFn, isPublic: node.IsPublic);
                        var paramErr2 = RegisterParameterAnnotations(node, funcName, context, interpreter);
                        if (paramErr2 != null) return res.Failure(paramErr2);
                        return res.Success(nativeFn);
                    }
                }
                else
                {
                    funcValue.MetadataKey = MetadataTarget.BuildKey(AnnotationTargetKind.Function, null, funcName);
                }

                var paramErr = RegisterParameterAnnotations(node, funcName, context, interpreter);
                if (paramErr != null) return res.Failure(paramErr);
            }
            return res.Success(funcValue);
        }

        internal static RaLanguage.Errors.Error? RegisterParameterAnnotations(
            FunctionDefinitionNode node,
            string ownerKey,
            Context context,
            IInterpreter interpreter)
        {
            for (int i = 0; i < node.ArgNameToks.Count; i++)
            {
                if (i >= node.ParamAnnotations.Count) break;
                var anns = node.ParamAnnotations[i];
                if (anns == null || anns.Count == 0) continue;
                var paramName = node.ArgNameToks[i].Value?.ToString() ?? "";
                var target = new MetadataTarget(AnnotationTargetKind.Parameter, ownerKey, paramName);
                var err = AnnotationProcessor.Process(anns, target, context, interpreter);
                if (err != null) return err;
            }

            if (node.HasVarArgs && node.VarArgAnnotations != null && node.VarArgAnnotations.Count > 0)
            {
                var paramName = node.VarArgNameTok?.Value?.ToString() ?? "params";
                var target = new MetadataTarget(AnnotationTargetKind.Parameter, ownerKey, paramName);
                var err = AnnotationProcessor.Process(node.VarArgAnnotations, target, context, interpreter);
                if (err != null) return err;
            }

            return null;
        }
    }
}
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Interpreter.Visitors.Functions
{
    public class FunctionDefinitionNodeVisitor : NodeVisitor<FunctionDefinitionNode>
    {
        protected sealed override RuntimeResult VisitNode(FunctionDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            string funcName = node.VarNameTok != null ? node.VarNameTok.Value.ToString() : null;
            var argNames = node.ArgNameToks.Select(t => t.Value.ToString()).ToList();
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

            if (node.VarNameTok != null)
            {
                context.SymbolTable.Set(funcName, funcValue, isPublic: node.IsPublic);

                if (node.HasAnnotations)
                {
                    var target = new MetadataTarget(AnnotationTargetKind.Function, null, funcName);
                    funcValue.MetadataKey = target.Key;
                    var annErr = AnnotationProcessor.Process(node.Annotations, target, context, interpreter);
                    if (annErr != null) return res.Failure(annErr);
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
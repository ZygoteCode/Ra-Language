using RaLanguage.Interpreter.Runtime.Async;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public static class AnnotationInterceptors
    {
        public static IEnumerable<AnnotationInstanceValue> GetInterceptorsFor(string? metadataKey)
        {
            if (string.IsNullOrEmpty(metadataKey)) return System.Linq.Enumerable.Empty<AnnotationInstanceValue>();
            var anns = MetadataRegistry.Global.GetByKey(metadataKey);
            return anns
                .Where(a => a.Definition.HasIntercept)
                .OrderByDescending(a => a.Priority);
        }

        public static Error? RunBefore(
            string? metadataKey,
            string calleeName,
            List<RuntimeValue> args,
            Context context)
        {
            if (string.IsNullOrEmpty(metadataKey)) return null;

            foreach (var ann in GetInterceptorsFor(metadataKey))
            {
                var beforeFn = ann.Definition.InterceptBefore;
                if (string.IsNullOrEmpty(beforeFn)) continue;

                var handler = context.SymbolTable.Get(beforeFn);
                if (handler is not BaseFunctionValue bfn) continue;

                var argsList = new ListValue(new List<RuntimeValue>(args))
                    .SetContext(context)
                    .SetPos(ann.ApplicationStart, ann.ApplicationEnd);

                var execRes = SyncAwait.Get(bfn.Execute(new List<RuntimeValue>
                {
                    new StringValue(calleeName).SetContext(context).SetPos(ann.ApplicationStart, ann.ApplicationEnd),
                    argsList,
                    ann
                }));
                if (execRes.Error != null) return execRes.Error;
            }
            return null;
        }

        public static Error? RunAfter(
            string? metadataKey,
            string calleeName,
            RuntimeValue result,
            Context context)
        {
            if (string.IsNullOrEmpty(metadataKey)) return null;

            foreach (var ann in GetInterceptorsFor(metadataKey))
            {
                var afterFn = ann.Definition.InterceptAfter;
                if (string.IsNullOrEmpty(afterFn)) continue;

                var handler = context.SymbolTable.Get(afterFn);
                if (handler is not BaseFunctionValue bfn) continue;

                var execRes = SyncAwait.Get(bfn.Execute(new List<RuntimeValue>
                {
                    new StringValue(calleeName).SetContext(context).SetPos(ann.ApplicationStart, ann.ApplicationEnd),
                    result,
                    ann
                }));
                if (execRes.Error != null) return execRes.Error;
            }
            return null;
        }

        public static string? ResolveCalleeMetadataKey(RuntimeValue callee)
        {
            switch (callee)
            {
                case FunctionValue fv:
                    return fv.MetadataKey;
                case BoundClassMethodValue bcm:
                    var className = bcm.Definition?.ClassName;
                    var methodName = bcm.MethodNode?.VarNameTok?.Value?.ToString() ?? className ?? "";
                    var kind = bcm.MethodNode?.IsConstructor == true
                        ? AnnotationTargetKind.Constructor
                        : AnnotationTargetKind.Method;
                    return MetadataTarget.BuildKey(kind, className, methodName);
            }
            return null;
        }

        public static string ResolveCalleeName(RuntimeValue callee)
        {
            return callee switch
            {
                FunctionValue fv => fv.Name,
                BaseFunctionValue bfv => bfv.Name,
                _ => callee.ToString() ?? "<callee>"
            };
        }
    }
}

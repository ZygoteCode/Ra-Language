using RaLanguage.Interpreter.Runtime.Async;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public static class AnnotationProcessor
    {
        public static Error? Process(
            IReadOnlyList<AnnotationApplicationNode>? applications,
            MetadataTarget target,
            Context context,
            IInterpreter interpreter)
        {
            if (applications == null || applications.Count == 0) return null;

            var seenDefinitions = new HashSet<string>(System.StringComparer.Ordinal);

            foreach (var application in applications)
            {
                var err = ApplyOne(application, target, context, interpreter, seenDefinitions);
                if (err != null) return err;
            }

            return null;
        }

        private static Error? ApplyOne(
            AnnotationApplicationNode application,
            MetadataTarget target,
            Context context,
            IInterpreter interpreter,
            HashSet<string> seenDefinitions)
        {
            var typeSymbol = context.SymbolTable.Get(application.Name);
            if (typeSymbol is not AnnotationTypeValue typeValue)
            {
                return new RuntimeError(
                    application.PositionStart,
                    application.PositionEnd,
                    $"Annotation '@{application.Name}' is not defined",
                    context);
            }

            if (!typeValue.AcceptsTarget(target.Kind))
            {
                return new RuntimeError(
                    application.PositionStart,
                    application.PositionEnd,
                    $"Annotation '@{typeValue.AnnotationName}' is not applicable to target kind '{target.Kind}'",
                    context);
            }

            if (!typeValue.IsRepeatable && seenDefinitions.Contains(typeValue.AnnotationName))
            {
                return new RuntimeError(
                    application.PositionStart,
                    application.PositionEnd,
                    $"Annotation '@{typeValue.AnnotationName}' is not repeatable but was applied more than once on '{target.Key}'",
                    context);
            }

            seenDefinitions.Add(typeValue.AnnotationName);

            var (positional, named, bindErr) = EvaluateArgs(application, typeValue, context, interpreter);
            if (bindErr != null) return bindErr;

            if (typeValue.BuiltInValidator != null)
            {
                var validatorRes = typeValue.BuiltInValidator(positional, named, context);
                if (validatorRes.Error != null) return validatorRes.Error;
            }

            var instance = new AnnotationInstanceValue(
                typeValue,
                positional,
                named,
                application.PositionStart,
                application.PositionEnd)
            {
                Target = target,
                Priority = typeValue.Priority
            };

            var prioVal = instance.Get("priority");
            if (prioVal is NumberValue prioNum)
            {
                try { instance.Priority = (int)prioNum.Value.ToBigInteger(); } catch { }
            }
            else if (prioVal is IntegerValue prioInt)
            {
                instance.Priority = (int)prioInt.Value;
            }

            MetadataRegistry.Global.Register(target, instance);

            if (typeValue.AnnotationName == "returns"
                && (target.Kind == AnnotationTargetKind.Function
                    || target.Kind == AnnotationTargetKind.Method
                    || target.Kind == AnnotationTargetKind.Constructor))
            {
                var returnTarget = new MetadataTarget(AnnotationTargetKind.Return, target.Owner, target.Name);
                foreach (var arg in positional)
                {
                    if (arg is AnnotationInstanceValue innerInst)
                    {
                        var copy = new AnnotationInstanceValue(
                            innerInst.Definition,
                            innerInst.PositionalArgs,
                            innerInst.NamedArgs,
                            application.PositionStart,
                            application.PositionEnd)
                        {
                            Target = returnTarget,
                            Priority = innerInst.Definition.Priority
                        };
                        MetadataRegistry.Global.Register(returnTarget, copy);
                    }
                    else if (arg is AnnotationTypeValue innerType)
                    {
                        var copy = new AnnotationInstanceValue(
                            innerType,
                            new List<RuntimeValue>(),
                            new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal),
                            application.PositionStart,
                            application.PositionEnd)
                        {
                            Target = returnTarget,
                            Priority = innerType.Priority
                        };
                        MetadataRegistry.Global.Register(returnTarget, copy);
                    }
                }
            }

            foreach (var meta in typeValue.MetaAnnotations)
            {
                if (meta.DefinitionName != "composes") continue;
                foreach (var composed in meta.PositionalArgs)
                {
                    if (composed is AnnotationInstanceValue composedInst)
                    {
                        var newInst = new AnnotationInstanceValue(
                            composedInst.Definition,
                            composedInst.PositionalArgs,
                            composedInst.NamedArgs,
                            application.PositionStart,
                            application.PositionEnd)
                        {
                            Target = target,
                            Priority = composedInst.Definition.Priority
                        };

                        MetadataRegistry.Global.Register(target, newInst);
                        continue;
                    }

                    AnnotationTypeValue? composedDef = composed switch
                    {
                        AnnotationTypeValue atv => atv,
                        StringValue sv => context.SymbolTable.Get(sv.Value) as AnnotationTypeValue,
                        _ => null
                    };
                    if (composedDef == null) continue;

                    var defaultInst = new AnnotationInstanceValue(
                        composedDef,
                        new List<RuntimeValue>(),
                        new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal),
                        application.PositionStart,
                        application.PositionEnd)
                    {
                        Target = target,
                        Priority = composedDef.Priority
                    };

                    MetadataRegistry.Global.Register(target, defaultInst);
                }
            }

            return null;
        }

        public static (List<RuntimeValue> Positional, Dictionary<string, RuntimeValue> Named, Error? Error) EvaluateArgs(
            AnnotationApplicationNode application,
            AnnotationTypeValue typeValue,
            Context context,
            IInterpreter interpreter)
        {
            var positional = new List<RuntimeValue>();
            var named = new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal);

            foreach (var argNode in application.PositionalArgs)
            {
                var argRes = IrExpressionEvaluator.EvaluateBlocking(argNode, context, interpreter);
                if (argRes.Error != null) return (positional, named, argRes.Error);
                positional.Add(argRes.Value!);
            }

            foreach (var (key, valNode) in application.NamedArgs)
            {
                var keyName = key.Value?.ToString() ?? string.Empty;
                if (named.ContainsKey(keyName))
                    return (positional, named, new RuntimeError(key.PositionStart, key.PositionEnd, $"Duplicate named argument '{keyName}' in annotation '@{typeValue.AnnotationName}'", context));

                var argRes = IrExpressionEvaluator.EvaluateBlocking(valNode, context, interpreter);
                if (argRes.Error != null) return (positional, named, argRes.Error);
                named[keyName] = argRes.Value!;
            }

            int positionalCount = positional.Count;
            int paramIndex = 0;
            bool varargsReached = false;
            foreach (var param in typeValue.Parameters)
            {
                if (param.IsVarArgs)
                {
                    var remaining = new List<RuntimeValue>();
                    for (int i = paramIndex; i < positionalCount; i++) remaining.Add(positional[i]);
                    varargsReached = true;
                    break;
                }
                if (paramIndex < positionalCount)
                {
                    paramIndex++;
                    continue;
                }
                if (named.ContainsKey(param.Name)) { paramIndex++; continue; }
                if (param.DefaultValueNode != null)
                {
                    var defRes = IrExpressionEvaluator.EvaluateBlocking(param.DefaultValueNode, context, interpreter);
                    if (defRes.Error != null) return (positional, named, defRes.Error);
                    named[param.Name] = defRes.Value!;
                    paramIndex++;
                    continue;
                }
                return (positional, named, new RuntimeError(application.PositionStart, application.PositionEnd, $"Missing required argument '{param.Name}' for annotation '@{typeValue.AnnotationName}'", context));
            }

            if (!varargsReached && positionalCount > typeValue.Parameters.Count(p => !p.IsVarArgs))
            {
                return (positional, named, new RuntimeError(application.PositionStart, application.PositionEnd, $"Too many arguments for annotation '@{typeValue.AnnotationName}'", context));
            }

            foreach (var kv in named)
            {
                if (typeValue.FindParameter(kv.Key) == null)
                    return (positional, named, new RuntimeError(application.PositionStart, application.PositionEnd, $"Unknown named argument '{kv.Key}' for annotation '@{typeValue.AnnotationName}'", context));
            }

            for (int i = 0; i < positional.Count && i < typeValue.Parameters.Count; i++)
            {
                var param = typeValue.Parameters[i];
                if (param.IsVarArgs) break;
                if (param.DeclaredType != null && !TypeSystem.IsAssignable(context, param.DeclaredType, positional[i]))
                {
                    return (positional, named, new RuntimeError(application.PositionStart, application.PositionEnd, $"Argument '{param.Name}' of annotation '@{typeValue.AnnotationName}' expects type '{param.DeclaredType}', got '{positional[i].Type}'", context));
                }
            }

            foreach (var kv in named)
            {
                var param = typeValue.FindParameter(kv.Key);
                if (param?.DeclaredType != null && !TypeSystem.IsAssignable(context, param.DeclaredType, kv.Value))
                {
                    return (positional, named, new RuntimeError(application.PositionStart, application.PositionEnd, $"Named argument '{kv.Key}' of annotation '@{typeValue.AnnotationName}' expects type '{param.DeclaredType}', got '{kv.Value.Type}'", context));
                }
            }

            return (positional, named, null);
        }
    }
}

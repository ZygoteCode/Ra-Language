using System.Threading.Tasks;
using System.Collections.Generic;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Annotations
{
    public sealed class AnnotationDefinitionNodeVisitor : NodeVisitor<AnnotationDefinitionNode>
    {
        protected override async ValueTask<RuntimeResult> VisitNode(AnnotationDefinitionNode node, Context context, IInterpreter interpreter)
            => Apply(node, context, interpreter);

        public static RuntimeResult Apply(AnnotationDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var name = node.Name;

            if (string.IsNullOrWhiteSpace(name))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid annotation name", context));

            if (context.SymbolTable.Get(name) != null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{name}' is already defined", context));

            var typeValue = new AnnotationTypeValue(name, node.IsPublic, node.Parameters);

            if (node.Annotations != null)
            {
                foreach (var meta in node.Annotations)
                {
                    var metaErr = ApplyMetaAnnotation(meta, typeValue, context, interpreter);
                    if (metaErr != null) return res.Failure(metaErr);
                }
            }

            context.SymbolTable.Set(name, typeValue, isLet: true, declaredType: null, isStaticallyTyped: false, isPublic: node.IsPublic);

            var selfTarget = new MetadataTarget(AnnotationTargetKind.Annotation, null, name);
            var inheritErr = AnnotationProcessor.Process(node.Annotations, selfTarget, context, interpreter);
            if (inheritErr != null) return res.Failure(inheritErr);

            return res.Success(typeValue);
        }

        private static RaLanguage.Errors.Error? ApplyMetaAnnotation(
            AnnotationApplicationNode app,
            AnnotationTypeValue typeValue,
            Context context,
            IInterpreter interpreter)
        {
            var metaSymbol = context.SymbolTable.Get(app.Name);
            if (metaSymbol is not AnnotationTypeValue metaType)
            {
                return new RuntimeError(app.PositionStart, app.PositionEnd, $"Meta-annotation '@{app.Name}' is not defined", context);
            }

            if (!metaType.AcceptsTarget(AnnotationTargetKind.Annotation))
            {
                return new RuntimeError(app.PositionStart, app.PositionEnd, $"Annotation '@{metaType.AnnotationName}' cannot be applied to annotation definitions", context);
            }

            var (positional, named, evalErr) = AnnotationProcessor.EvaluateArgs(app, metaType, context, interpreter);
            if (evalErr != null) return evalErr;

            switch (metaType.AnnotationName)
            {
                case BuiltInAnnotations.Target:
                {
                    typeValue.AllowedTargets ??= new HashSet<AnnotationTargetKind>();
                    foreach (var arg in positional)
                    {
                        var kindName = ExtractStringOrName(arg);
                        if (kindName == null)
                            return new RuntimeError(app.PositionStart, app.PositionEnd, "@target arguments must be strings or identifiers naming target kinds", context);

                        var kind = MetadataTarget.FromName(kindName);
                        if (kind == null)
                            return new RuntimeError(app.PositionStart, app.PositionEnd, $"Unknown annotation target kind '{kindName}'", context);

                        typeValue.AllowedTargets.Add(kind.Value);
                    }
                    break;
                }
                case BuiltInAnnotations.Repeatable:
                    typeValue.IsRepeatable = true;
                    break;
                case BuiltInAnnotations.Inherited:
                    typeValue.IsInherited = true;
                    break;
                case BuiltInAnnotations.Sealed:
                    typeValue.IsSealed = true;
                    break;
                case BuiltInAnnotations.Priority:
                {
                    if (positional.Count == 0 && !named.TryGetValue("value", out _))
                        return new RuntimeError(app.PositionStart, app.PositionEnd, "@priority requires a value", context);
                    var prioVal = positional.Count > 0 ? positional[0] : named["value"];
                    int p = 0;
                    if (prioVal is NumberValue nv) { try { p = (int)nv.Value.ToBigInteger(); } catch { } }
                    else if (prioVal is IntegerValue iv) { p = (int)iv.Value; }
                    else return new RuntimeError(app.PositionStart, app.PositionEnd, "@priority value must be an integer", context);
                    typeValue.Priority = p;
                    break;
                }
                case BuiltInAnnotations.Intercept:
                {
                    string? before = null, after = null;
                    if (named.TryGetValue("before", out var b) && b is StringValue bs) before = bs.Value;
                    if (named.TryGetValue("after", out var a) && a is StringValue assv) after = assv.Value;
                    if (positional.Count >= 1 && positional[0] is StringValue ps1) before ??= ps1.Value;
                    if (positional.Count >= 2 && positional[1] is StringValue ps2) after ??= ps2.Value;
                    typeValue.InterceptBefore = before;
                    typeValue.InterceptAfter = after;
                    break;
                }
                case BuiltInAnnotations.Composes:
                {
                    var dummy = new AnnotationInstanceValue(metaType, positional, named, app.PositionStart, app.PositionEnd);
                    typeValue.MetaAnnotations.Add(dummy);
                    return null;
                }
                case BuiltInAnnotations.Validator:
                {
                    var checkVal = named.TryGetValue("check", out var c1) ? c1 : (positional.Count > 0 ? positional[0] : null);
                    if (checkVal is StringValue checkStr) typeValue.ValidatorFunctionName = checkStr.Value;
                    else return new RuntimeError(app.PositionStart, app.PositionEnd, "@validator requires 'check' to be a string (validator function name)", context);

                    var msgVal = named.TryGetValue("message", out var m1) ? m1 : (positional.Count > 1 ? positional[1] : null);
                    if (msgVal is StringValue msgStr) typeValue.ValidatorMessageTemplate = msgStr.Value;
                    break;
                }
                case BuiltInAnnotations.Deferred:
                {
                    typeValue.IsDeferred = true;
                    break;
                }
                case BuiltInAnnotations.Coerce:
                {
                    var stratVal = named.TryGetValue("strategy", out var s1) ? s1 : (positional.Count > 0 ? positional[0] : null);
                    var fnVal = named.TryGetValue("handler", out var w1) ? w1 : (positional.Count > 1 ? positional[1] : null);
                    if (stratVal is StringValue ss) typeValue.CoercerStrategy = ss.Value;
                    if (fnVal is StringValue fs) typeValue.CoercerFunctionName = fs.Value;
                    if (typeValue.CoercerStrategy == null && typeValue.CoercerFunctionName == null)
                        return new RuntimeError(app.PositionStart, app.PositionEnd, "@coerce as meta-annotation requires either 'strategy' or 'handler'", context);
                    break;
                }
            }

            var metaInstance = new AnnotationInstanceValue(metaType, positional, named, app.PositionStart, app.PositionEnd);
            typeValue.MetaAnnotations.Add(metaInstance);
            return null;
        }

        private static string? ExtractStringOrName(RuntimeValue value)
        {
            if (value is StringValue sv) return sv.Value;
            return null;
        }
    }
}

using RaLanguage.Interpreter.Runtime.Async;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public sealed class DeferredValidation
    {
        public AnnotationInstanceValue Annotation { get; }
        public RuntimeValue Value { get; }
        public string SubjectLabel { get; }
        public Context Context { get; }

        public DeferredValidation(AnnotationInstanceValue ann, RuntimeValue value, string subject, Context ctx)
        {
            Annotation = ann;
            Value = value;
            SubjectLabel = subject;
            Context = ctx;
        }
    }

    public static class AnnotationValidator
    {
        private static readonly List<DeferredValidation> _deferredQueue = new();

        public static IReadOnlyList<DeferredValidation> DeferredQueueSnapshot => _deferredQueue;
        public static int DeferredCount => _deferredQueue.Count;
        public static void ClearDeferred() => _deferredQueue.Clear();

        public static List<Error> DrainAndRunDeferred()
        {
            var errs = new List<Error>();
            var queue = new List<DeferredValidation>(_deferredQueue);
            _deferredQueue.Clear();
            foreach (var d in queue)
            {
                var defCopy = d.Annotation.Definition;
                var wasDeferred = defCopy.IsDeferred;
                defCopy.IsDeferred = false;
                try
                {
                    var err = Validate(d.Annotation, d.Value, d.SubjectLabel, d.Context);
                    if (err != null) errs.Add(err);
                }
                finally
                {
                    defCopy.IsDeferred = wasDeferred;
                }
            }
            return errs;
        }

        public static Error? Validate(
            AnnotationInstanceValue annotation,
            RuntimeValue value,
            string subjectLabel,
            Context context)
        {
            var def = annotation.Definition;
            if (!def.HasValueValidation) return null;

            if (def.IsDeferred)
            {
                _deferredQueue.Add(new DeferredValidation(annotation, value, subjectLabel, context));
                return null;
            }

            if (def.BuiltInValueValidator != null)
            {
                var (ok, customMsg) = def.BuiltInValueValidator(annotation, value, context);
                if (ok) return null;
                return BuildError(annotation, value, subjectLabel, customMsg, context);
            }

            if (def.ValidatorFunctionName != null)
            {
                var fnSymbol = context.SymbolTable.Get(def.ValidatorFunctionName);
                if (fnSymbol is not BaseFunctionValue bfn)
                {
                    return new RuntimeError(
                        annotation.ApplicationStart,
                        annotation.ApplicationEnd,
                        $"Validator function '{def.ValidatorFunctionName}' (declared by @{def.AnnotationName}) is not defined",
                        context);
                }

                var argsList = new List<RuntimeValue> { value, annotation };
                var execRes = SyncAwait.Get(bfn.Execute(argsList));
                if (execRes.Error != null) return execRes.Error;

                bool ok = execRes.Value switch
                {
                    BooleanValue bv => bv.Value,
                    NumberValue nv => !nv.Value.IsZero(),
                    IntegerValue iv => iv.Value != 0,
                    NullValue => false,
                    _ => true
                };

                string? msg = null;
                if (execRes.Value is StringValue sv)
                {
                    msg = sv.Value;
                    ok = string.IsNullOrEmpty(msg);
                }

                if (ok) return null;
                return BuildError(annotation, value, subjectLabel, msg, context);
            }

            return null;
        }

        public static Error? ValidateTarget(
            string targetKey,
            RuntimeValue value,
            string subjectLabel,
            Context context)
        {
            var resolver = MetadataKeyResolver.ForContext(context);
            foreach (var ann in MetadataRegistry.Global.GetEffective(targetKey, resolver))
            {
                if (!ann.Definition.HasValueValidation) continue;
                var err = Validate(ann, value, subjectLabel, context);
                if (err != null) return err;
            }
            return null;
        }

        public static (RuntimeValue value, Error? error) CoerceTarget(
            string targetKey,
            RuntimeValue value,
            string subjectLabel,
            Context context)
        {
            var resolver = MetadataKeyResolver.ForContext(context);
            var current = value;
            foreach (var ann in MetadataRegistry.Global.GetEffective(targetKey, resolver))
            {
                if (!ann.Definition.HasCoercion) continue;
                var (newVal, err) = ApplyCoercion(ann, current, subjectLabel, context);
                if (err != null) return (current, err);
                if (newVal != null) current = newVal;
            }
            return (current, null);
        }

        public static (RuntimeValue value, Error? error) CoerceAndValidate(
            string targetKey,
            RuntimeValue value,
            string subjectLabel,
            Context context)
        {
            var (coerced, cerr) = CoerceTarget(targetKey, value, subjectLabel, context);
            if (cerr != null) return (value, cerr);
            var verr = ValidateTarget(targetKey, coerced, subjectLabel, context);
            return (coerced, verr);
        }

        public static (RuntimeValue? value, Error? error) CoerceWithAnnotation(
            AnnotationInstanceValue annotation,
            RuntimeValue value,
            string subjectLabel,
            Context context)
            => ApplyCoercion(annotation, value, subjectLabel, context);

        private static (RuntimeValue? value, Error? error) ApplyCoercion(
            AnnotationInstanceValue annotation,
            RuntimeValue value,
            string subjectLabel,
            Context context)
        {
            var def = annotation.Definition;

            if (def.BuiltInCoercer != null)
            {
                var (newVal, msg) = def.BuiltInCoercer(annotation, value, context);
                if (msg != null)
                    return (null, new RuntimeError(annotation.ApplicationStart, annotation.ApplicationEnd, $"@{def.AnnotationName} coercion failed on {subjectLabel}: {msg}", context));
                return (newVal, null);
            }

            if (def.CoercerStrategy != null)
            {
                var (newVal, msg) = CoercerRegistry.Apply(def.CoercerStrategy, value, context);
                if (msg != null)
                    return (null, new RuntimeError(annotation.ApplicationStart, annotation.ApplicationEnd, $"@{def.AnnotationName}(strategy={def.CoercerStrategy}) failed on {subjectLabel}: {msg}", context));
                return (newVal, null);
            }

            if (def.CoercerFunctionName != null)
            {
                var fnSymbol = context.SymbolTable.Get(def.CoercerFunctionName);
                if (fnSymbol is not BaseFunctionValue bfn)
                    return (null, new RuntimeError(annotation.ApplicationStart, annotation.ApplicationEnd, $"Coercer function '{def.CoercerFunctionName}' not defined", context));
                var execRes = SyncAwait.Get(bfn.Execute(new List<RuntimeValue> { value, annotation }));
                if (execRes.Error != null) return (null, execRes.Error);
                return (execRes.Value, null);
            }

            return (null, null);
        }

        private static RuntimeError BuildError(
            AnnotationInstanceValue annotation,
            RuntimeValue value,
            string subjectLabel,
            string? customMsg,
            Context context)
        {
            var template = customMsg
                ?? annotation.Definition.ValidatorMessageTemplate
                ?? $"validation '@{annotation.Definition.AnnotationName}' failed on {{subject}}: got {{value}}";

            var rendered = RenderTemplate(template, annotation, value, subjectLabel);
            return new RuntimeError(
                annotation.ApplicationStart,
                annotation.ApplicationEnd,
                rendered,
                context);
        }

        private static string RenderTemplate(
            string template,
            AnnotationInstanceValue ann,
            RuntimeValue value,
            string subjectLabel)
        {
            var sb = new StringBuilder(template.Length + 32);
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c == '{' && i + 1 < template.Length)
                {
                    int end = template.IndexOf('}', i + 1);
                    if (end > i)
                    {
                        var key = template.Substring(i + 1, end - i - 1);
                        sb.Append(LookupKey(key, ann, value, subjectLabel));
                        i = end + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static string LookupKey(
            string key,
            AnnotationInstanceValue ann,
            RuntimeValue value,
            string subjectLabel)
        {
            switch (key)
            {
                case "value": return value?.ToString() ?? "null";
                case "subject": return subjectLabel;
                case "annotation": return $"@{ann.DefinitionName}";
                default:
                    var v = ann.Get(key);
                    return v?.ToString() ?? string.Empty;
            }
        }
    }

}

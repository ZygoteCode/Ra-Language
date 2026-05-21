using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Annotations
{
    public sealed class AnnotationTypeValue : RuntimeValue
    {
        public string AnnotationName { get; }
        public bool IsPublic { get; }
        public List<AnnotationParameterNode> Parameters { get; }
        public List<AnnotationInstanceValue> MetaAnnotations { get; } = new();
        public HashSet<AnnotationTargetKind>? AllowedTargets { get; set; }
        public bool IsRepeatable { get; set; }
        public bool IsInherited { get; set; }
        public bool IsSealed { get; set; }
        public int Priority { get; set; }
        public bool IsBuiltIn { get; }
        public string? InterceptBefore { get; set; }
        public string? InterceptAfter { get; set; }
        public bool HasIntercept => InterceptBefore != null || InterceptAfter != null;
        public Func<List<RuntimeValue>, Dictionary<string, RuntimeValue>, Context, RuntimeResult>? BuiltInValidator { get; }
        public string? ValidatorFunctionName { get; set; }
        public string? ValidatorMessageTemplate { get; set; }
        public Func<AnnotationInstanceValue, RuntimeValue, Context, (bool ok, string? msg)>? BuiltInValueValidator { get; set; }
        public bool HasValueValidation => BuiltInValueValidator != null || ValidatorFunctionName != null;
        public bool IsDeferred { get; set; }
        public string? CoercerStrategy { get; set; }
        public Func<AnnotationInstanceValue, RuntimeValue, Context, (RuntimeValue? newValue, string? msg)>? BuiltInCoercer { get; set; }
        public string? CoercerFunctionName { get; set; }
        public bool HasCoercion => BuiltInCoercer != null || CoercerStrategy != null || CoercerFunctionName != null;

        public override RuntimeValueType Type => RuntimeValueType.AnnotationType;
        public override bool IsCopy => false;

        public AnnotationTypeValue(
            string name,
            bool isPublic,
            List<AnnotationParameterNode> parameters,
            bool isBuiltIn = false,
            Func<List<RuntimeValue>, Dictionary<string, RuntimeValue>, Context, RuntimeResult>? builtInValidator = null)
        {
            AnnotationName = name;
            IsPublic = isPublic;
            Parameters = parameters ?? new List<AnnotationParameterNode>();
            IsBuiltIn = isBuiltIn;
            BuiltInValidator = builtInValidator;
        }

        public bool AcceptsTarget(AnnotationTargetKind kind)
            => AllowedTargets == null || AllowedTargets.Count == 0 || AllowedTargets.Contains(kind);

        public AnnotationParameterNode? FindParameter(string name)
        {
            for (int i = 0; i < Parameters.Count; i++)
                if (string.Equals(Parameters[i].Name, name, StringComparison.Ordinal)) return Parameters[i];
            return null;
        }

        public override RuntimeValue Copy() => this;

        public override string ToString() => $"<annotation {AnnotationName}>";
    }
}

using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Functions
{
    // Symbol-table entry produced by a `delegate Name = fn(...) -> R;`
    // declaration. Carries the structural TypeDescriptor (always
    // IsFunctionType=true) plus the type-parameter envelope so generic
    // delegate aliases (`delegate Predicate<T> = fn(T) -> bool`) can be
    // instantiated at use sites the same way generic classes are.
    //
    // The value itself is non-callable: it's a type, not a function. Code
    // that says `Predicate<int>` as a type expression resolves through the
    // existing name lookup and constructs a substituted TypeDescriptor on
    // the fly. The TypeSystem.IsAssignable path treats the resulting
    // descriptor as a structural fn type (IsFunctionType=true) — the
    // nominal alias name is purely for diagnostics.
    public sealed class DelegateTypeValue : RuntimeValue
    {
        public string DelegateName { get; }
        public TypeDescriptor SignatureType { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }
        public bool IsPublic { get; }

        public override RuntimeValueType Type => RuntimeValueType.DelegateType;
        public override bool IsCopy => true;

        public DelegateTypeValue(
            string delegateName,
            TypeDescriptor signatureType,
            List<string> genericTypeParams,
            List<WhereConstraintNode> whereConstraints,
            bool isPublic)
        {
            DelegateName = delegateName;
            SignatureType = signatureType;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            IsPublic = isPublic;
        }

        public TypeDescriptor InstantiateWith(List<TypeDescriptor> typeArgs)
        {
            if (GenericTypeParams.Count == 0 || typeArgs == null || typeArgs.Count == 0)
                return SignatureType;
            if (typeArgs.Count != GenericTypeParams.Count)
                return SignatureType;
            var bindings = new Dictionary<string, TypeDescriptor>(System.StringComparer.Ordinal);
            for (int i = 0; i < GenericTypeParams.Count; i++)
                bindings[GenericTypeParams[i]] = typeArgs[i];
            return SignatureType.Substitute(bindings);
        }

        public override RuntimeValue Copy() => this;
        public override string ToString() => $"<delegate {DelegateName} = {SignatureType}>";
    }
}

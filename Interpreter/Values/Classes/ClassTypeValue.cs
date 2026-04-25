using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Classes;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ClassTypeValue : BaseFunctionValue
    {
        public string ClassName { get; }
        public bool IsPublic { get; }
        public bool IsAbstract { get; set; }
        public TypeDescriptor? BaseType { get; }
        public ClassTypeValue? BaseClass { get; set; }
        public List<TraitTypeValue> Traits { get; set; } = new List<TraitTypeValue>();
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<FunctionDefinitionNode> Methods { get; }
        public List<OperatorDefinitionNode> Operators { get; } = new();

        public override RuntimeValueType Type => RuntimeValueType.ClassType;

        public Dictionary<string, RuntimeValue> StaticFields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> StaticFieldPublicity { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TypeDescriptor?> StaticFieldTypes { get; } = new(StringComparer.Ordinal);

        public override bool IsCopy => false;
        public override RuntimeValue Copy() => this;

        public ClassTypeValue(
            string className,
            bool isPublic,
            bool isAbstract,
            TypeDescriptor? baseType,
            List<TypeDescriptor> withTraits,
            List<StructFieldDefinitionNode> fields,
            List<FunctionDefinitionNode> methods,
            List<OperatorDefinitionNode> operators
        ) : base(className)
        {
            ClassName = className;
            IsPublic = isPublic;
            IsAbstract = isAbstract;
            BaseType = baseType;
            Fields = fields;
            Methods = methods;
            Operators = operators;
        }

        public void SetStaticField(string name, RuntimeValue value, bool isPublic, TypeDescriptor? fieldType = null)
        {
            StaticFields[name] = value.IsCopy ? value.Copy() : value;
            StaticFieldPublicity[name] = isPublic;
            StaticFieldTypes[name] = fieldType;
        }

        public bool HasStaticField(string name) => StaticFields.ContainsKey(name);

        public bool IsFieldPublic(string name)
        {
            foreach (StructFieldDefinitionNode field in Fields)
            {
                if (field.NameTok.Value.ToString().Equals(name) && field.IsPublic)
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsStaticFieldPublic(string name)
            => StaticFieldPublicity.TryGetValue(name, out var p) && p;

        public TypeDescriptor? GetStaticFieldType(string name)
            => StaticFieldTypes.TryGetValue(name, out var t) ? t : null;

        public bool TryGetStaticFieldOwner(string name, out ClassTypeValue owner)
        {
            if (StaticFields.ContainsKey(name))
            {
                owner = this;
                return true;
            }

            if (BaseClass != null)
                return BaseClass.TryGetStaticFieldOwner(name, out owner);

            owner = null!;
            return false;
        }

        public bool TryGetStaticMethodOwner(string name, out ClassTypeValue owner, out FunctionDefinitionNode? method)
        {
            var local = Methods.FirstOrDefault(m =>
                m.IsStatic &&
                string.Equals(m.VarNameTok?.Value?.ToString(), name, StringComparison.Ordinal));

            if (local != null)
            {
                owner = this;
                method = local;
                return true;
            }

            if (BaseClass != null)
                return BaseClass.TryGetStaticMethodOwner(name, out owner, out method);

            owner = null!;
            method = null;
            return false;
        }

        public List<FunctionDefinitionNode> ResolveInstanceMethods(string name)
        {
            var result = Methods
                .Where(m => !m.IsStatic && !m.IsAbstract &&
                            string.Equals(m.VarNameTok?.Value?.ToString(), name, StringComparison.Ordinal))
                .ToList();

            if (result.Count > 0) return result;

            foreach (var trait in Traits)
            {
                List<TraitMethodDefinitionNode> methods = trait.GetDefaultMethodsByName(name).ToList();

                foreach (var method in methods)
                {
                    result.Add(new FunctionDefinitionNode(method.NameTok, method.ArgNameToks, method.ArgTypes, method.IsRefParams, method.ParamDefaults, method.HasVarArgs, method.VarArgNameTok, method.VarArgType, method.ReturnType, method.BodyNode, method.ShouldAutoReturn, null, true, method.IsConstructor, method.IsOverride, method.IsAbstract, false));
                }

                result.AddRange();
            }

            if (BaseClass != null)
                result.AddRange(BaseClass.ResolveInstanceMethods(name));

            return result;
        }

        public List<FunctionDefinitionNode> ResolveStaticMethods(string name)
        {
            var result = Methods
                .Where(m => m.IsStatic && !m.IsAbstract &&
                            string.Equals(m.VarNameTok?.Value?.ToString(), name, StringComparison.Ordinal))
                .ToList();

            if (result.Count > 0) return result;

            if (BaseClass != null)
                result.AddRange(BaseClass.ResolveStaticMethods(name));

            return result;
        }

        public List<ICallableMethodDefinition> ResolveCandidates(string methodName)
        {
            var result = new List<ICallableMethodDefinition>();

            result.AddRange(Methods.Where(m =>
                !m.IsAbstract &&
                string.Equals(m.VarNameTok?.Value?.ToString(), methodName, StringComparison.Ordinal)));

            foreach (var trait in Traits)
            {
                result.AddRange(trait.GetDefaultMethodsByName(methodName));
            }

            if (BaseClass != null)
            {
                result.AddRange(BaseClass.ResolveCandidates(methodName));
            }

            return result;
        }

        public IEnumerable<ICallableMethodDefinition> GetAbstractRequirementsInHierarchy()
        {
            var all = new List<ICallableMethodDefinition>();

            CollectAbstractRequirements(this, all);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var req in all)
            {
                var key = MethodSignature.KeyOf(req);
                if (seen.Add(key))
                    yield return req;
            }
        }

        private static void CollectAbstractRequirements(ClassTypeValue type, List<ICallableMethodDefinition> output)
        {
            output.AddRange(type.Methods.Where(m => m.IsAbstract));

            foreach (var trait in type.Traits)
                output.AddRange(trait.GetRequiredMethods());

            if (type.BaseClass != null)
                CollectAbstractRequirements(type.BaseClass, output);
        }

        public bool HasConcreteImplementation(ICallableMethodDefinition requirement)
        {
            return ResolveCandidates(MethodSignature.NameOf(requirement))
                .Any(c => MethodSignature.MatchesSignature(c, requirement));
        }

        public IEnumerable<ICallableMethodDefinition> GetUnresolvedAbstractRequirements()
        {
            return GetAbstractRequirementsInHierarchy()
                .Where(req => !HasConcreteImplementation(req))
                .ToList();
        }

        public List<StructFieldDefinitionNode> GetAbstractFieldsInHierarchy()
        {
            var all = new List<StructFieldDefinitionNode>();
            var result = new List<StructFieldDefinitionNode>();

            CollectAbstractFields(this, all);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in all)
            {
                if (seen.Add(field.NameTok.Value?.ToString() ?? ""))
                    result.Add(field);
            }

            return result;
        }

        private static void CollectAbstractFields(ClassTypeValue type, List<StructFieldDefinitionNode> output)
        {
            output.AddRange(type.Fields.Where(f => f.IsAbstract));

            if (type.BaseClass != null)
                CollectAbstractFields(type.BaseClass, output);
        }

        public bool HasField(string name)
        {
            return Fields.Any(f => string.Equals(f.NameTok.Value?.ToString(), name, StringComparison.Ordinal));
        }

        public bool InheritsFrom(string ancestorName)
        {
            if (string.Equals(ClassName, ancestorName, StringComparison.Ordinal))
                return true;

            var current = BaseClass;
            while (current != null)
            {
                if (string.Equals(current.ClassName, ancestorName, StringComparison.Ordinal))
                    return true;

                current = current.BaseClass;
            }

            return false;
        }

        public bool SatisfiesTrait(TraitTypeValue trait)
        {
            foreach (var required in trait.GetRequiredMethods())
            {
                if (!HasMethodSignatureInHierarchy(required))
                    return false;
            }

            foreach (var field in trait.Fields)
            {
                if (!HasFieldMatching(field))
                    return false;
            }

            return true;
        }

        public bool HasInheritedOrTraitMethodSignature(ICallableMethodDefinition method)
        {
            if (BaseClass != null && BaseClass.HasMethodSignatureInHierarchy(method))
                return true;

            foreach (var trait in Traits)
            {
                if (trait.GetDefaultMethodsByName(MethodSignature.NameOf(method))
                    .Any(m => MethodSignature.MatchesSignature(m, method)))
                    return true;
            }

            return false;
        }

        public bool HasInheritedOrTraitField(StructFieldDefinitionNode field)
        {
            var fieldName = field.NameTok.Value?.ToString() ?? "";

            if (BaseClass != null)
            {
                var baseField = BaseClass.GetField(fieldName);
                if (baseField != null)
                    return true;
            }

            foreach (var trait in Traits)
            {
                var traitField = trait.GetField(fieldName);
                if (traitField != null)
                    return true;
            }

            return false;
        }

        public bool HasMethodSignatureInHierarchy(ICallableMethodDefinition method)
        {
            foreach (var m in Methods.Where(m => string.Equals(m.VarNameTok?.Value?.ToString(), MethodSignature.NameOf(method), StringComparison.Ordinal)))
            {
                if (MethodSignature.MatchesSignature(m, method))
                    return true;
            }

            foreach (var trait in Traits)
            {
                foreach (var m in trait.GetDefaultMethodsByName(MethodSignature.NameOf(method)))
                {
                    if (MethodSignature.MatchesSignature(m, method))
                        return true;
                }
            }

            return BaseClass?.HasMethodSignatureInHierarchy(method) ?? false;
        }

        public List<ICallableMethodDefinition> ResolveBaseCandidates(string methodName)
        {
            if (BaseClass == null) return new List<ICallableMethodDefinition>();
            return BaseClass.ResolveCandidates(methodName);
        }

        public FunctionDefinitionNode? ResolveConstructor(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var ctors = Methods.Where(m => m.IsConstructor).ToList();
            foreach (var ctor in ctors)
            {
                if (MethodCallBinder.CanBind(ctor, positionalArgs, namedArgs, Context))
                    return ctor;
            }

            return null;
        }

        public StructFieldDefinitionNode? GetField(string name)
            => Fields.FirstOrDefault(f => string.Equals(f.NameTok.Value?.ToString(), name, StringComparison.Ordinal));

        public FunctionDefinitionNode? ResolveMethod(string name, List<RuntimeValue> args, Dictionary<string, RuntimeValue> namedArgs)
        {
            var local = Methods
                .Where(m => string.Equals(m.VarNameTok?.Value?.ToString(), name, StringComparison.Ordinal))
                .ToList();

            foreach (var m in local)
            {
                if (CallableBinder.CanBind(Context, m, args, namedArgs))
                    return m;
            }

            return BaseClass?.ResolveMethod(name, args, namedArgs);
        }

        public FunctionDefinitionNode? ResolveOwnConstructor(List<RuntimeValue> args, Dictionary<string, RuntimeValue> namedArgs)
        {
            var ctors = Methods.Where(m => m.IsConstructor).ToList();
            foreach (var ctor in ctors)
            {
                if (CallableBinder.CanBind(Context, ctor, args, namedArgs))
                    return ctor;
            }
            return null;
        }

        public override RuntimeResult Execute(List<RuntimeValue> args)
            => ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();

            if (IsAbstract)
            {
                return res.Failure(new RuntimeError(
                    PositionStart,
                    PositionEnd,
                    $"Cannot instantiate abstract class '{ClassName}'",
                    Context));
            }

            var instance = (ClassInstanceValue)new ClassInstanceValue(this)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            InitializeFieldChain(instance, Context, this);

            var ownCtor = ResolveOwnConstructor(positionalArgs, namedArgs);
            if (ownCtor != null)
            {
                var boundCtor = (BoundClassMethodValue)new BoundClassMethodValue(this, instance, ownCtor, false)
                    .SetContext(Context)
                    .SetPos(PositionStart, PositionEnd);

                var ctorRes = boundCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
                if (ctorRes.Error != null) return ctorRes;

                return res.Success(instance);
            }

            if (!Methods.Any(m => m.IsConstructor) && BaseClass != null)
            {
                var baseCtorMatch = BaseClass.ResolveOwnConstructor(positionalArgs, namedArgs);
                if (baseCtorMatch != null)
                {
                    var boundBaseCtor = (BoundClassMethodValue)new BoundClassMethodValue(BaseClass, instance, baseCtorMatch, false)
                        .SetContext(Context)
                        .SetPos(PositionStart, PositionEnd);

                    var baseCtorRes = boundBaseCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
                    if (baseCtorRes.Error != null) return baseCtorRes;

                    return res.Success(instance);
                }

                if (BaseClass.Methods.Any(m => m.IsConstructor))
                {
                    return res.Failure(new RuntimeError(
                        PositionStart,
                        PositionEnd,
                        $"No matching constructor found for class '{ClassName}' or its base class '{BaseClass.ClassName}'",
                        Context));
                }
            }

            return res.Success(instance);
        }

        private static void InitializeFieldChain(ClassInstanceValue instance, Context context, ClassTypeValue type)
        {
            if (type.BaseClass != null)
                InitializeFieldChain(instance, context, type.BaseClass);

            foreach (var field in type.Fields)
            {
                RuntimeValue value = new NullValue().SetContext(context).SetPos(type.PositionStart, type.PositionEnd);

                if (field.DefaultValueNode != null)
                {
                    var initRes = new Interpreter().Visit(field.DefaultValueNode, context);
                    if (initRes.Error == null && initRes.Value != null)
                        value = initRes.Value;
                }

                instance.SetField(field.NameTok.Value?.ToString() ?? "", value, field.IsPublic, field.FieldType, field.DeclarationType);
            }
        }

        public List<FunctionDefinitionNode> GetAllMethodsByName(string name)
        {
            var own = Methods.Where(m => string.Equals(m.VarNameTok?.Value?.ToString(), name, StringComparison.Ordinal));
            var baseMethods = BaseClass?.GetAllMethodsByName(name) ?? Enumerable.Empty<FunctionDefinitionNode>();
            return own.Concat(baseMethods).ToList();
        }

        public bool ImplementsInterface(InterfaceTypeValue iface)
        {
            foreach (var required in iface.Methods)
            {
                var candidates = GetAllMethodsByName(required.NameTok.Value?.ToString() ?? "");
                if (!candidates.Any(m => InterfaceCompatibility.AreCompatible(m, required)))
                    return false;
            }

            foreach (var field in iface.Fields)
            {
                if (!HasFieldMatching(field))
                    return false;
            }

            return true;
        }

        public bool HasFieldMatching(StructFieldDefinitionNode field)
        {
            var fieldName = field.NameTok.Value?.ToString() ?? "";
            
            var classField = GetField(fieldName);
            if (classField == null)
                return false;

            if (field.FieldType != null && classField.FieldType != null)
            {
                if (!string.Equals(field.FieldType.Name, classField.FieldType.Name, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        public OperatorDefinitionNode? ResolveOperator(TokenType operatorType, string parameterTypeName)
        {
            foreach (var op in Operators)
            {
                if (op.OperatorTok.Type == operatorType && 
                    op.ArgType != null && 
                    string.Equals(op.ArgType.Name, parameterTypeName, StringComparison.Ordinal))
                {
                    return op;
                }
            }

            if (BaseClass != null)
            {
                return BaseClass.ResolveOperator(operatorType, parameterTypeName);
            }

            return null;
        }

        public List<OperatorDefinitionNode> GetAllOperators()
        {
            var result = new List<OperatorDefinitionNode>(Operators);

            if (BaseClass != null)
            {
                result.AddRange(BaseClass.GetAllOperators());
            }

            return result;
        }
    }
}
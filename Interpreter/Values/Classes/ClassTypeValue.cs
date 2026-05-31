using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Classes;
using RaLanguage.Interpreter.Runtime.Events;
using RaLanguage.Interpreter.Runtime.Properties;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ClassTypeValue : BaseFunctionValue
    {
        public string ClassName { get; }

        // Cached self-type descriptor for binding `self` in instance methods.
        // `new TypeDescriptor(ClassName)` is otherwise rebuilt on every method
        // call; the descriptor is immutable and consumed read-only (stored as a
        // SymbolEntry.DeclaredType and fed to IsAssignable), so a single shared
        // instance per class is correct. Lazily built; the init race is benign
        // (both instances are structurally identical and immutable).
        private TypeDescriptor? _selfTypeDescriptor;
        public TypeDescriptor SelfTypeDescriptor => _selfTypeDescriptor ??= new TypeDescriptor(ClassName);

        public bool IsPublic { get; }
        public bool IsAbstract { get; set; }
        public TypeDescriptor? BaseType { get; }
        public ClassTypeValue? BaseClass { get; set; }
        public List<TraitTypeValue> Traits { get; set; } = new List<TraitTypeValue>();
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<FunctionDefinitionNode> Methods { get; }
        public List<OperatorDefinitionNode> Operators { get; } = new();
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        // Property descriptors declared on this class (not the
        // hierarchy). GetProperty walks the BaseClass chain.
        public List<PropertyDescriptor> Properties { get; } = new();
        public Dictionary<string, PropertyDescriptor> PropertyByName { get; } = new(StringComparer.Ordinal);

        // Event descriptors declared on this class (not the hierarchy).
        // GetEvent walks the BaseClass chain. Subscriber storage lives
        // per-instance for non-static events; static events store the
        // subscriber list directly on the type below.
        public List<EventDescriptor> Events { get; } = new();
        public Dictionary<string, EventDescriptor> EventByName { get; } = new(StringComparer.Ordinal);

        // Per-class storage for static events. Allocated lazily.
        public Dictionary<string, EventSubscriberList>? StaticEventSubs;

        public override RuntimeValueType Type => RuntimeValueType.ClassType;

        public Dictionary<string, RuntimeValue> StaticFields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> StaticFieldPublicity { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TypeDescriptor?> StaticFieldTypes { get; } = new(StringComparer.Ordinal);

        // Extension-field storage on the type itself (`static var` in
        // an `extend` block). Mirrors the per-instance ExtFieldSlots
        // shape on ClassInstanceValue but keyed off the type, so a
        // `static` ext-field is shared by every instance and survives
        // independent of any specific receiver.
        public RuntimeValue?[]? StaticExtFieldSlots;
        public ulong[]? StaticExtFieldInitBits;
        public ulong[]? StaticExtFieldLazyBits;

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
            List<OperatorDefinitionNode> operators,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null
        ) : base(className)
        {
            ClassName = className;
            IsPublic = isPublic;
            IsAbstract = isAbstract;
            BaseType = baseType;
            Fields = fields;
            Methods = methods;
            Operators = operators;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
        }

        public void SetStaticField(string name, RuntimeValue value, bool isPublic, TypeDescriptor? fieldType = null)
        {
            StaticFields[name] = value.IsCopy ? value.Copy() : value;
            StaticFieldPublicity[name] = isPublic;
            StaticFieldTypes[name] = fieldType;
        }

        public bool HasStaticField(string name) => StaticFields.ContainsKey(name);

        // Registers a property declared on this class. Invalidates the
        // shape cache so the slot allocator picks up the new backing
        // slot on the next access.
        public void AddProperty(PropertyDescriptor desc)
        {
            Properties.Add(desc);
            PropertyByName[desc.Name] = desc;
            _fieldNameToIndex = null;
            _fieldSlotCount = -1;
        }

        // Walks this class then base classes, returning the most-derived
        // descriptor for `name`. Overrides on this class shadow base
        // descriptors automatically because we hit them first.
        public PropertyDescriptor? GetProperty(string name)
        {
            if (PropertyByName.TryGetValue(name, out var d)) return d;
            return BaseClass?.GetProperty(name);
        }

        public void AddEvent(EventDescriptor desc)
        {
            Events.Add(desc);
            EventByName[desc.Name] = desc;
        }

        // Walks the class and its base classes for an event named `name`.
        // Overrides on this class shadow base descriptors.
        public EventDescriptor? GetEvent(string name)
        {
            if (EventByName.TryGetValue(name, out var d)) return d;
            return BaseClass?.GetEvent(name);
        }

        // M38: hidden class / shape information. Field set on a class
        // definition is static — fields are declared at parse time and
        // never added dynamically (Ra has no runtime property-add like
        // JavaScript), so we can pre-compute a name→slot mapping shared
        // across every instance of the class. Inherited fields stack on
        // top of the base class's mapping in declaration order so a
        // subclass keeps its parent's slot indices stable. The result is
        // O(1) field access via an integer-indexed array on
        // ClassInstanceValue, replacing the per-call
        // `Dictionary<string,RuntimeValue>.TryGetValue` walk.
        //
        // Computed lazily on first request; cached for the lifetime of the
        // ClassTypeValue. Single-threaded interpreter so no lock needed.
        private Dictionary<string, int>? _fieldNameToIndex;
        private int _fieldSlotCount = -1;

        public int FieldSlotCount
        {
            get
            {
                if (_fieldNameToIndex == null) BuildFieldShape();
                return _fieldSlotCount;
            }
        }

        // Returns the canonical slot index for the named field, or -1 if
        // the field is not declared on this class or any ancestor. The
        // index is dense (0..FieldSlotCount-1) and stable across all
        // instances of this class type.
        public int GetFieldSlotIndex(string name)
        {
            var map = _fieldNameToIndex;
            if (map == null)
            {
                BuildFieldShape();
                map = _fieldNameToIndex!;
            }
            return map.TryGetValue(name, out var idx) ? idx : -1;
        }

        private void BuildFieldShape()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            int next = 0;
            // Base-class fields first so inherited slot indices stay
            // stable when the subclass adds its own.
            if (BaseClass != null)
            {
                _ = BaseClass.FieldSlotCount; // force-build the parent shape
                if (BaseClass._fieldNameToIndex != null)
                {
                    foreach (var kv in BaseClass._fieldNameToIndex)
                    {
                        map[kv.Key] = kv.Value;
                        if (kv.Value >= next) next = kv.Value + 1;
                    }
                }
            }
            foreach (var f in Fields)
            {
                var n = f.NameTok.Value?.ToString();
                if (string.IsNullOrEmpty(n)) continue;
                if (!map.ContainsKey(n))
                {
                    map[n] = next++;
                }
            }
            // Stored properties allocate next to fields. Inherited
            // properties already received their slot via the base
            // class's BuildFieldShape recursion above.
            foreach (var p in Properties)
            {
                if (!p.HasBacking) continue;
                if (!map.ContainsKey(p.Name)) map[p.Name] = next++;
            }
            _fieldNameToIndex = map;
            _fieldSlotCount = next;
        }

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
            => ResolveInstanceMethodsImpl(name, new HashSet<string>(StringComparer.Ordinal));

        private List<FunctionDefinitionNode> ResolveInstanceMethodsImpl(string name, HashSet<string> visited)
        {
            if (!visited.Add(ClassName))
                return new List<FunctionDefinitionNode>();

            var result = Methods
                .Where(m => !m.IsStatic && !m.IsAbstract &&
                            string.Equals(m.VarNameTok?.Value?.ToString(), name, StringComparison.Ordinal))
                .ToList();

            if (result.Count > 0) return result;

            foreach (var trait in Traits)
            {
                foreach (var method in trait.GetDefaultMethodsByName(name))
                {
                    var wrapped = new FunctionDefinitionNode(
                        method.NameTok, method.ArgNameToks, method.ArgTypes, method.IsRefParams,
                        method.ParamDefaults, method.HasVarArgs, method.VarArgNameTok, method.VarArgType,
                        method.ReturnType, method.BodyNode, method.ShouldAutoReturn,
                        null, true, method.IsConstructor, method.IsOverride, method.IsAbstract, false);
                    wrapped.IsAsync = method.IsAsync;
                    wrapped.IsAsyncStream = method.IsAsyncStream;
                    result.Add(wrapped);
                }
            }

            if (BaseClass != null)
                result.AddRange(BaseClass.ResolveInstanceMethodsImpl(name, visited));

            return result;
        }

        public List<FunctionDefinitionNode> ResolveStaticMethods(string name)
            => ResolveStaticMethodsImpl(name, new HashSet<string>(StringComparer.Ordinal));

        private List<FunctionDefinitionNode> ResolveStaticMethodsImpl(string name, HashSet<string> visited)
        {
            if (!visited.Add(ClassName))
                return new List<FunctionDefinitionNode>();

            var result = Methods
                .Where(m => m.IsStatic && !m.IsAbstract &&
                            string.Equals(m.VarNameTok?.Value?.ToString(), name, StringComparison.Ordinal))
                .ToList();

            if (result.Count > 0) return result;

            if (BaseClass != null)
                result.AddRange(BaseClass.ResolveStaticMethodsImpl(name, visited));

            return result;
        }

        public List<ICallableMethodDefinition> ResolveCandidates(string methodName)
            => ResolveCandidatesImpl(methodName, new HashSet<string>(StringComparer.Ordinal));

        private List<ICallableMethodDefinition> ResolveCandidatesImpl(string methodName, HashSet<string> visited)
        {
            var result = new List<ICallableMethodDefinition>();

            if (!visited.Add(ClassName))
                return result;

            result.AddRange(Methods.Where(m =>
                !m.IsAbstract &&
                string.Equals(m.VarNameTok?.Value?.ToString(), methodName, StringComparison.Ordinal)));

            foreach (var trait in Traits)
            {
                result.AddRange(trait.GetDefaultMethodsByName(methodName));
            }

            if (BaseClass != null)
            {
                result.AddRange(BaseClass.ResolveCandidatesImpl(methodName, visited));
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

        public List<PropertyDescriptor> GetAbstractPropertiesInHierarchy()
        {
            var result = new List<PropertyDescriptor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Collect(ClassTypeValue? type)
            {
                if (type == null) return;
                foreach (var p in type.Properties)
                    if (p.IsAbstract && seen.Add(p.Name))
                        result.Add(p);
                Collect(type.BaseClass);
            }

            Collect(this);
            return result;
        }

        public bool HasConcretePropertyOverride(string name)
        {
            var p = GetProperty(name);
            return p != null && !p.IsAbstract;
        }

        // Walks the hierarchy collecting abstract event descriptors not
        // yet resolved by a concrete override. Used at concrete-class
        // build time to refuse instantiation when an abstract event is
        // left unimplemented.
        public List<RaLanguage.Interpreter.Runtime.Events.EventDescriptor> GetAbstractEventsInHierarchy()
        {
            var result = new List<RaLanguage.Interpreter.Runtime.Events.EventDescriptor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Collect(ClassTypeValue? type)
            {
                if (type == null) return;
                foreach (var ev in type.Events)
                    if (ev.IsAbstract && seen.Add(ev.Name))
                        result.Add(ev);
                Collect(type.BaseClass);
            }
            Collect(this);
            return result;
        }

        public bool HasConcreteEventOverride(string name)
        {
            var ev = GetEvent(name);
            return ev != null && !ev.IsAbstract;
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

            // Trait property requirements — abstract entries declare a
            // contract the implementor must supply. Default-bodied
            // property declarations in the trait carry their own
            // accessors and need not be re-declared.
            foreach (var requiredProp in trait.Properties)
            {
                if (!requiredProp.IsAbstract) continue;
                var prov = GetProperty(requiredProp.Name);
                if (prov == null)
                {
                    var fld = GetField(requiredProp.Name);
                    if (fld != null && !requiredProp.HasSetter && !requiredProp.HasInitter) continue;
                    return false;
                }
                if (requiredProp.HasGetter && !prov.HasGetter) return false;
                if (requiredProp.HasSetter && !prov.HasSetter) return false;
            }

            // Trait event requirements — implementer must supply a
            // concrete event whose full signature (name + arity +
            // cancellable + payload types pairwise) matches.
            foreach (var requiredEv in trait.Events)
            {
                if (!requiredEv.IsAbstract) continue;
                var prov = GetEvent(requiredEv.Name);
                if (prov == null) return false;
                if (prov.IsAbstract) return false;
                if (!prov.SignatureMatches(requiredEv)) return false;
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
            => HasMethodSignatureInHierarchyImpl(method, new HashSet<string>(StringComparer.Ordinal));

        private bool HasMethodSignatureInHierarchyImpl(ICallableMethodDefinition method, HashSet<string> visited)
        {
            if (!visited.Add(ClassName))
                return false;

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

            return BaseClass?.HasMethodSignatureInHierarchyImpl(method, visited) ?? false;
        }

        public bool HasAnyConstructorInHierarchy()
        {
            if (Methods.Any(m => m.IsConstructor)) return true;
            return BaseClass?.HasAnyConstructorInHierarchy() ?? false;
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

        // The UNNAMED generative constructor that binds these arguments. Kept
        // narrow on purpose: this drives `super(...)` chaining and the inherited
        // base-constructor fallback, both of which mean the classic `T(...)`
        // allocator — never a named constructor and never a factory.
        public FunctionDefinitionNode? ResolveOwnConstructor(List<RuntimeValue> args, Dictionary<string, RuntimeValue> namedArgs)
        {
            var ctors = Methods.Where(m => m.IsConstructor && m.ConstructorName == null).ToList();
            foreach (var ctor in ctors)
            {
                if (CallableBinder.CanBind(Context, ctor, args, namedArgs))
                    return ctor;
            }
            return null;
        }

        // Every constructor-flavoured member (generative, named or factory)
        // whose name matches `name` (null == the unnamed form) and whose
        // signature binds the supplied arguments. Used by the unified
        // construction core; ordering is declaration order.
        public List<FunctionDefinitionNode> ResolveConstructorCandidates(string? name, List<RuntimeValue> args, Dictionary<string, RuntimeValue> namedArgs)
        {
            var result = new List<FunctionDefinitionNode>();
            foreach (var m in Methods)
            {
                if (!m.IsAnyConstructor) continue;
                if (!string.Equals(m.ConstructorName, name, StringComparison.Ordinal)) continue;
                if (CallableBinder.CanBind(Context, m, args, namedArgs))
                    result.Add(m);
            }
            return result;
        }

        public bool HasAnyConstructorNamed(string name)
            => Methods.Any(m => m.IsAnyConstructor && string.Equals(m.ConstructorName, name, StringComparison.Ordinal));

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            return await ExecuteWithNamedArgs(positionalArgs, namedArgs, null);
        }

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs, List<TypeDescriptor?>? explicitTypeArgs)
            => await Construct(positionalArgs, namedArgs, explicitTypeArgs, null, Context!, PositionStart, PositionEnd);

        // Unified construction core. Reached from `T(args)` (ctorName == null,
        // routed here by FunctionCallExecutor with the live call site) and from
        // `T.name(args)` (via BoundConstructorValue). Resolves the constructor
        // by name + signature, enforces visibility against `callSite`, then
        // dispatches: generative ⇒ allocate + field-init + run body; factory ⇒
        // run body + enforce the return contract.
        public async ValueTask<RuntimeResult> Construct(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs,
            string? ctorName,
            Context callSite,
            Position posStart,
            Position posEnd)
        {
            var res = new RuntimeResult();
            callSite ??= Context!;

            var bindings = ResolveGenericBindings(explicitTypeArgs, posStart, posEnd, callSite, out var genErr);
            if (genErr != null) return res.Failure(genErr);

            var candidates = ResolveConstructorCandidates(ctorName, positionalArgs, namedArgs);

            if (candidates.Count > 1)
                return res.Failure(AmbiguousConstructorError(ctorName, candidates, callSite, posStart, posEnd));

            if (candidates.Count == 1)
            {
                var ctor = candidates[0];
                // Visibility: the UNNAMED constructor `T(...)` is the public
                // construction entry and is always callable (this also preserves
                // backward compatibility — every pre-existing constructor is
                // unnamed). NAMED constructors `T.name(...)` follow the standard
                // member rule: private unless `pub`, callable from outside only
                // when public. This is what powers the private-named-allocator +
                // public-factory idiom.
                if (ctor.ConstructorName != null && !ctor.IsPublic && !IsConstructionAllowedFrom(callSite))
                    return res.Failure(PrivateConstructorError(ctor, ctorName, callSite, posStart, posEnd));

                if (ctor.IsFactory)
                    return await RunFactory(ctor, positionalArgs, namedArgs, explicitTypeArgs, bindings, callSite, posStart, posEnd);

                return await RunGenerative(ctor, positionalArgs, namedArgs, bindings, callSite, posStart, posEnd);
            }

            // No candidate bound the arguments.
            if (ctorName != null)
            {
                if (!HasAnyConstructorNamed(ctorName))
                    return res.Failure(UnknownNamedConstructorError(ctorName, callSite, posStart, posEnd));
                return res.Failure(new RuntimeError(posStart, posEnd,
                    $"no overload of constructor '{ClassName}.{ctorName}' matches the supplied arguments",
                    callSite,
                    code: DiagnosticCode.RuntimeConstructorNotFound,
                    primaryLabel: "argument count or types do not match",
                    help: "check the arguments against the named constructor's parameters"));
            }

            // Unnamed, no matching constructor: preserve the historical
            // default-construct / inherited-constructor fallback verbatim.
            if (IsAbstract)
                return res.Failure(new RuntimeError(posStart, posEnd, $"Cannot instantiate abstract class '{ClassName}'", callSite));

            var instance = (ClassInstanceValue)new ClassInstanceValue(this, bindings)
                .SetContext(Context)
                .SetPos(posStart, posEnd);
            var initFieldErr = await InitializeFieldChain(instance, Context, this);
            if (initFieldErr != null) return res.Failure(initFieldErr);

            bool selfHasCtor = Methods.Any(m => m.IsAnyConstructor);
            if (!selfHasCtor)
            {
                var current = BaseClass;
                while (current != null)
                {
                    var baseCtorMatch = current.ResolveOwnConstructor(positionalArgs, namedArgs);
                    if (baseCtorMatch != null)
                    {
                        var boundBaseCtor = (BoundClassMethodValue)new BoundClassMethodValue(current, instance, baseCtorMatch, false)
                            .SetContext(Context)
                            .SetPos(posStart, posEnd);
                        var baseCtorRes = await boundBaseCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
                        if (baseCtorRes.Error != null) return baseCtorRes;
                        return res.Success(instance);
                    }

                    // Only an UNNAMED GENERATIVE base constructor participates
                    // in implicit base initialisation. A base that exposes only
                    // named or factory constructors does not block a derived
                    // class's default construction.
                    if (current.Methods.Any(m => m.IsConstructor && m.ConstructorName == null))
                        return res.Failure(new RuntimeError(posStart, posEnd,
                            $"No matching constructor found for class '{ClassName}' (inherited from '{current.ClassName}'). Check argument count and types.",
                            callSite));

                    current = current.BaseClass;
                }

                return res.Success(instance);
            }

            return res.Failure(new RuntimeError(posStart, posEnd,
                $"No matching constructor found for class '{ClassName}'. Check argument count and types.",
                callSite));
        }

        // Generative dispatch: allocate, run field-init chain, then the
        // constructor body (self-bound, IsInConstructor via BoundClassMethodValue).
        private async ValueTask<RuntimeResult> RunGenerative(
            FunctionDefinitionNode ctor, List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs,
            Dictionary<string, TypeDescriptor> bindings, Context callSite, Position posStart, Position posEnd)
        {
            var res = new RuntimeResult();
            if (IsAbstract)
                return res.Failure(new RuntimeError(posStart, posEnd, $"Cannot instantiate abstract class '{ClassName}'", callSite));

            var instance = (ClassInstanceValue)new ClassInstanceValue(this, bindings)
                .SetContext(Context)
                .SetPos(posStart, posEnd);

            var initErr = await InitializeFieldChain(instance, Context, this);
            if (initErr != null) return res.Failure(initErr);

            var boundCtor = (BoundClassMethodValue)new BoundClassMethodValue(this, instance, ctor, false)
                .SetContext(Context)
                .SetPos(posStart, posEnd);
            var ctorRes = await boundCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
            if (ctorRes.Error != null) return ctorRes;
            return res.Success(instance);
        }

        // Factory dispatch: run the body like a static method (no self, no
        // field-init, CurrentClassMethodOwner set so it can reach this class's
        // own private constructors), then enforce the return contract.
        private async ValueTask<RuntimeResult> RunFactory(
            FunctionDefinitionNode factory, List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs,
            List<TypeDescriptor?>? explicitTypeArgs, Dictionary<string, TypeDescriptor> bindings,
            Context callSite, Position posStart, Position posEnd)
        {
            var res = new RuntimeResult();

            var bound = (BoundClassMethodValue)new BoundClassMethodValue(this, null, factory, isStatic: true)
                .SetContext(callSite)
                .SetPos(posStart, posEnd);
            var bodyRes = await bound.ExecuteWithNamedArgs(positionalArgs, namedArgs, explicitTypeArgs);
            if (bodyRes.Error != null) return bodyRes;

            var produced = bodyRes.Value;

            // Implicit return type (no `->`): require a non-null instance of
            // this class or a subclass. An explicit return type was already
            // enforced by the bound-method return-type check.
            if (factory.ReturnType == null)
            {
                if (produced == null || produced.Type == RuntimeValueType.Null)
                    return res.Failure(FactoryMustReturnError(factory, posStart, posEnd, callSite));
                if (!(produced is ClassInstanceValue ci && ci.Definition.InheritsFrom(ClassName)))
                    return res.Failure(FactoryReturnTypeError(factory, produced, posStart, posEnd, callSite));
            }

            return res.Success(produced ?? NullValue.Null.SetContext(callSite).SetPos(posStart, posEnd));
        }

        // Shared immutable empty binding set for non-generic constructions.
        // A non-generic class binds no type parameters, so every such instance
        // can carry the same empty dict instead of allocating one per `T(args)`.
        // ClassInstanceValue.GenericBindings is read-only after construction
        // (verified repo-wide: no write/Add/index-set sites), so sharing is safe.
        private static readonly Dictionary<string, TypeDescriptor> s_emptyBindings =
            new Dictionary<string, TypeDescriptor>(0, StringComparer.Ordinal);

        // Resolves the generic-binding dict for one construction. Returns the
        // shared empty sentinel for a non-generic class (no per-construction
        // allocation), a freshly populated dict for a generic class, or null
        // with `error` set on a diagnostic. The allocation decision lives here
        // so Construct never news a dict it does not need.
        private Dictionary<string, TypeDescriptor>? ResolveGenericBindings(List<TypeDescriptor?>? explicitTypeArgs, Position posStart, Position posEnd, Context ctx, out Error? error)
        {
            error = null;
            if (GenericTypeParams.Count > 0)
            {
                if (explicitTypeArgs == null || explicitTypeArgs.Count == 0)
                {
                    error = new RuntimeError(posStart, posEnd,
                        $"Generic class '{ClassName}' requires explicit type arguments (e.g., {ClassName}<{string.Join(", ", GenericTypeParams)}>(...))", ctx);
                    return null;
                }
                if (explicitTypeArgs.Count != GenericTypeParams.Count)
                {
                    error = new RuntimeError(posStart, posEnd,
                        $"Wrong number of type arguments for class '{ClassName}': expected {GenericTypeParams.Count}, got {explicitTypeArgs.Count}", ctx);
                    return null;
                }
                var bindings = new Dictionary<string, TypeDescriptor>(StringComparer.Ordinal);
                for (int i = 0; i < GenericTypeParams.Count; i++)
                    bindings[GenericTypeParams[i]] = explicitTypeArgs[i] ?? new TypeDescriptor("any");
                var constraintErr = TypeSystem.ValidateWhereConstraints(bindings, WhereConstraints);
                if (constraintErr != null)
                {
                    error = new RuntimeError(posStart, posEnd, $"Where-constraint violated in class '{ClassName}': {constraintErr}", ctx);
                    return null;
                }
                return bindings;
            }

            if (explicitTypeArgs != null && explicitTypeArgs.Count > 0)
            {
                error = new RuntimeError(posStart, posEnd, $"Class '{ClassName}' is not generic and cannot take type arguments", ctx);
                return null;
            }
            return s_emptyBindings;
        }

        // A private constructor is reachable only from inside the declaring
        // class's own bodies. Uses the lexical owner (works for factories /
        // static methods that have no `self`) or a `self` of exactly this class.
        private bool IsConstructionAllowedFrom(Context callSite)
        {
            if (callSite == null) return false;
            var owner = callSite.CurrentClassMethodOwner;
            if (owner != null && string.Equals(owner.ClassName, ClassName, StringComparison.Ordinal)) return true;
            var selfEntry = callSite.SymbolTable?.GetEntry("self");
            if (selfEntry != null && selfEntry.Value.Type == RuntimeValueType.ClassInstance
                && string.Equals(((ClassInstanceValue)selfEntry.Value).Definition.ClassName, ClassName, StringComparison.Ordinal))
                return true;
            return false;
        }

        private string DescribeConstructor(FunctionDefinitionNode c)
        {
            var prefix = c.IsFactory ? "factory " : "";
            var name = c.ConstructorName == null ? ClassName : $"{ClassName}.{c.ConstructorName}";
            var ps = new List<string>(c.ArgTypes.Count);
            for (int i = 0; i < c.ArgNameToks.Count; i++)
            {
                var t = i < c.ArgTypes.Count ? c.ArgTypes[i] : null;
                ps.Add(t == null ? (c.ArgNameToks[i].Value?.ToString() ?? "_") : $"{c.ArgNameToks[i].Value}: {t}");
            }
            return $"{prefix}{name}({string.Join(", ", ps)})";
        }

        private List<string> CollectConstructorNames()
        {
            var names = new List<string>();
            void Walk(ClassTypeValue? t)
            {
                if (t == null) return;
                foreach (var m in t.Methods)
                {
                    if (m.IsAnyConstructor && m.ConstructorName != null && !names.Contains(m.ConstructorName))
                        names.Add(m.ConstructorName);
                    if (m.IsStatic && m.ConstructorName == null)
                    {
                        var sn = m.VarNameTok?.Value?.ToString();
                        if (sn != null && !names.Contains(sn)) names.Add(sn);
                    }
                }
                Walk(t.BaseClass);
            }
            Walk(this);
            return names;
        }

        // Closest named-constructor / static-member name to `name` (Levenshtein
        // ≤ 2), or null. Powers the "did you mean 'T.x'?" hint when member access
        // on the type fails — covers a mistyped named constructor as well as
        // static methods and fields across the hierarchy.
        public string? SuggestMember(string name)
        {
            var names = CollectConstructorNames();
            var t = this;
            while (t != null)
            {
                foreach (var k in t.StaticFields.Keys)
                    if (!names.Contains(k)) names.Add(k);
                t = t.BaseClass;
            }
            return ClosestName(name, names);
        }

        private RuntimeError PrivateConstructorError(FunctionDefinitionNode ctor, string? name, Context ctx, Position ps, Position pe)
        {
            string disp = name == null ? $"{ClassName}(...)" : $"{ClassName}.{name}(...)";
            string kind = ctor.IsFactory ? "factory" : "constructor";
            return new RuntimeError(ps, pe,
                $"{kind} '{disp}' of class '{ClassName}' is private",
                ctx,
                code: DiagnosticCode.RuntimeConstructorPrivate,
                primaryLabel: "called from outside the declaring class",
                help: name == null
                    ? $"mark the {kind} with 'pub', or expose construction through a public factory (e.g. 'pub factory {ClassName}.create(...)')"
                    : $"mark the {kind} with 'pub' to allow this call");
        }

        private RuntimeError AmbiguousConstructorError(string? name, List<FunctionDefinitionNode> cands, Context ctx, Position ps, Position pe)
        {
            var sb = new System.Text.StringBuilder();
            string disp = name == null ? $"{ClassName}(...)" : $"{ClassName}.{name}(...)";
            sb.Append($"ambiguous constructor call '{disp}' — these definitions all match the arguments:");
            foreach (var c in cands) sb.Append("\n  - ").Append(DescribeConstructor(c));
            return new RuntimeError(ps, pe, sb.ToString(), ctx,
                code: DiagnosticCode.RuntimeConstructorAmbiguous,
                primaryLabel: "more than one constructor matches",
                help: "make the parameter types distinct, or give the constructors different names so the call is unambiguous");
        }

        private RuntimeError UnknownNamedConstructorError(string name, Context ctx, Position ps, Position pe)
        {
            var names = CollectConstructorNames();
            var suggestion = ClosestName(name, names);
            string help = suggestion != null
                ? $"did you mean '{ClassName}.{suggestion}'?"
                : (names.Count > 0
                    ? $"available named constructors: {string.Join(", ", names.ConvertAll(n => ClassName + "." + n))}"
                    : $"class '{ClassName}' has no named constructors; construct it with '{ClassName}(...)'");
            return new RuntimeError(ps, pe,
                $"class '{ClassName}' has no constructor named '{name}'",
                ctx,
                code: DiagnosticCode.RuntimeConstructorNotFound,
                primaryLabel: "unknown named constructor",
                help: help);
        }

        private RuntimeError FactoryMustReturnError(FunctionDefinitionNode f, Position ps, Position pe, Context ctx)
        {
            string disp = f.ConstructorName == null ? $"{ClassName}(...)" : $"{ClassName}.{f.ConstructorName}(...)";
            return new RuntimeError(ps, pe,
                $"factory constructor '{disp}' must return a value",
                ctx,
                code: DiagnosticCode.RuntimeFactoryReturn,
                primaryLabel: "the factory finished without returning an instance",
                help: $"return a '{ClassName}' (or a subtype) on every path; to allow a missing result, declare the factory '-> {ClassName}?' and return null");
        }

        private RuntimeError FactoryReturnTypeError(FunctionDefinitionNode f, RuntimeValue produced, Position ps, Position pe, Context ctx)
        {
            string disp = f.ConstructorName == null ? $"{ClassName}(...)" : $"{ClassName}.{f.ConstructorName}(...)";
            string got = produced is ClassInstanceValue gci ? gci.Definition.ClassName : produced.Type.ToString();
            return new RuntimeError(ps, pe,
                $"factory constructor '{disp}' returned an incompatible value",
                ctx,
                code: DiagnosticCode.RuntimeFactoryReturn,
                primaryLabel: $"expected '{ClassName}' or a subtype, got '{got}'",
                help: $"a factory for '{ClassName}' must return a '{ClassName}' or one of its subclasses; to return a different type, declare an explicit '-> Type' on the factory");
        }

        private static string? ClosestName(string target, List<string> candidates)
        {
            int best = int.MaxValue;
            string? suggestion = null;
            foreach (var c in candidates)
            {
                int d = Levenshtein(target, c);
                if (d < best) { best = d; suggestion = c; }
            }
            return best <= 2 ? suggestion : null;
        }

        private static int Levenshtein(string a, string b)
        {
            int n = a.Length, m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = System.Math.Min(System.Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[m];
        }

        private static async ValueTask<Error?> InitializeFieldChain(ClassInstanceValue instance, Context context, ClassTypeValue type)
        {
            if (type.BaseClass != null)
            {
                var baseErr = await InitializeFieldChain(instance, context, type.BaseClass);
                if (baseErr != null) return baseErr;
            }

            foreach (var field in type.Fields)
            {
                if (field.IsStatic) continue;
                RuntimeValue value = NullValue.Null.SetContext(context).SetPos(type.PositionStart, type.PositionEnd);

                if (field.DefaultValueNode != null)
                {
                    var initRes = await new Interpreter().Visit(field.DefaultValueNode, context);
                    if (initRes.Error == null && initRes.Value != null)
                        value = initRes.Value;
                }

                var fieldName = field.NameTok.Value?.ToString() ?? "";
                var fieldKey = MetadataTarget.BuildKey(
                    field.IsStatic ? AnnotationTargetKind.StaticField : AnnotationTargetKind.Field,
                    type.ClassName,
                    fieldName);
                var (coerced, verr) = AnnotationValidator.CoerceAndValidate(fieldKey, value, $"field '{type.ClassName}.{fieldName}'", context);
                if (verr != null) return verr;
                value = coerced;

                instance.SetField(fieldName, value, field.IsPublic, field.FieldType, field.DeclarationType);
            }

            // Property defaults: stored, non-lazy, non-abstract properties
            // are populated from their DefaultValueNode at construction
            // time. Lazy properties skip this pass and initialise on first
            // read. Computed properties have no backing.
            foreach (var prop in type.Properties)
            {
                if (prop.IsAbstract) continue;
                if (!prop.HasBacking) continue;
                if (prop.IsLazy) continue;

                RuntimeValue value = Primitives.NullValue.Null.SetContext(context).SetPos(type.PositionStart, type.PositionEnd);

                if (prop.DefaultValueNode != null)
                {
                    var initRes = await new Interpreter().Visit(prop.DefaultValueNode, context);
                    if (initRes.Error != null) return initRes.Error;
                    if (initRes.Value != null) value = initRes.Value;
                }

                var propKey = MetadataTarget.BuildKey(AnnotationTargetKind.Field, type.ClassName, prop.Name);
                var (coerced, verr) = AnnotationValidator.CoerceAndValidate(propKey, value, $"property '{type.ClassName}.{prop.Name}'", context);
                if (verr != null) return verr;
                value = coerced;

                instance.SetField(prop.Name, value, prop.IsPublic, prop.PropertyType, Parser.Nodes.Variables.VariableDeclarationType.VARIABLE);
            }

            return null;
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

            // Property contracts. A concrete property anywhere in the
            // class hierarchy satisfies the contract when it provides
            // at least the accessors the interface requires (a class
            // may add a setter when the interface only requires a
            // getter, but never the reverse). A field of the same name
            // also satisfies a `prop X { get; }` contract — direct
            // field access is read-compatible with a getter.
            foreach (var requiredProp in iface.Properties)
            {
                var prov = GetProperty(requiredProp.Name);
                if (prov == null)
                {
                    var fld = GetField(requiredProp.Name);
                    if (fld != null && !requiredProp.HasSetter && !requiredProp.HasInitter) continue;
                    return false;
                }
                if (requiredProp.HasGetter && !prov.HasGetter) return false;
                if (requiredProp.HasSetter && !prov.HasSetter) return false;
                if (requiredProp.HasInitter && !prov.HasInitter && !prov.HasSetter) return false;
            }

            // Event contracts. Implementer must declare a concrete event
            // whose full signature matches every event in the interface
            // body. See EventDescriptor.SignatureMatches for the test:
            // name + arity + cancellable + payload types pairwise.
            foreach (var requiredEv in iface.Events)
            {
                var prov = GetEvent(requiredEv.Name);
                if (prov == null) return false;
                if (prov.IsAbstract) return false;
                if (!prov.SignatureMatches(requiredEv)) return false;
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
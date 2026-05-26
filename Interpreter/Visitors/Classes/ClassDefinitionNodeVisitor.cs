using RaLanguage.Errors;
using System.Threading.Tasks;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Properties;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Interpreter.Visitors.Functions;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Classes
{
    public class ClassDefinitionNodeVisitor : NodeVisitor<ClassDefinitionNode>
    {
        protected override async ValueTask<RuntimeResult> VisitNode(ClassDefinitionNode node, Context context, IInterpreter interpreter)
            => await Apply(node, context, interpreter);

        public static async ValueTask<RuntimeResult> Apply(ClassDefinitionNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();
            var className = node.NameTok.Value?.ToString() ?? "";
            
            if (string.IsNullOrWhiteSpace(className))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid class name", context));

            if (context.SymbolTable.Get(className) != null)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"'{className}' is already defined", context));

            ClassTypeValue? baseClass = null;
            if (node.BaseType != null)
            {
                var baseValue = context.SymbolTable.Get(node.BaseType.Name);
                baseClass = baseValue as ClassTypeValue;

                if (baseClass == null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Base class '{node.BaseType}' not found", context));

                if (string.Equals(baseClass.ClassName, className, StringComparison.Ordinal))
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "A class cannot inherit from itself", context));

                var cursor = baseClass.BaseClass;
                while (cursor != null)
                {
                    if (string.Equals(cursor.ClassName, className, StringComparison.Ordinal))
                        return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Inheritance cycle detected", context));

                    cursor = cursor.BaseClass;
                }
            }

            var classValue = (ClassTypeValue) new ClassTypeValue(className, node.IsPublic, node.IsAbstract, node.BaseType, node.WithTraits, node.Fields, node.Methods, node.Operators, node.GenericTypeParams, node.WhereConstraints)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            var traits = new List<TraitTypeValue>();
            foreach (var td in node.WithTraits)
            {
                var traitSymbol = context.SymbolTable.Get(td.Name) as TraitTypeValue;
                if (traitSymbol == null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Trait '{td.Name}' not found", context));

                traits.Add(traitSymbol);
            }

            classValue.Traits = traits;
            classValue.BaseClass = baseClass;

            // Register property descriptors *before* trait/interface
            // satisfaction checks — the conformance test needs to see
            // the concrete property set on this class.
            foreach (var p in node.Properties)
            {
                var pname = p.NameTok.Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(pname)) continue;

                if (classValue.HasField(pname))
                {
                    return res.Failure(new RuntimeError(
                        p.PositionStart, p.PositionEnd,
                        $"property '{pname}' on class '{className}' collides with a field of the same name",
                        context));
                }

                if (classValue.PropertyByName.ContainsKey(pname))
                {
                    return res.Failure(new RuntimeError(
                        p.PositionStart, p.PositionEnd,
                        $"duplicate property '{pname}' in class '{className}'",
                        context));
                }

                if (p.IsAbstract && !node.IsAbstract)
                {
                    return res.Failure(new RuntimeError(
                        p.PositionStart, p.PositionEnd,
                        $"abstract property '{pname}' can only appear inside an abstract class",
                        context));
                }

                classValue.AddProperty(PropertyBuilder.Build(p, className));
            }

            // Register event descriptors. Same precedence as properties —
            // happens before trait / interface conformance checks so the
            // satisfier sees the concrete event set.
            foreach (var ev in node.Events)
            {
                var ename = ev.NameTok.Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(ename)) continue;

                if (classValue.HasField(ename))
                {
                    return res.Failure(new RuntimeError(
                        ev.PositionStart, ev.PositionEnd,
                        $"event '{ename}' on class '{className}' collides with a field of the same name",
                        context));
                }

                if (classValue.PropertyByName.ContainsKey(ename))
                {
                    return res.Failure(new RuntimeError(
                        ev.PositionStart, ev.PositionEnd,
                        $"event '{ename}' on class '{className}' collides with a property of the same name",
                        context));
                }

                if (classValue.EventByName.ContainsKey(ename))
                {
                    return res.Failure(new RuntimeError(
                        ev.PositionStart, ev.PositionEnd,
                        $"duplicate event '{ename}' in class '{className}'",
                        context));
                }

                if (ev.IsAbstract && !node.IsAbstract)
                {
                    return res.Failure(new RuntimeError(
                        ev.PositionStart, ev.PositionEnd,
                        $"abstract event '{ename}' can only appear inside an abstract class",
                        context));
                }

                classValue.AddEvent(
                    RaLanguage.Interpreter.Runtime.Events.EventBuilder.Build(ev, className));
            }

            foreach (var trait in traits)
            {
                if (!classValue.SatisfiesTrait(trait))
                {
                    return res.Failure(new RuntimeError(
                        node.PositionStart,
                        node.PositionEnd,
                        $"Class '{className}' does not satisfy trait '{trait.TraitName}'",
                        context));
                }
            }

            foreach (var ifaceDesc in node.ImplementedInterfaces)
            {
                var ifaceSymbol = context.SymbolTable.Get(ifaceDesc.Name) as InterfaceTypeValue;
                if (ifaceSymbol == null)
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Interface '{ifaceDesc.Name}' not found", context));

                if (!classValue.ImplementsInterface(ifaceSymbol))
                {
                    return res.Failure(new RuntimeError(
                        node.PositionStart,
                        node.PositionEnd,
                        $"Class '{className}' does not implement interface '{ifaceSymbol.InterfaceName}'",
                        context));
                }
            }

            var contractErr = ValidateInheritanceContract(node, classValue, context);
            if (contractErr != null) return res.Failure(contractErr);

            foreach (var field in node.Fields.Where(f => f.IsAbstract))
            {
                if (!node.IsAbstract)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Abstract fields can only be declared in abstract classes",
                        context));
                }

                if (field.DefaultValueNode != null)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Abstract fields cannot have default values",
                        context));
                }
            }

            foreach (var field in node.Fields)
            {
                var fieldName = field.NameTok.Value?.ToString() ?? "";
                
                if (field.DeclarationType == VariableDeclarationType.CONST && field.DefaultValueNode == null && !field.IsAbstract)
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Const field '{fieldName}' must be initialized with a value",
                        context));
                }
            }

            foreach (var field in node.Fields.Where(f => f.IsStatic))
            {
                RuntimeValue value = NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

                if (field.DefaultValueNode != null)
                {
                    var initRes = await RaLanguage.Interpreter.Runtime.IrExpressionEvaluator.Evaluate(field.DefaultValueNode, context, interpreter);
                    if (initRes.Error != null) return res.Failure(initRes.Error);
                    value = initRes.Value ?? value;
                }

                if (field.FieldType != null && !TypeSystem.IsAssignable(context, field.FieldType, value))
                {
                    return res.Failure(new RuntimeError(
                        field.NameTok.PositionStart,
                        field.NameTok.PositionEnd,
                        $"Type mismatch for static field '{field.NameTok.Value?.ToString()}'",
                        context));
                }

                classValue.SetStaticField(
                    field.NameTok.Value?.ToString() ?? "",
                    value,
                    field.IsPublic,
                    field.FieldType);
            }

            ValidateToStringMethod(node, classValue, context, ref res);
            if (res.ShouldReturn()) return res;

            if (!node.IsAbstract)
            {
                var unresolvedFields = classValue.GetAbstractFieldsInHierarchy()
                    .Where(f => !classValue.HasField(f.NameTok.Value?.ToString() ?? ""))
                    .ToList();

                if (unresolvedFields.Count > 0)
                {
                    return res.Failure(new RuntimeError(
                        node.PositionStart,
                        node.PositionEnd,
                        $"Class '{className}' does not implement abstract fields: {string.Join(", ", unresolvedFields.Select(f => f.NameTok.Value?.ToString() ?? ""))}",
                        context));
                }

                var unresolvedProps = classValue.GetAbstractPropertiesInHierarchy()
                    .Where(p => !classValue.HasConcretePropertyOverride(p.Name))
                    .ToList();

                if (unresolvedProps.Count > 0)
                {
                    return res.Failure(new RuntimeError(
                        node.PositionStart,
                        node.PositionEnd,
                        $"Class '{className}' does not implement abstract properties: {string.Join(", ", unresolvedProps.Select(p => p.Name))}",
                        context));
                }

                var unresolvedEvents = classValue.GetAbstractEventsInHierarchy()
                    .Where(e => !classValue.HasConcreteEventOverride(e.Name))
                    .ToList();

                if (unresolvedEvents.Count > 0)
                {
                    return res.Failure(new RuntimeError(
                        node.PositionStart,
                        node.PositionEnd,
                        $"Class '{className}' does not implement abstract events: {string.Join(", ", unresolvedEvents.Select(e => e.Name))}",
                        context));
                }

                // Override-signature check: every `override event` on this
                // class must structurally match the abstract event it
                // overrides (name, arity, cancellable, payload types).
                foreach (var localEv in classValue.Events)
                {
                    if (!localEv.IsOverride) continue;
                    var baseEv = classValue.BaseClass?.GetEvent(localEv.Name);
                    if (baseEv == null)
                    {
                        return res.Failure(new RuntimeError(
                            localEv.SourceNode.PositionStart, localEv.SourceNode.PositionEnd,
                            $"override event '{localEv.Name}' on class '{className}' has no matching event in any base class",
                            context));
                    }
                    if (!localEv.SignatureMatches(baseEv))
                    {
                        return res.Failure(new RuntimeError(
                            localEv.SourceNode.PositionStart, localEv.SourceNode.PositionEnd,
                            $"override event '{className}.{localEv.Name}' does not match the base event signature (arity, cancellable flag, or payload types differ)",
                            context));
                    }
                }
            }

            context.SymbolTable.Set(
                className,
                classValue,
                isLet: true,
                declaredType: new TypeDescriptor(className),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);

            var classTarget = new MetadataTarget(AnnotationTargetKind.Class, null, className);
            if (node.HasAnnotations)
            {
                var annErr = AnnotationProcessor.Process(node.Annotations, classTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            if (classValue.BaseClass != null)
            {
                foreach (var method in node.Methods)
                {
                    if (!method.IsOverride) continue;
                    var methodName = method.VarNameTok?.Value?.ToString() ?? "";
                    var baseKey = MetadataTarget.BuildKey(AnnotationTargetKind.Method, classValue.BaseClass.ClassName, methodName);
                    var sealedAnn = MetadataRegistry.Global.FindEffective(baseKey, "sealed", MetadataKeyResolver.ForContext(context));
                    if (sealedAnn != null)
                    {
                        return res.Failure(new RuntimeError(
                            method.PositionStart,
                            method.PositionEnd,
                            $"Method '{methodName}' is sealed on base class '{classValue.BaseClass.ClassName}' and cannot be overridden",
                            context));
                    }

                    foreach (var ann in MetadataRegistry.Global.GetByKey(baseKey))
                    {
                        if (ann.Definition.IsSealed)
                        {
                            return res.Failure(new RuntimeError(
                                method.PositionStart,
                                method.PositionEnd,
                                $"Method '{methodName}' is sealed by '@{ann.DefinitionName}' on base class '{classValue.BaseClass.ClassName}' and cannot be overridden",
                                context));
                        }
                    }
                }

                foreach (var field in node.Fields)
                {
                    if (!field.IsOverride) continue;
                    var fieldName = field.NameTok.Value?.ToString() ?? "";
                    var baseKey = MetadataTarget.BuildKey(AnnotationTargetKind.Field, classValue.BaseClass.ClassName, fieldName);
                    foreach (var ann in MetadataRegistry.Global.GetByKey(baseKey))
                    {
                        if (ann.Definition.IsSealed)
                        {
                            return res.Failure(new RuntimeError(
                                field.PositionStart,
                                field.PositionEnd,
                                $"Field '{fieldName}' is sealed by '@{ann.DefinitionName}' on base class '{classValue.BaseClass.ClassName}' and cannot be overridden",
                                context));
                        }
                    }
                }
            }

            foreach (var field in node.Fields)
            {
                if (!field.HasAnnotations) continue;
                var kind = field.IsStatic ? AnnotationTargetKind.StaticField : AnnotationTargetKind.Field;
                var fieldTarget = new MetadataTarget(kind, className, field.NameTok.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(field.Annotations, fieldTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var method in node.Methods)
            {
                var name = method.VarNameTok?.Value?.ToString() ?? className;
                if (method.HasAnnotations)
                {
                    var kind = method.IsConstructor ? AnnotationTargetKind.Constructor : AnnotationTargetKind.Method;
                    var methodTarget = new MetadataTarget(kind, className, name);
                    var annErr = AnnotationProcessor.Process(method.Annotations, methodTarget, context, interpreter);
                    if (annErr != null) return res.Failure(annErr);
                }

                var paramErr = RaLanguage.Interpreter.Runtime.FunctionDefinitionHelper.RegisterParameterAnnotations(method, $"{className}.{name}", context, interpreter);
                if (paramErr != null) return res.Failure(paramErr);
            }

            foreach (var op in node.Operators)
            {
                if (!op.HasAnnotations) continue;
                var opTarget = new MetadataTarget(AnnotationTargetKind.Operator, className, op.OperatorTok.Type.ToString());
                var annErr = AnnotationProcessor.Process(op.Annotations, opTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            foreach (var ev in node.Events)
            {
                if (!ev.HasAnnotations) continue;
                var kind = ev.IsStatic ? AnnotationTargetKind.StaticEvent : AnnotationTargetKind.Event;
                var evTarget = new MetadataTarget(kind, className, ev.NameTok.Value?.ToString() ?? "");
                var annErr = AnnotationProcessor.Process(ev.Annotations, evTarget, context, interpreter);
                if (annErr != null) return res.Failure(annErr);
            }

            return res.Success(classValue);
        }

        private static Error? ValidateInheritanceContract(ClassDefinitionNode node, ClassTypeValue classValue, Context context)
        {
            var className = classValue.ClassName;

            var ownMethodSignatures = new Dictionary<string, FunctionDefinitionNode>(StringComparer.Ordinal);
            foreach (var method in node.Methods)
            {
                if (method.IsConstructor) continue;

                var key = MethodSignature.KeyOf(method);
                if (ownMethodSignatures.TryGetValue(key, out _))
                {
                    return new RuntimeError(
                        method.PositionStart,
                        method.PositionEnd,
                        $"Duplicate method '{method.VarNameTok?.Value}' with the same signature in class '{className}'",
                        context);
                }

                ownMethodSignatures[key] = method;
            }

            var ownFieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in node.Fields)
            {
                var fieldName = field.NameTok.Value?.ToString() ?? "";
                if (!ownFieldNames.Add(fieldName))
                {
                    return new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Duplicate field '{fieldName}' in class '{className}'",
                        context);
                }
            }

            foreach (var method in node.Methods)
            {
                if (method.IsConstructor || method.IsOverride || method.IsAbstract) continue;

                if (classValue.HasInheritedOrTraitMethodSignature(method))
                {
                    var methodName = method.VarNameTok?.Value?.ToString() ?? "<anonymous>";
                    var origin = DescribeMethodOrigin(classValue, method);

                    return new RuntimeError(
                        method.PositionStart,
                        method.PositionEnd,
                        $"Method '{methodName}' in class '{className}' shadows the same-signature member from {origin}. " +
                        $"Mark it 'override' to replace the inherited definition, or rename it.",
                        context);
                }
            }

            foreach (var method in node.Methods.Where(m => m.IsOverride))
            {
                var methodName = method.VarNameTok?.Value?.ToString() ?? "<anonymous>";

                if (method.IsConstructor)
                {
                    return new RuntimeError(
                        method.PositionStart,
                        method.PositionEnd,
                        $"Constructors cannot be marked 'override' in class '{className}'",
                        context);
                }

                if (classValue.BaseClass == null && classValue.Traits.Count == 0)
                {
                    return new RuntimeError(
                        method.PositionStart,
                        method.PositionEnd,
                        $"Method '{methodName}' is marked 'override' but class '{className}' has no base class or trait",
                        context);
                }

                if (!classValue.HasInheritedOrTraitMethodSignature(method))
                {
                    return new RuntimeError(
                        method.PositionStart,
                        method.PositionEnd,
                        $"No matching base or trait-default method found to override for '{methodName}' in class '{className}'. " +
                        $"The override must match the inherited signature exactly.",
                        context);
                }
            }

            foreach (var field in node.Fields)
            {
                if (field.IsOverride || field.IsAbstract) continue;

                if (classValue.HasInheritedOrTraitField(field))
                {
                    var fieldName = field.NameTok.Value?.ToString() ?? "";
                    return new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Field '{fieldName}' in class '{className}' shadows an inherited or trait field. " +
                        $"Mark it 'override' to replace the inherited definition, or rename it.",
                        context);
                }
            }

            foreach (var field in node.Fields.Where(f => f.IsOverride))
            {
                var fieldName = field.NameTok.Value?.ToString() ?? "";

                if (field.IsStatic)
                {
                    return new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"Static fields cannot be marked 'override' (field '{fieldName}' in class '{className}')",
                        context);
                }

                if (!classValue.HasInheritedOrTraitField(field))
                {
                    return new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"No matching base or trait field found to override for '{fieldName}' in class '{className}'",
                        context);
                }
            }

            var traitDefaultOrigins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var trait in classValue.Traits)
            {
                foreach (var method in trait.Methods.Where(m => m.HasBody))
                {
                    var key = MethodSignature.KeyOf(method);
                    if (!traitDefaultOrigins.TryGetValue(key, out var origins))
                    {
                        origins = new List<string>();
                        traitDefaultOrigins[key] = origins;
                    }

                    if (!origins.Contains(trait.TraitName, StringComparer.Ordinal))
                        origins.Add(trait.TraitName);
                }
            }

            foreach (var entry in traitDefaultOrigins)
            {
                if (entry.Value.Count <= 1) continue;
                if (ownMethodSignatures.ContainsKey(entry.Key)) continue;

                return new RuntimeError(
                    node.PositionStart,
                    node.PositionEnd,
                    $"Class '{className}' inherits conflicting default method implementations from traits {string.Join(", ", entry.Value)} " +
                    $"for the same signature. Provide an 'override' in the class to disambiguate.",
                    context);
            }

            return null;
        }

        private static string DescribeMethodOrigin(ClassTypeValue classValue, FunctionDefinitionNode method)
        {
            if (classValue.BaseClass != null && classValue.BaseClass.HasMethodSignatureInHierarchy(method))
                return $"base class '{classValue.BaseClass.ClassName}'";

            var methodName = method.VarNameTok?.Value?.ToString() ?? "";
            foreach (var trait in classValue.Traits)
            {
                if (trait.GetDefaultMethodsByName(methodName)
                    .Any(m => MethodSignature.MatchesSignature(m, method)))
                {
                    return $"trait '{trait.TraitName}'";
                }
            }

            return "an inherited member";
        }

        private static void ValidateToStringMethod(ClassDefinitionNode node, ClassTypeValue classValue, Context context, ref RuntimeResult res)
        {
            var toStringMethod = node.Methods.FirstOrDefault(m => 
                string.Equals(m.VarNameTok?.Value?.ToString(), "to_string", StringComparison.Ordinal));

            if (toStringMethod == null)
                return;

            if (toStringMethod.ArgNameToks.Count > 0)
            {
                res = res.Failure(new RuntimeError(
                    toStringMethod.PositionStart,
                    toStringMethod.PositionEnd,
                    $"Method 'to_string' must not have parameters",
                    context));
                return;
            }

            if (toStringMethod.ReturnType == null || 
                !string.Equals(toStringMethod.ReturnType.Name, "string", StringComparison.Ordinal))
            {
                res = res.Failure(new RuntimeError(
                    toStringMethod.PositionStart,
                    toStringMethod.PositionEnd,
                    $"Method 'to_string' must return type 'string'",
                    context));
                return;
            }
        }
    }
}
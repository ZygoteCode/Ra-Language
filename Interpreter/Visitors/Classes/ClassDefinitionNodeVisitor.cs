using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Visitors.Classes
{
    public class ClassDefinitionNodeVisitor : NodeVisitor<ClassDefinitionNode>
    {
        protected override RuntimeResult VisitNode(ClassDefinitionNode node, Context context, IInterpreter interpreter)
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

            var classValue = (ClassTypeValue) new ClassTypeValue(className, node.IsPublic, node.IsAbstract, node.BaseType, node.WithTraits, node.Fields, node.Methods)
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

            foreach (var method in node.Methods.Where(m => m.IsOverride))
            {
                if (method.IsConstructor)
                    return res.Failure(new RuntimeError(method.PositionStart, method.PositionEnd, "Constructors cannot be marked override", context));

                if (!classValue.HasInheritedOrTraitMethodSignature(method))
                {
                    return res.Failure(new RuntimeError(
                        method.PositionStart,
                        method.PositionEnd,
                        $"No base/trait method found to override for '{method.VarNameTok?.Value}'",
                        context));
                }
            }

            foreach (var field in node.Fields.Where(f => f.IsOverride))
            {
                if (field.IsStatic)
                    return res.Failure(new RuntimeError(field.PositionStart, field.PositionEnd, "Static fields cannot be marked override", context));

                if (!classValue.HasInheritedOrTraitField(field))
                {
                    return res.Failure(new RuntimeError(
                        field.PositionStart,
                        field.PositionEnd,
                        $"No base/trait field found to override for '{field.NameTok.Value?.ToString()}'",
                        context));
                }
            }

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

            foreach (var field in node.Fields.Where(f => f.IsStatic))
            {
                RuntimeValue value = new NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

                if (field.DefaultValueNode != null)
                {
                    var initRes = interpreter.Visit(field.DefaultValueNode, context);
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

            ValidateOverrides(node, classValue, context, ref res);
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
            }

            context.SymbolTable.Set(
                className,
                classValue,
                isLet: true,
                declaredType: new TypeDescriptor(className),
                isStaticallyTyped: true,
                isPublic: node.IsPublic);
            
            return res.Success(classValue);
        }

        private void ValidateOverrides(ClassDefinitionNode node, ClassTypeValue classValue, Context context, ref RuntimeResult res)
        {
            foreach (var method in node.Methods.Where(m => m.IsOverride))
            {
                if (method.IsConstructor)
                {
                    res = res.Failure(new RuntimeError(method.PositionStart, method.PositionEnd, "Constructors cannot be marked override", context));
                    return;
                }

                if (classValue.BaseClass == null)
                {
                    res = res.Failure(new RuntimeError(method.PositionStart, method.PositionEnd, $"Method '{method.VarNameTok?.Value}' is marked override but class has no base class", context));
                    return;
                }

                var candidate = classValue.BaseClass.ResolveMethod(method.VarNameTok?.Value?.ToString() ?? "", new List<RuntimeValue>(), new Dictionary<string, RuntimeValue>());
                if (candidate == null)
                {
                    res = res.Failure(new RuntimeError(method.PositionStart, method.PositionEnd, $"No base method found to override for '{method.VarNameTok?.Value}'", context));
                    return;
                }

                if (!SameSignature(method, candidate))
                {
                    res = res.Failure(new RuntimeError(method.PositionStart, method.PositionEnd, $"Override signature mismatch for method '{method.VarNameTok?.Value}'", context));
                    return;
                }
            }
        }

        private bool SameSignature(FunctionDefinitionNode a, FunctionDefinitionNode b)
        {
            if (a.ArgNameToks.Count != b.ArgNameToks.Count) return false;
            if (a.HasVarArgs != b.HasVarArgs) return false;
            if (a.ArgTypes.Count != b.ArgTypes.Count) return false;

            for (int i = 0; i < a.ArgTypes.Count; i++)
            {
                var x = a.ArgTypes[i]?.ToString() ?? "";
                var y = b.ArgTypes[i]?.ToString() ?? "";
                if (!string.Equals(x, y, StringComparison.Ordinal)) return false;
            }

            return true;
        }
    }
}
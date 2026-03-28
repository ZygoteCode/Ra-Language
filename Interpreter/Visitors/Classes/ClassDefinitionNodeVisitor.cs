using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Interfaces;
using RaLanguage.Interpreter.Values.Primitives;
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

            var classValue = (ClassTypeValue) new ClassTypeValue(className, node.IsPublic, node.BaseType, node.Fields, node.Methods)
                .SetContext(context)
                .SetPos(node.PositionStart, node.PositionEnd);

            classValue.BaseClass = baseClass;

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

            ValidateOverrides(node, classValue, context, ref res);
            if (res.Error != null) return res;
            if (res.ShouldReturn()) return res;

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
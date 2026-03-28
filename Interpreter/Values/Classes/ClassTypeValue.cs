using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Classes;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Classes
{
    public class ClassTypeValue : BaseFunctionValue
    {
        public string ClassName { get; }
        public bool IsPublic { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<FunctionDefinitionNode> Methods { get; }

        public override RuntimeValueType Type => RuntimeValueType.ClassType;

        public ClassTypeValue(
            string className,
            bool isPublic,
            List<StructFieldDefinitionNode> fields,
            List<FunctionDefinitionNode> methods
        ) : base(className)
        {
            ClassName = className;
            IsPublic = isPublic;
            Fields = fields;
            Methods = methods;
        }

        public FunctionDefinitionNode? GetConstructor(
            List<RuntimeValue> positionalArgs,
            Dictionary<string, RuntimeValue> namedArgs,
            Context context)
        {
            var ctors = Methods.Where(m => m.IsConstructor).ToList();

            foreach (var ctor in ctors)
            {
                if (CallableBinder.CanBind(context, ctor, positionalArgs, namedArgs))
                    return ctor;
            }

            return null;
        }

        public FunctionDefinitionNode? GetMethod(string name)
            => Methods.FirstOrDefault(m => string.Equals(m.VarNameTok?.Value?.ToString(), name, StringComparison.Ordinal));

        public override RuntimeResult Execute(List<RuntimeValue> args)
            => ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            var instance = (ClassInstanceValue) new ClassInstanceValue(this)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            var initCtx = GenerateNewContext();
            initCtx.SymbolTable.Set(
                "self",
                instance,
                isLet: true,
                declaredType: new TypeDescriptor(ClassName),
                isStaticallyTyped: true,
                isPublic: false);

            foreach (var field in Fields)
            {
                RuntimeValue value = new NullValue().SetContext(initCtx).SetPos(PositionStart, PositionEnd);

                if (field.DefaultValueNode != null)
                {
                    var initRes = interpreter.Visit(field.DefaultValueNode, initCtx);
                    if (initRes.Error != null) return res.Failure(initRes.Error);
                    value = initRes.Value ?? value;
                }

                if (field.FieldType != null && !TypeSystem.IsAssignable(initCtx, field.FieldType, value))
                {
                    return res.Failure(new RuntimeError(
                        field.NameTok.PositionStart,
                        field.NameTok.PositionEnd,
                        $"Type mismatch for field '{field.NameTok.Value?.ToString()}': expected '{field.FieldType}', got '{value.Type}'",
                        Context));
                }

                instance.SetField(field.NameTok.Value?.ToString() ?? "", value, field.IsPublic, field.FieldType);
            }

            var ctor = GetConstructor(positionalArgs, namedArgs, Context);
            if (ctor == null)
            {
                if (Methods.Any(m => m.IsConstructor))
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"No matching constructor found for class '{ClassName}'", Context));

                return res.Success(instance);
            }

            var boundCtor = (BoundClassMethodValue) new BoundClassMethodValue(this, instance, ctor)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            var callRes = boundCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
            if (callRes.Error != null) return callRes;

            return res.Success(instance);
        }

        public override RuntimeValue Copy()
            => new ClassTypeValue(ClassName, IsPublic, Fields, Methods)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<class {ClassName}>";
    }
}
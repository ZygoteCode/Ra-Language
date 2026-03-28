using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Classes;
using RaLanguage.Interpreter.Values.Classes;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ClassTypeValue : BaseFunctionValue
    {
        public string ClassName { get; }
        public bool IsPublic { get; }
        public TypeDescriptor? BaseType { get; }
        public ClassTypeValue? BaseClass { get; set; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<FunctionDefinitionNode> Methods { get; }

        public override RuntimeValueType Type => RuntimeValueType.ClassType;

        public ClassTypeValue(string className, bool isPublic, TypeDescriptor? baseType, List<StructFieldDefinitionNode> fields, List<FunctionDefinitionNode> methods)
            : base(className)
        {
            ClassName = className;
            IsPublic = isPublic;
            BaseType = baseType;
            Fields = fields;
            Methods = methods;
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

        public StructFieldDefinitionNode? GetField(string name)
            => Fields.FirstOrDefault(f => string.Equals(f.NameTok.Value?.ToString(), name, StringComparison.Ordinal));

        public FunctionDefinitionNode? ResolveMethod(string name, List<RuntimeValue> args, Dictionary<string, RuntimeValue> namedArgs)
        {
            // derived-first lookup
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
            var instance = (ClassInstanceValue) new ClassInstanceValue(this)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            // init campi della catena base -> derived
            InitializeFieldChain(instance, Context, this);

            // 1) prova prima i costruttori della classe corrente
            var ownCtor = ResolveOwnConstructor(positionalArgs, namedArgs);
            if (ownCtor != null)
            {
                var boundCtor = (BoundClassMethodValue) new BoundClassMethodValue(this, instance, ownCtor)
                    .SetContext(Context)
                    .SetPos(PositionStart, PositionEnd);

                var ctorRes = boundCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
                if (ctorRes.Error != null) return ctorRes;

                return res.Success(instance);
            }

            // 2) se la classe corrente NON ha alcun costruttore, prova la catena base
            if (!Methods.Any(m => m.IsConstructor) && BaseClass != null)
            {
                var baseCtorMatch = BaseClass.ResolveOwnConstructor(positionalArgs, namedArgs);
                if (baseCtorMatch != null)
                {
                    var boundBaseCtor = (BoundClassMethodValue) new BoundClassMethodValue(BaseClass, instance, baseCtorMatch)
                        .SetContext(Context)
                        .SetPos(PositionStart, PositionEnd);

                    var baseCtorRes = boundBaseCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
                    if (baseCtorRes.Error != null) return baseCtorRes;

                    return res.Success(instance);
                }

                // se il base ha costruttori ma nessuno compatibile, errore chiaro
                if (BaseClass.Methods.Any(m => m.IsConstructor))
                {
                    return res.Failure(new RuntimeError(
                        PositionStart,
                        PositionEnd,
                        $"No matching constructor found for class '{ClassName}' or its base class '{BaseClass.ClassName}'",
                        Context));
                }
            }

            // nessun costruttore dichiarato e nessun base compatibile
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

                instance.SetField(field.NameTok.Value?.ToString() ?? "", value, field.IsPublic, field.FieldType);
            }
        }

        public override RuntimeValue Copy()
            => new ClassTypeValue(ClassName, IsPublic, BaseType, Fields, Methods)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<class {ClassName}>";
    }
}
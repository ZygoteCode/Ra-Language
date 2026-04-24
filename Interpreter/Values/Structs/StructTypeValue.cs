using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Structs
{
    public class StructTypeValue : RuntimeValue
    {
        public string StructName { get; }
        public bool IsPublic { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<StructMethodDefinitionNode> Methods { get; }
        public List<OperatorDefinitionNode> Operators { get; } = new();

        public sealed override RuntimeValueType Type => RuntimeValueType.StructType;
        public sealed override bool IsCopy => true;

        public StructTypeValue(string structName, bool isPublic, List<StructFieldDefinitionNode> fields, List<StructMethodDefinitionNode> methods, List<OperatorDefinitionNode> operators)
        {
            StructName = structName;
            IsPublic = isPublic;
            Fields = fields;
            Methods = methods;
            Operators = operators;
        }

        public bool HasField(string name)
        {
            foreach (StructFieldDefinitionNode field in Fields)
            {
                if (field.NameTok.Value.ToString().Equals(name))
                {
                    return true;
                }
            }

            return false;
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

        public StructMethodDefinitionNode? GetConstructor(List<RuntimeValue> args, Dictionary<string, RuntimeValue> namedArgs)
        {
            var ctors = Methods.Where(m => m.IsConstructor).ToList();

            foreach (var ctor in ctors)
            {
                if (StructBinder.CanBind(
                    ctor.ArgNameToks.Select(t => t.Value?.ToString() ?? "").ToList(),
                    ctor.ArgTypes,
                    ctor.ParamDefaults,
                    ctor.HasVarArgs,
                    ctor.VarArgNameTok,
                    ctor.VarArgType,
                    args,
                    namedArgs))
                {
                    return ctor;
                }
            }

            return null;
        }

        public StructMethodDefinitionNode? GetMethod(string name)
            => Methods.FirstOrDefault(m => string.Equals(m.NameTok.Value?.ToString(), name, StringComparison.Ordinal));

        public StructFieldDefinitionNode? GetField(string name)
            => Fields.FirstOrDefault(f => string.Equals(f.NameTok.Value?.ToString(), name, StringComparison.Ordinal));

        public sealed override RuntimeResult Execute(List<RuntimeValue> args)
            => ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public RuntimeResult ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();

            var instance = (StructInstanceValue) new StructInstanceValue(this)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            foreach (var field in Fields)
            {
                RuntimeValue fieldValue = new NullValue().SetContext(Context).SetPos(PositionStart, PositionEnd);

                if (field.DefaultValueNode != null)
                {
                    var initRes = new Interpreter().Visit(field.DefaultValueNode, Context);
                    if (initRes.Error != null) return res.Failure(initRes.Error);
                    fieldValue = initRes.Value ?? fieldValue;
                }

                instance.SetField(field.NameTok.Value?.ToString() ?? "", fieldValue, field.IsPublic, field.DeclarationType);
            }

            var ctor = GetConstructor(positionalArgs, namedArgs);
            if (ctor == null)
            {
                if (Methods.Any(m => m.IsConstructor))
                {
                    return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"No matching constructor found for struct '{StructName}'", Context));
                }

                return res.Success(instance);
            }

            var boundCtor = (BoundStructMethodValue) new BoundStructMethodValue(this, instance, ctor)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            var callRes = boundCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
            if (callRes.Error != null) return callRes;

            return res.Success(instance);
        }

        public sealed override RuntimeValue Copy()
        {
            return new StructTypeValue(StructName, IsPublic, Fields, Methods, Operators)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public sealed override string ToString() => $"<struct {StructName}>";

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

            return null;
        }
    }
}
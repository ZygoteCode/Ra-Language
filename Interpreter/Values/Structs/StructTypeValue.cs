using System.Text;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Special;
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
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.StructType;
        public sealed override bool IsCopy => true;

        public StructTypeValue(
            string structName,
            bool isPublic,
            List<StructFieldDefinitionNode> fields,
            List<StructMethodDefinitionNode> methods,
            List<OperatorDefinitionNode> operators,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null)
        {
            StructName = structName;
            IsPublic = isPublic;
            Fields = fields;
            Methods = methods;
            Operators = operators;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
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

        // M41: hidden-class shape parity with ClassTypeValue (M38). Structs
        // declare their full field set at parse time (no inheritance, no
        // dynamic addition) so the name → dense-index map is computed once
        // and shared by every instance. Enables O(1) field reads through
        // the StructInstance.FieldSlots array, replacing the per-call
        // Dictionary<string, RuntimeValue>.TryGetValue walk on the IC hot
        // path.
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

        public int GetFieldSlotIndex(string name)
        {
            var map = _fieldNameToIndex;
            if (map == null) { BuildFieldShape(); map = _fieldNameToIndex!; }
            return map.TryGetValue(name, out var idx) ? idx : -1;
        }

        private void BuildFieldShape()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            int next = 0;
            foreach (var f in Fields)
            {
                var n = f.NameTok.Value?.ToString();
                if (string.IsNullOrEmpty(n)) continue;
                if (!map.ContainsKey(n)) map[n] = next++;
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

        public sealed override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();

            var instance = (StructInstanceValue) new StructInstanceValue(this)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

            foreach (var field in Fields)
            {
                RuntimeValue fieldValue = NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);

                if (field.DefaultValueNode != null)
                {
                    var initRes = await new Interpreter().Visit(field.DefaultValueNode, Context);
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

            var callRes = await boundCtor.ExecuteWithNamedArgs(positionalArgs, namedArgs);
            if (callRes.Error != null) return callRes;

            return res.Success(instance);
        }

        public sealed override RuntimeValue Copy()
        {
            return new StructTypeValue(StructName, IsPublic, Fields, Methods, Operators, GenericTypeParams, WhereConstraints)
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
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Interpreter.Values.Interfaces
{
    public class InterfaceTypeValue : RuntimeValue
    {
        public string InterfaceName { get; }
        public List<InterfaceMethodSignatureNode> Methods { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public override RuntimeValueType Type => RuntimeValueType.InterfaceType;
        public override bool IsCopy => true;

        public InterfaceTypeValue(
            string interfaceName,
            List<InterfaceMethodSignatureNode> methods,
            List<StructFieldDefinitionNode> fields,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null)
        {
            InterfaceName = interfaceName;
            Methods = methods;
            Fields = fields;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
        }

        public InterfaceMethodSignatureNode? GetMethod(string name)
            => Methods.FirstOrDefault(m => string.Equals(m.NameTok.Value?.ToString(), name, StringComparison.Ordinal));

        public StructFieldDefinitionNode? GetField(string name)
            => Fields.FirstOrDefault(f => string.Equals(f.NameTok.Value?.ToString(), name, StringComparison.Ordinal));

        public bool HasFieldMatching(StructFieldDefinitionNode field, ClassTypeValue classValue)
        {
            var className = classValue.ClassName;
            var fieldName = field.NameTok.Value?.ToString() ?? "";

            var classField = classValue.GetField(fieldName);
            if (classField == null)
                return false;

            if (field.FieldType != null && classField.FieldType != null)
            {
                if (!string.Equals(field.FieldType.Name, classField.FieldType.Name, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        public override RuntimeValue Copy()
            => new InterfaceTypeValue(InterfaceName, Methods, Fields, GenericTypeParams, WhereConstraints)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<interface {InterfaceName}>";
    }
}

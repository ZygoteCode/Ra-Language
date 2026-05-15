using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Interpreter.Values.Traits
{
    public class TraitTypeValue : RuntimeValue
    {
        public string TraitName { get; }
        public bool IsPublic { get; }
        public List<TraitMethodDefinitionNode> Methods { get; }
        public List<StructFieldDefinitionNode> Fields { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public override RuntimeValueType Type => RuntimeValueType.TraitType;
        public override bool IsCopy => true;

        public TraitTypeValue(
            string traitName,
            bool isPublic,
            List<TraitMethodDefinitionNode> methods,
            List<StructFieldDefinitionNode> fields,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null)
        {
            TraitName = traitName;
            IsPublic = isPublic;
            Methods = methods;
            Fields = fields;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
        }

        public IEnumerable<TraitMethodDefinitionNode> GetRequiredMethods()
            => Methods.Where(m => !m.HasBody);

        public IEnumerable<TraitMethodDefinitionNode> GetDefaultMethodsByName(string name)
            => Methods.Where(m => m.HasBody &&
                                 string.Equals(m.NameTok.Value.ToString(), name, StringComparison.Ordinal));

        public StructFieldDefinitionNode? GetField(string name)
            => Fields.FirstOrDefault(f => string.Equals(f.NameTok.Value?.ToString(), name, StringComparison.Ordinal));

        public override RuntimeValue Copy()
            => new TraitTypeValue(TraitName, IsPublic, Methods, Fields, GenericTypeParams, WhereConstraints)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<trait {TraitName}>";
    }
}

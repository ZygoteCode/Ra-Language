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

        public override RuntimeValueType Type => RuntimeValueType.TraitType;
        public override bool IsCopy => true;

        public TraitTypeValue(string traitName, bool isPublic, List<TraitMethodDefinitionNode> methods, List<StructFieldDefinitionNode> fields)
        {
            TraitName = traitName;
            IsPublic = isPublic;
            Methods = methods;
            Fields = fields;
        }

        public IEnumerable<TraitMethodDefinitionNode> GetRequiredMethods()
            => Methods.Where(m => !m.HasBody);

        public IEnumerable<TraitMethodDefinitionNode> GetDefaultMethodsByName(string name)
            => Methods.Where(m => m.HasBody &&
                                 string.Equals(m.NameTok.Value.ToString(), name, StringComparison.Ordinal));

        public StructFieldDefinitionNode? GetField(string name)
            => Fields.FirstOrDefault(f => string.Equals(f.NameTok.Value?.ToString(), name, StringComparison.Ordinal));

        public override RuntimeValue Copy()
            => new TraitTypeValue(TraitName, IsPublic, Methods, Fields)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<trait {TraitName}>";
    }
}
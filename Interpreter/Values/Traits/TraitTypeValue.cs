using RaLanguage.Parser.Nodes.Traits;

namespace RaLanguage.Interpreter.Values.Traits
{
    public class TraitTypeValue : RuntimeValue
    {
        public string TraitName { get; }
        public bool IsPublic { get; }
        public List<TraitMethodDefinitionNode> Methods { get; }

        public override RuntimeValueType Type => RuntimeValueType.TraitType;
        public override bool IsCopy => true;

        public TraitTypeValue(string traitName, bool isPublic, List<TraitMethodDefinitionNode> methods)
        {
            TraitName = traitName;
            IsPublic = isPublic;
            Methods = methods;
        }

        public IEnumerable<TraitMethodDefinitionNode> GetRequiredMethods()
            => Methods.Where(m => !m.HasBody);

        public IEnumerable<TraitMethodDefinitionNode> GetDefaultMethodsByName(string name)
            => Methods.Where(m => m.HasBody &&
                                 string.Equals(m.NameTok.Value.ToString(), name, StringComparison.Ordinal));

        public override RuntimeValue Copy()
            => new TraitTypeValue(TraitName, IsPublic, Methods)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<trait {TraitName}>";
    }
}
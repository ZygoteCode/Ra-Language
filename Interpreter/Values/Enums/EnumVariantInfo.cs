using System.Collections.Generic;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    // Runtime descriptor for a single enum variant. Built once at
    // EnumDefinitionNodeVisitor time and re-used by:
    //   * EnumTypeValue.GetMember (zero-arity → EnumValue; payload → constructor)
    //   * EnumVariantConstructor (validates arity / payload, builds EnumValue)
    //   * pattern matching (variant lookup by name → arity check)
    public sealed class EnumVariantInfo
    {
        public string Name { get; }
        public int Index { get; }
        public System.Int128 UnderlyingValue { get; }
        public IReadOnlyList<TypeDescriptor>? PayloadTypes { get; }

        public bool HasPayload => PayloadTypes != null && PayloadTypes.Count > 0;
        public int Arity => PayloadTypes?.Count ?? 0;

        public EnumVariantInfo(string name, int index, System.Int128 underlyingValue, IReadOnlyList<TypeDescriptor>? payloadTypes)
        {
            Name = name;
            Index = index;
            UnderlyingValue = underlyingValue;
            PayloadTypes = payloadTypes;
        }
    }
}

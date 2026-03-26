using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class EnumTypeValue : RuntimeValue
    {
        public string EnumName { get; }
        public Dictionary<string, Int128> Members { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.EnumType;

        public EnumTypeValue(string enumName, Dictionary<string, Int128> members)
        {
            EnumName = enumName;
            Members = members;
        }

        public bool HasMember(string name) => Members.ContainsKey(name);

        public EnumValue GetMember(string memberName)
        {
            return (EnumValue) new EnumValue(EnumName, memberName, Members[memberName])
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public sealed override RuntimeValue Copy()
        {
            return new EnumTypeValue(EnumName, new Dictionary<string, Int128>(Members))
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public sealed override string ToString() => $"<enum {EnumName}>";
    }
}
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Parser.Nodes.Interfaces;

namespace RaLanguage.Interpreter.Values.Interfaces
{
    public class InterfaceTypeValue : RuntimeValue
    {
        public string InterfaceName { get; }
        public List<InterfaceMethodSignatureNode> Methods { get; }

        public override RuntimeValueType Type => RuntimeValueType.InterfaceType;
        public override bool IsCopy => true;

        public InterfaceTypeValue(string interfaceName, List<InterfaceMethodSignatureNode> methods)
        {
            InterfaceName = interfaceName;
            Methods = methods;
        }

        public InterfaceMethodSignatureNode? GetMethod(string name)
            => Methods.FirstOrDefault(m => string.Equals(m.NameTok.Value?.ToString(), name, StringComparison.Ordinal));

        public override RuntimeValue Copy()
            => new InterfaceTypeValue(InterfaceName, Methods)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);

        public override string ToString() => $"<interface {InterfaceName}>";
    }
}
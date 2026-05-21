using RaLanguage.Types;
using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Parser.Nodes.Classes
{
    public sealed class ExtensionDefinitionNode : AstNode
    {
        public TypeDescriptor TargetType { get; }
        public bool IsPublic { get; }
        public List<FunctionDefinitionNode> Methods { get; }

        public ExtensionDefinitionNode(
            TypeDescriptor targetType,
            bool isPublic,
            List<FunctionDefinitionNode> methods) : base(AstNodeType.ExtensionDefinition)
        {
            TargetType = targetType;
            IsPublic = isPublic;
            Methods = methods;

            PositionStart = methods.Count > 0 ? methods[0].PositionStart : PositionStart;
            PositionEnd = methods.Count > 0 ? methods[^1].PositionEnd : PositionEnd;
        }
    }
}
using RaLanguage.Parser.Nodes.Events;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Classes
{
    // Wraps a StructFieldDefinitionNode with extension-only flags
    // (`static` storage on the type, `lazy` first-touch eval). The
    // field node itself carries declarationType / IsPublic / etc.
    public sealed class ExtensionFieldDeclaration
    {
        public StructFieldDefinitionNode Field { get; }
        public bool IsStaticField { get; }
        public bool IsLazy { get; }

        public ExtensionFieldDeclaration(StructFieldDefinitionNode field, bool isStaticField, bool isLazy)
        {
            Field = field;
            IsStaticField = isStaticField;
            IsLazy = isLazy;
        }
    }

    public sealed class ExtensionDefinitionNode : AstNode
    {
        public TypeDescriptor TargetType { get; }
        public bool IsPublic { get; }
        public List<FunctionDefinitionNode> Methods { get; }
        public List<PropertyDefinitionNode> Properties { get; }
        public List<OperatorDefinitionNode> Operators { get; }
        public List<EventDefinitionNode> Events { get; }
        public List<ExtensionFieldDeclaration> Fields { get; }
        public List<(FunctionDefinitionNode Method, bool IsSetter)> Indexers { get; }
        public bool IsSealed { get; set; }

        public ExtensionDefinitionNode(
            TypeDescriptor targetType,
            bool isPublic,
            List<FunctionDefinitionNode> methods,
            List<PropertyDefinitionNode>? properties = null,
            List<OperatorDefinitionNode>? operators = null,
            List<EventDefinitionNode>? events = null,
            List<(FunctionDefinitionNode, bool)>? indexers = null,
            List<ExtensionFieldDeclaration>? fields = null,
            bool isSealed = false) : base(AstNodeType.ExtensionDefinition)
        {
            TargetType = targetType;
            IsPublic = isPublic;
            Methods = methods;
            Properties = properties ?? new List<PropertyDefinitionNode>();
            Operators = operators ?? new List<OperatorDefinitionNode>();
            Events = events ?? new List<EventDefinitionNode>();
            Indexers = indexers ?? new List<(FunctionDefinitionNode, bool)>();
            Fields = fields ?? new List<ExtensionFieldDeclaration>();
            IsSealed = isSealed;

            if (methods.Count > 0)
            {
                PositionStart = methods[0].PositionStart;
                PositionEnd = methods[^1].PositionEnd;
            }
            if (Properties.Count > 0)
            {
                if (methods.Count == 0) PositionStart = Properties[0].PositionStart;
                PositionEnd = Properties[^1].PositionEnd;
            }
            if (Operators.Count > 0)
            {
                if (methods.Count == 0 && Properties.Count == 0) PositionStart = Operators[0].PositionStart;
                PositionEnd = Operators[^1].PositionEnd;
            }
            if (Events.Count > 0)
            {
                if (methods.Count == 0 && Properties.Count == 0 && Operators.Count == 0)
                    PositionStart = Events[0].PositionStart;
                PositionEnd = Events[^1].PositionEnd;
            }
            if (Fields.Count > 0)
            {
                if (methods.Count == 0 && Properties.Count == 0 && Operators.Count == 0 && Events.Count == 0)
                    PositionStart = Fields[0].Field.PositionStart;
                PositionEnd = Fields[^1].Field.PositionEnd;
            }
        }
    }
}

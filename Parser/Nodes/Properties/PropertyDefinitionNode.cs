using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Properties
{
    // A class/struct/record/interface/trait member of the form
    //
    //     [modifiers] prop NAME[: TYPE] [= default] [body]
    //
    // where body is either
    //
    //     { (accessor (';' | NEWLINE))+ }
    //
    // or the readonly shorthand
    //
    //     '=>' expression
    //
    // (the shorthand is equivalent to `{ get => expression }`).
    //
    // Held as an in-order list of accessors; visitor builds the
    // PropertyDescriptor by classifying them. Multiple accessors of the
    // same kind are a parse error (caught in the parser, not here).
    public sealed class PropertyDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public TypeDescriptor? PropertyType { get; }
        public AstNode? DefaultValueNode { get; }
        public List<PropertyAccessorNode> Accessors { get; }

        public bool IsPublic { get; }
        public bool IsStatic { get; }
        public bool IsAbstract { get; }
        public bool IsOverride { get; }
        public bool IsLazy { get; }

        public PropertyDefinitionNode(
            Token nameTok,
            TypeDescriptor? propertyType,
            AstNode? defaultValueNode,
            List<PropertyAccessorNode> accessors,
            bool isPublic,
            bool isStatic,
            bool isAbstract,
            bool isOverride,
            bool isLazy) : base(AstNodeType.PropertyDefinition)
        {
            NameTok = nameTok;
            PropertyType = propertyType;
            DefaultValueNode = defaultValueNode;
            Accessors = accessors;
            IsPublic = isPublic;
            IsStatic = isStatic;
            IsAbstract = isAbstract;
            IsOverride = isOverride;
            IsLazy = isLazy;

            PositionStart = nameTok.PositionStart;
            if (accessors.Count > 0)
                PositionEnd = accessors[^1].PositionEnd;
            else if (defaultValueNode != null)
                PositionEnd = defaultValueNode.PositionEnd;
            else
                PositionEnd = nameTok.PositionEnd;
        }
    }
}

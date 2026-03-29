using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Structs
{
    public class StructFieldDefinitionNode : AstNode
    {
        public bool IsPublic { get; }
        public bool IsStatic { get; }
        public Token NameTok { get; }
        public TypeDescriptor? FieldType { get; }
        public AstNode? DefaultValueNode { get; }

        public StructFieldDefinitionNode(bool isPublic, Token nameTok, TypeDescriptor? fieldType, AstNode? defaultValueNode, bool isStatic) : base(AstNodeType.StructFieldDefinition)
        {
            IsPublic = isPublic;
            IsStatic = isStatic;
            NameTok = nameTok;
            FieldType = fieldType;
            DefaultValueNode = defaultValueNode;
            PositionStart = nameTok.PositionStart;
            PositionEnd = defaultValueNode != null ? defaultValueNode.PositionEnd : nameTok.PositionEnd;
        }
    }
}
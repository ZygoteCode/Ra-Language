using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Records
{
    // A primary-constructor parameter of a record. Every primary field
    // becomes a public, immutable instance field on the record instance
    // unless explicitly marked `mut`. Visibility defaults to public —
    // records exist to be read from the outside, so opting in to `pub`
    // for every field would just be noise. Use `priv` to hide.
    public sealed class RecordPrimaryFieldNode : AstNode
    {
        public Token NameTok { get; }
        public TypeDescriptor? FieldType { get; }
        public AstNode? DefaultValueNode { get; }
        public bool IsPublic { get; }
        public bool IsMutable { get; }

        public RecordPrimaryFieldNode(
            Token nameTok,
            TypeDescriptor? fieldType,
            AstNode? defaultValueNode,
            bool isPublic,
            bool isMutable) : base(AstNodeType.RecordPrimaryField)
        {
            NameTok = nameTok;
            FieldType = fieldType;
            DefaultValueNode = defaultValueNode;
            IsPublic = isPublic;
            IsMutable = isMutable;
            PositionStart = nameTok.PositionStart;
            PositionEnd = defaultValueNode != null ? defaultValueNode.PositionEnd : (fieldType != null ? nameTok.PositionEnd : nameTok.PositionEnd);
        }
    }
}

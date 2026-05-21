using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Interfaces
{
    public sealed class InterfaceMethodSignatureNode : AstNode
    {
        public Token NameTok { get; }
        public List<Token> ArgNameToks { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public TypeDescriptor? ReturnType { get; }

        public InterfaceMethodSignatureNode(
            Token nameTok,
            List<Token> argNameToks,
            List<TypeDescriptor?> argTypes,
            TypeDescriptor? returnType)
            : base(AstNodeType.InterfaceMethodSignature)
        {
            NameTok = nameTok;
            ArgNameToks = argNameToks;
            ArgTypes = argTypes;
            ReturnType = returnType;
            PositionStart = nameTok.PositionStart;
            PositionEnd = returnType?.Name != null ? nameTok.PositionEnd : nameTok.PositionEnd;
        }
    }
}
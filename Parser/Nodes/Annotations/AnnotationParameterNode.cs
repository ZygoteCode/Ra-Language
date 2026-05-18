using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Annotations
{
    public class AnnotationParameterNode
    {
        public Token NameTok { get; }
        public string Name => NameTok.Value?.ToString() ?? string.Empty;
        public TypeDescriptor? DeclaredType { get; }
        public AstNode? DefaultValueNode { get; }
        public bool IsVarArgs { get; }

        public AnnotationParameterNode(Token nameTok, TypeDescriptor? declaredType, AstNode? defaultValueNode, bool isVarArgs = false)
        {
            NameTok = nameTok;
            DeclaredType = declaredType;
            DefaultValueNode = defaultValueNode;
            IsVarArgs = isVarArgs;
        }
    }
}

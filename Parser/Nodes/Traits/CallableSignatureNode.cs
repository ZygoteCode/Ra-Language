using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Traits
{
    public class CallableSignatureNode : AstNode
    {
        public List<Token> ArgNameToks { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public List<AstNode?> ParamDefaults { get; }
        public bool HasVarArgs { get; }
        public Token? VarArgNameTok { get; }
        public TypeDescriptor? VarArgType { get; }
        public TypeDescriptor? ReturnType { get; }

        public CallableSignatureNode(
            List<Token> argNameToks,
            List<TypeDescriptor?> argTypes,
            List<AstNode?> paramDefaults,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType,
            TypeDescriptor? returnType
        ) : base(AstNodeType.CallableSignature)
        {
            ArgNameToks = argNameToks;
            ArgTypes = argTypes;
            ParamDefaults = paramDefaults;
            HasVarArgs = hasVarArgs;
            VarArgNameTok = varArgNameTok;
            VarArgType = varArgType;
            ReturnType = returnType;
        }
    }
}
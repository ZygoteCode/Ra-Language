using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Functions
{
    public class FunctionDefinitionNode : AstNode
    {
        public Token? VarNameTok { get; }
        public List<Token> ArgNameToks { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public List<AstNode?> ParamDefaults { get; }
        public bool HasVarArgs { get; }
        public Token? VarArgNameTok { get; }
        public TypeDescriptor? VarArgType { get; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode BodyNode { get; }
        public bool ShouldAutoReturn { get; }
        public List<string> GenericTypeParams { get; }
        public bool IsPublic { get; }
        public bool IsConstructor { get; }

        public FunctionDefinitionNode(
            Token? varNameTok,
            List<Token> argNameToks,
            List<TypeDescriptor?> argTypes,
            List<AstNode?> paramDefaults,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType,
            TypeDescriptor? returnType,
            AstNode bodyNode,
            bool shouldAutoReturn,
            List<string>? genericTypeParams = null,
            bool isPublic = false,
            bool isConstructor = false
        ) : base(AstNodeType.FunctionDefinition)
        {
            VarNameTok = varNameTok;
            ArgNameToks = argNameToks ?? new List<Token>();
            ArgTypes = argTypes ?? new List<TypeDescriptor?>();
            ParamDefaults = paramDefaults ?? new List<AstNode?>();
            HasVarArgs = hasVarArgs;
            VarArgNameTok = varArgNameTok;
            VarArgType = varArgType;
            ReturnType = returnType;
            BodyNode = bodyNode;
            ShouldAutoReturn = shouldAutoReturn;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            IsPublic = isPublic;
            IsConstructor = isConstructor;

            if (varNameTok != null) PositionStart = varNameTok.Value.PositionStart;
            else if (ArgNameToks.Count > 0) PositionStart = ArgNameToks[0].PositionStart;
            else PositionStart = bodyNode.PositionStart;

            PositionEnd = bodyNode.PositionEnd;
        }
    }
}
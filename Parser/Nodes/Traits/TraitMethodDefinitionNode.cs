using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Traits
{
    public class TraitMethodDefinitionNode : AstNode, ICallableMethodDefinition
    {
        public Token? NameTok { get; }
        public List<Token> ArgNameToks { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public List<AstNode?> ParamDefaults { get; }
        public bool HasVarArgs { get; }
        public Token? VarArgNameTok { get; }
        public TypeDescriptor? VarArgType { get; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode? BodyNode { get; }
        public bool ShouldAutoReturn { get; }
        public bool IsAbstract { get; }

        public Token? NameTokAlias => NameTok;
        public bool HasBody => BodyNode != null;
        public bool IsOverride => false;
        public bool IsConstructor => false;

        public TraitMethodDefinitionNode(
            Token nameTok,
            List<Token> argNameToks,
            List<TypeDescriptor?> argTypes,
            List<AstNode?> paramDefaults,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType,
            TypeDescriptor? returnType,
            AstNode? bodyNode,
            bool shouldAutoReturn,
            bool isAbstract)
            : base(AstNodeType.TraitMethodDefinition)
        {
            NameTok = nameTok;
            ArgNameToks = argNameToks ?? new List<Token>();
            ArgTypes = argTypes ?? new List<TypeDescriptor?>();
            ParamDefaults = paramDefaults ?? new List<AstNode?>();
            HasVarArgs = hasVarArgs;
            VarArgNameTok = varArgNameTok;
            VarArgType = varArgType;
            ReturnType = returnType;
            BodyNode = bodyNode;
            ShouldAutoReturn = shouldAutoReturn;
            IsAbstract = isAbstract;

            PositionStart = nameTok.PositionStart;
            PositionEnd = bodyNode?.PositionEnd ?? nameTok.PositionEnd;
        }
    }
}
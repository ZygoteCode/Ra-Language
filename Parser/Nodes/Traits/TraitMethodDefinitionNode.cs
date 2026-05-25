using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Traits
{
    public sealed class TraitMethodDefinitionNode : AstNode, ICallableMethodDefinition
    {
        // M18: resolver-populated metadata for IR compilation.
        public int FrameId = -1;
        public RaLanguage.Interpreter.Pipeline.BindingId[]? ParamBindings;
        public RaLanguage.Interpreter.IR.RaFunction? CompiledBody;
        public bool IrCompileTried;

        public Token? NameTok { get; }
        public List<Token> ArgNameToks { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public List<bool> IsRefParams { get; }
        public List<AstNode?> ParamDefaults { get; }
        public bool HasVarArgs { get; }
        public Token? VarArgNameTok { get; }
        public TypeDescriptor? VarArgType { get; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode? BodyNode { get; }
        public bool ShouldAutoReturn { get; }
        public bool IsAbstract { get; }
        public bool IsAsync { get; set; }
        public bool IsAsyncStream { get; set; }

        public Token? NameTokAlias => NameTok;
        public bool HasBody => BodyNode != null;
        public bool IsOverride => false;
        public bool IsConstructor => false;

        public TraitMethodDefinitionNode(
            Token nameTok,
            List<Token> argNameToks,
            List<TypeDescriptor?> argTypes,
            List<bool> isRefParams,
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
            IsRefParams = isRefParams ?? new List<bool>();
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
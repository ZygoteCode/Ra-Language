using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Structs
{
    public class StructMethodDefinitionNode : AstNode
    {
        // M18: Resolver populates these so the IR compiler can lower the
        // method body just like a regular FunctionDefinitionNode (slot
        // opcodes for params + locals declared inside the body).
        public int FrameId = -1;
        public RaLanguage.Interpreter.Pipeline.BindingId[]? ParamBindings;
        public RaLanguage.Interpreter.IR.RaFunction? CompiledBody;
        public bool IrCompileTried;

        public bool IsPublic { get; }
        public bool IsConstructor { get; }
        public Token NameTok { get; }
        public List<Token> ArgNameToks { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public List<bool> IsRefParams { get; }
        public List<AstNode?> ParamDefaults { get; }
        public bool HasVarArgs { get; }
        public Token? VarArgNameTok { get; }
        public TypeDescriptor? VarArgType { get; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode BodyNode { get; }
        public bool ShouldAutoReturn { get; }
        public bool IsAsync { get; set; }
        public bool IsAsyncStream { get; set; }

        public StructMethodDefinitionNode(
            bool isPublic,
            bool isConstructor,
            Token nameTok,
            List<Token> argNameToks,
            List<TypeDescriptor?> argTypes,
            List<bool> isRefParams,
            List<AstNode?> paramDefaults,
            bool hasVarArgs,
            Token? varArgNameTok,
            TypeDescriptor? varArgType,
            TypeDescriptor? returnType,
            AstNode bodyNode,
            bool shouldAutoReturn) : base(AstNodeType.StructMethodDefinition)
        {
            IsPublic = isPublic;
            IsConstructor = isConstructor;
            NameTok = nameTok;
            ArgNameToks = argNameToks;
            ArgTypes = argTypes;
            IsRefParams = isRefParams ?? new List<bool>();
            ParamDefaults = paramDefaults;
            HasVarArgs = hasVarArgs;
            VarArgNameTok = varArgNameTok;
            VarArgType = varArgType;
            ReturnType = returnType;
            BodyNode = bodyNode;
            ShouldAutoReturn = shouldAutoReturn;

            PositionStart = nameTok.PositionStart;
            PositionEnd = bodyNode.PositionEnd;
        }
    }
}

using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Classes
{
    public sealed class OperatorDefinitionNode : AstNode
    {
        // M18: resolver-populated metadata for IR compilation.
        public int FrameId = -1;
        public RaLanguage.Interpreter.Pipeline.BindingId[]? ParamBindings;
        public RaLanguage.Interpreter.IR.RaFunction? CompiledBody;
        public bool IrCompileTried;

        public bool IsPublic { get; }
        public bool IsOverride { get; }
        public bool IsStatic { get; }
        public Token OperatorTok { get; }
        public Token ArgNameTok { get; }
        public TypeDescriptor? ArgType { get; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode BodyNode { get; }
        public bool ShouldAutoReturn { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public OperatorDefinitionNode(
            bool isPublic,
            bool isOverride,
            bool isStatic,
            Token operatorTok,
            Token argNameTok,
            TypeDescriptor? argType,
            TypeDescriptor? returnType,
            AstNode bodyNode,
            bool shouldAutoReturn,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null) : base(AstNodeType.OperatorDefinition)
        {
            IsPublic = isPublic;
            IsOverride = isOverride;
            IsStatic = isStatic;
            OperatorTok = operatorTok;
            ArgNameTok = argNameTok;
            ArgType = argType;
            ReturnType = returnType;
            BodyNode = bodyNode;
            ShouldAutoReturn = shouldAutoReturn;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();

            PositionStart = operatorTok.PositionStart;
            PositionEnd = bodyNode.PositionEnd;
        }
    }
}

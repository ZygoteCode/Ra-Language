using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Classes
{
    public class OperatorDefinitionNode : AstNode
    {
        public bool IsPublic { get; }
        public bool IsOverride { get; }
        public bool IsStatic { get; }
        public Token OperatorTok { get; }
        public Token ArgNameTok { get; }
        public TypeDescriptor? ArgType { get; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode BodyNode { get; }
        public bool ShouldAutoReturn { get; }

        public OperatorDefinitionNode(
            bool isPublic,
            bool isOverride,
            bool isStatic,
            Token operatorTok,
            Token argNameTok,
            TypeDescriptor? argType,
            TypeDescriptor? returnType,
            AstNode bodyNode,
            bool shouldAutoReturn) : base(AstNodeType.OperatorDefinition)
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

            PositionStart = operatorTok.PositionStart;
            PositionEnd = bodyNode.PositionEnd;
        }
    }
}

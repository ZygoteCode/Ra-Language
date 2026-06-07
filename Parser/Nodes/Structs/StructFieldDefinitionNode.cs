using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Structs
{
    public sealed class StructFieldDefinitionNode : AstNode
    {
        public bool IsPublic { get; }
        public bool IsStatic { get; }
        public bool IsAbstract { get; }
        public bool IsOverride { get; }
        public Token NameTok { get; }
        public TypeDescriptor? FieldType { get; }
        public AstNode? DefaultValueNode { get; }
        public VariableDeclarationType DeclarationType { get; }

        // L10: resolver-populated metadata for IR compilation of a NON-CONST field
        // default initializer (mirrors PropertyDefinitionNode's lazy-default fields).
        // The initializer is compiled to a self-bound 0-arg RaFunction run via the
        // VM eagerly at CONSTRUCTION (StructTypeValue / ClassTypeValue field-init);
        // `self` is the implicit slot 0. Const defaults fold to DefaultConst instead.
        public int DefaultFrameId { get; set; } = -1;
        public RaLanguage.Interpreter.Pipeline.BindingId[]? DefaultParamBindings { get; set; }
        public RaLanguage.Interpreter.IR.RaFunction? DefaultCompiledBody { get; set; }
        public bool DefaultIrCompileTried { get; set; }

        public StructFieldDefinitionNode(
            bool isPublic, 
            Token nameTok, 
            TypeDescriptor? fieldType, 
            AstNode? defaultValueNode, 
            bool isStatic,
            bool isAbstract = false,
            bool isOverride = false,
            VariableDeclarationType declarationType = VariableDeclarationType.VARIABLE) : base(AstNodeType.StructFieldDefinition)
        {
            IsPublic = isPublic;
            IsStatic = isStatic;
            IsAbstract = isAbstract;
            IsOverride = isOverride;
            NameTok = nameTok;
            FieldType = fieldType;
            DefaultValueNode = defaultValueNode;
            DeclarationType = declarationType;
            PositionStart = nameTok.PositionStart;
            PositionEnd = defaultValueNode != null ? defaultValueNode.PositionEnd : nameTok.PositionEnd;
        }
    }
}
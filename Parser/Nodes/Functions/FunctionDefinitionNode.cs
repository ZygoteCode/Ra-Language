using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Functions
{
    public class FunctionDefinitionNode : AstNode, ICallableMethodDefinition
    {
        public Token? VarNameTok { get; }
        public List<Token> ArgNameToks { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public List<bool> IsRefParams { get; }
        public List<AstNode?> ParamDefaults { get; }
        public List<List<AnnotationApplicationNode>?> ParamAnnotations { get; }
        public bool HasVarArgs { get; }
        public Token? VarArgNameTok { get; }
        public TypeDescriptor? VarArgType { get; }
        public List<AnnotationApplicationNode>? VarArgAnnotations { get; set; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode? BodyNode { get; }
        public bool ShouldAutoReturn { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }
        public bool IsPublic { get; }
        public bool IsConstructor { get; }
        public bool IsOverride { get; }
        public bool IsAbstract { get; }
        public bool IsStatic { get; }

        Token? ICallableMethodDefinition.NameTok => VarNameTok;
        List<bool> ICallableMethodDefinition.IsRefParams => IsRefParams;
        bool ICallableMethodDefinition.HasBody => BodyNode != null && !IsAbstract;
        bool ICallableMethodDefinition.IsAbstract => IsAbstract;
        bool ICallableMethodDefinition.IsOverride => IsOverride;
        bool ICallableMethodDefinition.IsConstructor => IsConstructor;
        bool ICallableMethodDefinition.ShouldAutoReturn => ShouldAutoReturn;
        AstNode? ICallableMethodDefinition.BodyNode => BodyNode;

        public FunctionDefinitionNode(
            Token? varNameTok,
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
            List<string>? genericTypeParams = null,
            bool isPublic = false,
            bool isConstructor = false,
            bool isOverride = false,
            bool isAbstract = false,
            bool isStatic = false,
            List<WhereConstraintNode>? whereConstraints = null,
            List<List<AnnotationApplicationNode>?>? paramAnnotations = null
        ) : base(AstNodeType.FunctionDefinition)
        {
            VarNameTok = varNameTok;
            ArgNameToks = argNameToks ?? new List<Token>();
            ArgTypes = argTypes ?? new List<TypeDescriptor?>();
            IsRefParams = isRefParams ?? new List<bool>();
            ParamDefaults = paramDefaults ?? new List<AstNode?>();
            ParamAnnotations = paramAnnotations ?? new List<List<AnnotationApplicationNode>?>();
            HasVarArgs = hasVarArgs;
            VarArgNameTok = varArgNameTok;
            VarArgType = varArgType;
            ReturnType = returnType;
            BodyNode = bodyNode;
            ShouldAutoReturn = shouldAutoReturn;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            IsPublic = isPublic;
            IsConstructor = isConstructor;
            IsOverride = isOverride;
            IsAbstract = isAbstract || bodyNode == null;
            IsStatic = isStatic;

            if (varNameTok != null) PositionStart = varNameTok.Value.PositionStart;
            else if (ArgNameToks.Count > 0) PositionStart = ArgNameToks[0].PositionStart;
            else if (bodyNode != null) PositionStart = bodyNode.PositionStart;

            PositionEnd = bodyNode?.PositionEnd ?? PositionStart;
        }
    }
}

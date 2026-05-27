using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Functions
{
    // Named alias for a structural function type:
    //
    //   delegate Predicate<T> = fn(T) -> bool;
    //   delegate Action       = fn();
    //   delegate Handler<T>   = fn(T) -> void;
    //
    // Two parse shapes are accepted, both produce the same node:
    //
    //   delegate Name [<TypeParams>] = <FnType>;            // explicit form
    //   delegate Name [<TypeParams>] ( ParamTypes ) -> Ret  // short form
    //
    // The visitor registers the alias in the active SymbolTable as a
    // DelegateTypeValue carrying a structural TypeDescriptor. Uses inside
    // type positions (`var x: Name<int>` etc.) resolve via the same name
    // lookup the class/struct/enum machinery uses.
    public sealed class DelegateDefinitionNode : AstNode
    {
        public Token NameTok { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }
        public TypeDescriptor SignatureType { get; }
        public bool IsPublic { get; }

        public DelegateDefinitionNode(
            Token nameTok,
            List<string> genericTypeParams,
            List<WhereConstraintNode> whereConstraints,
            TypeDescriptor signatureType,
            bool isPublic
        ) : base(AstNodeType.DelegateDefinition)
        {
            NameTok = nameTok;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();
            SignatureType = signatureType;
            IsPublic = isPublic;

            PositionStart = nameTok.PositionStart;
            PositionEnd = nameTok.PositionEnd;
        }
    }
}

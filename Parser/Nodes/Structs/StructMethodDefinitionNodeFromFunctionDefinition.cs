using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Parser.Nodes.Structs
{
    public sealed class StructMethodDefinitionNodeFromFunctionDefinition : StructMethodDefinitionNode
    {
        public StructMethodDefinitionNodeFromFunctionDefinition(FunctionDefinitionNode fn)
            : base(
                fn.IsPublic,
                fn.IsConstructor,
                fn.VarNameTok!.Value,
                fn.ArgNameToks,
                fn.ArgTypes,
                fn.IsRefParams,
                fn.ParamDefaults,
                fn.HasVarArgs,
                fn.VarArgNameTok,
                fn.VarArgType,
                fn.ReturnType,
                fn.BodyNode,
                fn.ShouldAutoReturn
            )
        {
            Annotations = fn.Annotations;
            IsAsync = fn.IsAsync;
            IsAsyncStream = fn.IsAsyncStream;
        }
    }
}
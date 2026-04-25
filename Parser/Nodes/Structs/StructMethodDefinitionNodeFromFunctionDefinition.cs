using RaLanguage.Parser.Nodes.Functions;

namespace RaLanguage.Parser.Nodes.Structs
{
    public class StructMethodDefinitionNodeFromFunctionDefinition : StructMethodDefinitionNode
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
        }
    }
}
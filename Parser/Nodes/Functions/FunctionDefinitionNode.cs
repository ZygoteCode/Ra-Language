using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Functions
{
    public class FunctionDefinitionNode : AstNode
    {
        public Token? VarNameTok { get; }
        public List<Token> ArgNameToks { get; }
        public List<TypeDescriptor?> ArgTypes { get; }
        public TypeDescriptor? ReturnType { get; }
        public AstNode BodyNode { get; }
        public bool ShouldAutoReturn { get; }

        public FunctionDefinitionNode(Token? varNameTok, List<Token> argNameToks, List<TypeDescriptor?> argTypes, TypeDescriptor? returnType, AstNode bodyNode, bool shouldAutoReturn) : base(AstNodeType.FunctionDefinition)
        {
            VarNameTok = varNameTok;
            ArgNameToks = argNameToks;
            ArgTypes = argTypes ?? new List<TypeDescriptor?>();
            ReturnType = returnType;
            BodyNode = bodyNode;
            ShouldAutoReturn = shouldAutoReturn;

            if (varNameTok != null) PositionStart = varNameTok.PositionStart;
            else if (argNameToks.Count > 0) PositionStart = argNameToks[0].PositionStart;
            else PositionStart = bodyNode.PositionStart;

            PositionEnd = bodyNode.PositionEnd;
        }
    }
}
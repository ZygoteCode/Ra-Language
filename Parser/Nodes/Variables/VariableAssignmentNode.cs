using RaLanguage.Interpreter.Pipeline;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Lexer.Tokens;

namespace RaLanguage.Parser.Nodes.Variables
{
    public sealed class VariableAssignmentNode : AstNode
    {
        public Token VarNameTok { get; }
        public Token AssignmentToken { get; }
        public AstNode ValueNode { get; }

        public string Name { get; }

        internal SymbolLookupCache? LookupCache;

        // See VariableAccessNode for the Resolver contract.
        public BindingId Binding = BindingId.Unresolved;
        public BindingKind BindingKind = BindingKind.Unresolved;

        public VariableAssignmentNode(Token varNameTok, Token assignmentToken, AstNode valueNode) : base(AstNodeType.VariableAssignment)
        {
            VarNameTok = varNameTok;
            AssignmentToken = assignmentToken;
            ValueNode = valueNode;
            Name = varNameTok.Value?.ToString() ?? "";
            PositionStart = varNameTok.PositionStart;
            PositionEnd = valueNode.PositionEnd;
        }
    }
}
using RaLanguage.Lexer.Tokens;
using RaLanguage.Types;

namespace RaLanguage.Parser.Nodes.Special
{
    public class WhereConstraintNode
    {
        public Token ParameterNameTok { get; }
        public string ParameterName => ParameterNameTok.Value?.ToString() ?? "";
        public TypeDescriptor ConstraintType { get; }

        public WhereConstraintNode(Token parameterNameTok, TypeDescriptor constraintType)
        {
            ParameterNameTok = parameterNameTok;
            ConstraintType = constraintType;
        }

        public override string ToString() => $"{ParameterName}: {ConstraintType}";
    }
}

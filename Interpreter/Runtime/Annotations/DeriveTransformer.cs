using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public static class DeriveTransformer
    {
        private static readonly Position SyntheticPos = new Position(0, 0, 0, "<derive>", string.Empty);

        public static void Apply(AstNode root) => Walk(root);

        private static void Walk(AstNode node)
        {
            switch (node)
            {
                case ScopeNode scope:
                    foreach (var n in scope.Nodes) Walk(n);
                    break;
                case ClassDefinitionNode cls:
                    ApplyToClass(cls);
                    foreach (var m in cls.Methods)
                        if (m.BodyNode != null) Walk(m.BodyNode);
                    break;
                case FunctionDefinitionNode fn:
                    if (fn.BodyNode != null) Walk(fn.BodyNode);
                    break;
            }
        }

        private static void ApplyToClass(ClassDefinitionNode cls)
        {
            if (cls.Annotations == null) return;

            var consumed = new List<AnnotationApplicationNode>();
            foreach (var app in cls.Annotations)
            {
                if (app.Name != "derive") continue;
                consumed.Add(app);

                foreach (var flagNode in app.PositionalArgs)
                {
                    string? flag = flagNode switch
                    {
                        Parser.Nodes.Variables.VariableAccessNode var => var.VarNameTok.Value?.ToString(),
                        Parser.Nodes.Primitives.StringNode strN when strN.Parts.Count == 1 && strN.Parts[0] is Parser.Nodes.Primitives.StringTextNode stxt => stxt.Text,
                        _ => null
                    };
                    if (flag == null) continue;
                    switch (flag)
                    {
                        case "Equals": InjectEquals(cls); break;
                        case "Hash": InjectHash(cls); break;
                        case "ToString": InjectToString(cls); break;
                        case "Copy": InjectCopy(cls); break;
                    }
                }
            }

            foreach (var c in consumed) cls.Annotations.Remove(c);
        }

        private static bool HasMethod(ClassDefinitionNode cls, string name)
            => cls.Methods.Any(m => string.Equals(m.VarNameTok?.Value?.ToString(), name, System.StringComparison.Ordinal));

        private static Token TokId(string name) => new Token(TokenType.IDENTIFIER, name, SyntheticPos);
        private static Token TokKw(Keyword kw) => new Token(TokenType.KEYWORD, kw, SyntheticPos);
        private static Token TokOp(TokenType t) => new Token(t, null, SyntheticPos);

        private static FunctionDefinitionNode BuildMethod(
            string name,
            List<Token> argNames,
            List<TypeDescriptor?> argTypes,
            TypeDescriptor returnType,
            AstNode body)
        {
            var scopeBody = new ScopeNode(new List<AstNode> { body }, SyntheticPos, SyntheticPos);
            return new FunctionDefinitionNode(
                varNameTok: TokId(name),
                argNameToks: argNames,
                argTypes: argTypes,
                isRefParams: argNames.Select(_ => false).ToList(),
                paramDefaults: argNames.Select(_ => (AstNode?)null).ToList(),
                hasVarArgs: false,
                varArgNameTok: null,
                varArgType: null,
                returnType: returnType,
                bodyNode: scopeBody,
                shouldAutoReturn: false,
                genericTypeParams: null,
                isPublic: true,
                isConstructor: false,
                isOverride: false,
                isAbstract: false,
                isStatic: false,
                whereConstraints: null,
                paramAnnotations: null
            );
        }

        private static AstNode SelfField(string field)
            => new MemberAccessNode(new SelfNode(SyntheticPos, SyntheticPos), TokId(field));

        private static AstNode OtherField(Token paramTok, string field)
            => new MemberAccessNode(new Parser.Nodes.Variables.VariableAccessNode(paramTok), TokId(field));

        private static void InjectEquals(ClassDefinitionNode cls)
        {
            if (HasMethod(cls, "equals")) return;

            var otherTok = TokId("other");
            var fields = cls.Fields.Where(f => !f.IsStatic).ToList();

            AstNode body = new Parser.Nodes.Primitives.BooleanNode(TokKw(Keyword.True));
            foreach (var f in fields)
            {
                var fname = f.NameTok.Value?.ToString() ?? "";
                var cmp = new Parser.Nodes.Operations.BinaryOperationNode(
                    SelfField(fname), TokOp(TokenType.EE), OtherField(otherTok, fname));
                body = new Parser.Nodes.Operations.BinaryOperationNode(body, TokKw(Keyword.And), cmp);
            }

            var ret = new ReturnNode(body, SyntheticPos, SyntheticPos);
            cls.Methods.Add(BuildMethod(
                "equals",
                new List<Token> { otherTok },
                new List<TypeDescriptor?> { new TypeDescriptor(cls.NameTok.Value?.ToString() ?? "") },
                new TypeDescriptor("bool"),
                ret));
        }

        private static void InjectHash(ClassDefinitionNode cls)
        {
            if (HasMethod(cls, "hash")) return;

            var fields = cls.Fields.Where(f => !f.IsStatic).ToList();
            int count = fields.Count;

            var body = new ReturnNode(
                new Parser.Nodes.Primitives.NumberNode(new Token(TokenType.INT, count.ToString(), SyntheticPos)),
                SyntheticPos, SyntheticPos);

            cls.Methods.Add(BuildMethod(
                "hash",
                new List<Token>(),
                new List<TypeDescriptor?>(),
                new TypeDescriptor("int"),
                body));
        }

        private static void InjectToString(ClassDefinitionNode cls)
        {
            if (HasMethod(cls, "to_string")) return;

            var className = cls.NameTok.Value?.ToString() ?? "";
            var parts = new List<AstNode>
            {
                new Parser.Nodes.Primitives.StringTextNode(className + "(", SyntheticPos, SyntheticPos)
            };

            bool first = true;
            foreach (var f in cls.Fields)
            {
                if (f.IsStatic) continue;
                var fname = f.NameTok.Value?.ToString() ?? "";
                if (!first) parts.Add(new Parser.Nodes.Primitives.StringTextNode(", ", SyntheticPos, SyntheticPos));
                first = false;
                parts.Add(new Parser.Nodes.Primitives.StringTextNode(fname + "=", SyntheticPos, SyntheticPos));
                parts.Add(SelfField(fname));
            }
            parts.Add(new Parser.Nodes.Primitives.StringTextNode(")", SyntheticPos, SyntheticPos));

            var strNode = new Parser.Nodes.Primitives.StringNode(parts, SyntheticPos, SyntheticPos);
            var ret = new ReturnNode(strNode, SyntheticPos, SyntheticPos);

            cls.Methods.Add(BuildMethod(
                "to_string",
                new List<Token>(),
                new List<TypeDescriptor?>(),
                new TypeDescriptor("string"),
                ret));
        }

        private static void InjectCopy(ClassDefinitionNode cls)
        {
            if (HasMethod(cls, "copy_of")) return;

            var className = cls.NameTok.Value?.ToString() ?? "";
            var argNodes = new List<ArgumentNode>();
            foreach (var f in cls.Fields)
            {
                if (f.IsStatic) continue;
                var fname = f.NameTok.Value?.ToString() ?? "";
                argNodes.Add(new ArgumentNode(null, SelfField(fname), false));
            }

            var callee = new Parser.Nodes.Variables.VariableAccessNode(TokId(className));
            var call = new FunctionCallNode(callee, argNodes, null);
            var ret = new ReturnNode(call, SyntheticPos, SyntheticPos);

            cls.Methods.Add(BuildMethod(
                "copy_of",
                new List<Token>(),
                new List<TypeDescriptor?>(),
                new TypeDescriptor(className),
                ret));
        }
    }
}

using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Variables;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public sealed class StaticAnalyzerDiagnostic
    {
        public string Message { get; }
        public RaLanguage.Lexer.Position PositionStart { get; }
        public RaLanguage.Lexer.Position PositionEnd { get; }
        public StaticAnalyzerDiagnostic(string msg, RaLanguage.Lexer.Position s, RaLanguage.Lexer.Position e)
        {
            Message = msg; PositionStart = s; PositionEnd = e;
        }
        public override string ToString() => $"[StaticAnalyzer] {Message}";
    }

    public static class StaticAnalyzer
    {
        public static List<StaticAnalyzerDiagnostic> Analyze(AstNode root, SymbolTable symbols)
        {
            var diagnostics = new List<StaticAnalyzerDiagnostic>();
            var ctx = new Context("<static>");
            ctx.SymbolTable = symbols;
            Visit(root, ctx, diagnostics);
            return diagnostics;
        }

        private static void Visit(AstNode? node, Context ctx, List<StaticAnalyzerDiagnostic> diags)
        {
            if (node == null) return;

            switch (node)
            {
                case ScopeNode scope:
                    foreach (var n in scope.Nodes) Visit(n, ctx, diags);
                    break;

                case VariableDeclarationNode varDecl:
                    if (varDecl.HasAnnotations)
                    {
                        foreach (var (nameTok, initNode, _) in varDecl.Declarations)
                        {
                            if (!TryEvaluateLiteral(initNode, out var literalVal)) continue;
                            CheckAnnotationsAgainstLiteral(varDecl.Annotations!, literalVal!, $"variable '{nameTok.Value}'", ctx, diags);
                        }
                    }
                    break;

                case FunctionDefinitionNode fn:
                    if (fn.BodyNode != null) Visit(fn.BodyNode, ctx, diags);
                    break;

                case ClassDefinitionNode cls:
                    foreach (var field in cls.Fields)
                    {
                        if (!field.HasAnnotations) continue;
                        if (!TryEvaluateLiteral(field.DefaultValueNode, out var literalVal)) continue;
                        CheckAnnotationsAgainstLiteral(field.Annotations!, literalVal!, $"field '{cls.NameTok.Value}.{field.NameTok.Value}'", ctx, diags);
                    }
                    foreach (var m in cls.Methods)
                        if (m.BodyNode != null) Visit(m.BodyNode, ctx, diags);
                    break;

                case StructDefinitionNode str:
                    foreach (var field in str.Fields)
                    {
                        if (!field.HasAnnotations) continue;
                        if (!TryEvaluateLiteral(field.DefaultValueNode, out var literalVal)) continue;
                        CheckAnnotationsAgainstLiteral(field.Annotations!, literalVal!, $"struct field '{str.NameTok.Value}.{field.NameTok.Value}'", ctx, diags);
                    }
                    break;
            }
        }

        private static void CheckAnnotationsAgainstLiteral(
            List<AnnotationApplicationNode> apps,
            RuntimeValue literalValue,
            string subjectLabel,
            Context ctx,
            List<StaticAnalyzerDiagnostic> diags)
        {
            foreach (var app in apps)
            {
                var typeSymbol = ctx.SymbolTable.Get(app.Name);
                if (typeSymbol is not AnnotationTypeValue typeValue) continue;
                if (typeValue.BuiltInValueValidator == null) continue;
                if (typeValue.IsDeferred) continue;

                var positional = new List<RuntimeValue>();
                var named = new Dictionary<string, RuntimeValue>(System.StringComparer.Ordinal);

                bool allLiteral = true;
                foreach (var argNode in app.PositionalArgs)
                {
                    if (!TryEvaluateLiteral(argNode, out var v)) { allLiteral = false; break; }
                    positional.Add(v!);
                }
                if (!allLiteral) continue;

                foreach (var (key, valNode) in app.NamedArgs)
                {
                    if (!TryEvaluateLiteral(valNode, out var v)) { allLiteral = false; break; }
                    named[key.Value?.ToString() ?? ""] = v!;
                }
                if (!allLiteral) continue;

                var instance = new AnnotationInstanceValue(typeValue, positional, named, app.PositionStart, app.PositionEnd);

                var (ok, msg) = typeValue.BuiltInValueValidator(instance, literalValue, ctx);
                if (!ok)
                {
                    var rendered = (msg ?? "validation failed")
                        .Replace("{subject}", subjectLabel)
                        .Replace("{value}", literalValue.ToString() ?? "null")
                        .Replace("{annotation}", $"@{typeValue.AnnotationName}");
                    diags.Add(new StaticAnalyzerDiagnostic(
                        $"{subjectLabel}: literal value {literalValue} violates @{typeValue.AnnotationName} — {rendered}",
                        app.PositionStart, app.PositionEnd));
                }
            }
        }

        private static bool TryEvaluateLiteral(AstNode? node, out RuntimeValue? value)
        {
            value = null;
            if (node == null) return false;

            switch (node)
            {
                case Parser.Nodes.Primitives.NumberNode nn:
                {
                    var tok = nn.Tok;
                    var s = tok.Value?.ToString() ?? "0";
                    if (tok.Type == RaLanguage.Lexer.Tokens.TokenType.INT)
                    {
                        if (long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long l))
                        {
                            if (l >= int.MinValue && l <= int.MaxValue)
                                value = new IntegerValue((int)l);
                            else
                                value = new LongValue(l);
                            return true;
                        }
                    }
                    if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                    {
                        value = new DoubleValue(d);
                        return true;
                    }
                    return false;
                }
                case Parser.Nodes.Primitives.BooleanNode bn:
                {
                    var tok = bn.Token;
                    var kw = tok.Value;
                    value = new BooleanValue(kw is RaLanguage.Lexer.Tokens.Keyword k && k == RaLanguage.Lexer.Tokens.Keyword.True);
                    return true;
                }
                case Parser.Nodes.Primitives.NullNode:
                    value = new NullValue();
                    return true;
                case Parser.Nodes.Primitives.StringNode sn:
                {
                    if (sn.Parts.Count == 1 && sn.Parts[0] is Parser.Nodes.Primitives.StringTextNode txt)
                    {
                        value = new StringValue(txt.Text ?? string.Empty);
                        return true;
                    }
                    return false;
                }
            }

            return false;
        }
    }
}

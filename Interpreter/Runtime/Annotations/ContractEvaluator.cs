using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Parser;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public static class ContractEvaluator
    {
        private static readonly ConditionalWeakTable<AnnotationInstanceValue, AstNode> _parseCache = new();

        public static Error? CheckPreconditions(string fnMetadataKey, Context execCtx)
            => CheckChain(fnMetadataKey, "requires", "precondition", null, execCtx);

        public static Error? CheckPostconditions(string fnMetadataKey, Context execCtx, RuntimeValue returnValue)
        {
            execCtx.SymbolTable.Set("result", returnValue, isLet: false, declaredType: null, isStaticallyTyped: false, isPublic: false);
            return CheckChain(fnMetadataKey, "ensures", "postcondition", returnValue, execCtx);
        }

        public static Error? CheckInvariants(string classKey, Context execCtx)
            => CheckChain(classKey, "invariant", "class invariant", null, execCtx);

        private static Error? CheckChain(string key, string annName, string label, RuntimeValue? extra, Context ctx)
        {
            var resolver = MetadataKeyResolver.ForContext(ctx);
            foreach (var ann in MetadataRegistry.Global.GetEffective(key, resolver))
            {
                if (ann.DefinitionName != annName) continue;
                var err = EvaluateOne(ann, label, ctx);
                if (err != null) return err;
            }
            return null;
        }

        private static Error? EvaluateOne(AnnotationInstanceValue ann, string label, Context ctx)
        {
            var conditionVal = ann.Get("condition");
            if (conditionVal is not StringValue conditionStr)
            {
                return new RuntimeError(ann.ApplicationStart, ann.ApplicationEnd,
                    $"@{ann.DefinitionName} requires 'condition' as string", ctx);
            }

            AstNode? exprNode;
            if (!_parseCache.TryGetValue(ann, out exprNode))
            {
                var lexer = new RaLanguage.Lexer.Lexer("<contract>", conditionStr.Value);
                var (tokens, lexDiag) = lexer.MakeTokens();
                if (lexDiag.HasErrors)
                {
                    return new RuntimeError(ann.ApplicationStart, ann.ApplicationEnd,
                        $"@{ann.DefinitionName} lex error in condition '{conditionStr.Value}'", ctx);
                }
                var parser = new RaLanguage.Parser.Parser(tokens);
                var parseRes = parser.Parse();
                if (parseRes.HasErrors)
                {
                    return new RuntimeError(ann.ApplicationStart, ann.ApplicationEnd,
                        $"@{ann.DefinitionName} parse error in condition '{conditionStr.Value}'", ctx);
                }
                exprNode = parseRes.Node;
                if (exprNode is Parser.Nodes.Special.ScopeNode scope && scope.Nodes.Count > 0)
                    exprNode = scope.Nodes[0];
                _parseCache.Add(ann, exprNode!);
            }

            var interpreter = new Interpreter();
            var res = interpreter.Visit(exprNode!, ctx);
            if (res.Error != null)
            {
                return new RuntimeError(ann.ApplicationStart, ann.ApplicationEnd,
                    $"@{ann.DefinitionName} evaluation error: {res.Error.Details}", ctx);
            }

            bool ok = res.Value switch
            {
                BooleanValue bv => bv.Value,
                NumberValue nv => !nv.Value.IsZero(),
                IntegerValue iv => iv.Value != 0,
                NullValue => false,
                _ => true
            };

            if (ok) return null;

            var msg = (ann.Get("message") as StringValue)?.Value
                ?? $"{label} '{conditionStr.Value}' failed";
            return new RuntimeError(ann.ApplicationStart, ann.ApplicationEnd, msg, ctx);
        }
    }
}

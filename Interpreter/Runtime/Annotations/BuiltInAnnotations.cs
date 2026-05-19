using System.Collections.Generic;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes.Annotations;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public static class BuiltInAnnotations
    {
        public const string Target = "target";
        public const string Repeatable = "repeatable";
        public const string Inherited = "inherited";
        public const string Sealed = "sealed";
        public const string Composes = "composes";
        public const string Priority = "priority";
        public const string Intercept = "intercept";
        public const string Deprecated = "deprecated";
        public const string Validator = "validator";
        public const string Returns = "returns";
        public const string Deferred = "deferred";
        public const string Coerce = "coerce";
        public const string DllImport = "dll_import";

        public static IEnumerable<AnnotationTypeValue> CreateAll()
        {
            yield return MakeTarget();
            yield return MakeRepeatable();
            yield return MakeInherited();
            yield return MakeSealed();
            yield return MakeComposes();
            yield return MakePriority();
            yield return MakeIntercept();
            yield return MakeDeprecated();
            yield return MakeValidator();
            yield return MakeReturns();
            yield return MakeDeferred();
            yield return MakeCoerce();
            yield return MakeTest();
            yield return MakeBefore();
            yield return MakeAfter();
            yield return MakeParameterized();
            yield return MakeExpectedThrows();
            yield return MakeSkip();
            yield return MakeRequires();
            yield return MakeEnsures();
            yield return MakeInvariant();
            yield return MakeDerive();
            yield return MakeDllImport();
            foreach (var v in BuiltInValidators.CreateAll())
                yield return v;
        }

        private static AnnotationTypeValue MakeDllImport()
        {
            var paramLib = new AnnotationParameterNode(Tok("library"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var paramEntry = new AnnotationParameterNode(Tok("entry_point"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var paramConv = new AnnotationParameterNode(Tok("calling_convention"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var paramCharset = new AnnotationParameterNode(Tok("charset"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var paramExact = new AnnotationParameterNode(Tok("exact_spelling"), new Types.TypeDescriptor("bool"), MakeBoolDefaultNode(false), false);
            var paramLastError = new AnnotationParameterNode(Tok("set_last_error"), new Types.TypeDescriptor("bool"), MakeBoolDefaultNode(false), false);
            var paramPreserve = new AnnotationParameterNode(Tok("preserve_sig"), new Types.TypeDescriptor("bool"), MakeBoolDefaultNode(true), false);
            var paramBestFit = new AnnotationParameterNode(Tok("best_fit_mapping"), new Types.TypeDescriptor("bool"), MakeBoolDefaultNode(true), false);
            var paramThrowUnmappable = new AnnotationParameterNode(Tok("throw_on_unmappable_char"), new Types.TypeDescriptor("bool"), MakeBoolDefaultNode(false), false);
            var paramSearchPaths = new AnnotationParameterNode(Tok("search_paths"), null, MakeNullDefaultNode(), false);
            var paramName = new AnnotationParameterNode(Tok("name"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var paramStringFree = new AnnotationParameterNode(Tok("string_free"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var paramTrace = new AnnotationParameterNode(Tok("trace"), new Types.TypeDescriptor("bool"), MakeBoolDefaultNode(false), false);
            var paramStaThread = new AnnotationParameterNode(Tok("sta_thread"), new Types.TypeDescriptor("bool"), MakeBoolDefaultNode(false), false);
            var paramAbiCanary = new AnnotationParameterNode(Tok("abi_canary"), new Types.TypeDescriptor("bool"), MakeBoolDefaultNode(false), false);

            var ann = new AnnotationTypeValue(DllImport, true,
                new List<AnnotationParameterNode>
                {
                    paramLib, paramEntry, paramConv, paramCharset, paramExact, paramLastError,
                    paramPreserve, paramBestFit, paramThrowUnmappable, paramSearchPaths, paramName, paramStringFree,
                    paramTrace, paramStaThread, paramAbiCanary
                }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Function };
            ann.IsRepeatable = false;
            return ann;
        }

        private static Parser.Nodes.AstNode MakeBoolDefaultNode(bool value)
        {
            var tok = new Token(TokenType.KEYWORD, value ? Keyword.True : Keyword.False, new Position(0, 0, 0, "<builtin>", string.Empty));
            return new Parser.Nodes.Primitives.BooleanNode(tok);
        }

        private static AnnotationTypeValue MakeTest()
        {
            var ann = new AnnotationTypeValue("test", true, new List<AnnotationParameterNode>(), isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Function };
            return ann;
        }
        private static AnnotationTypeValue MakeBefore()
        {
            var ann = new AnnotationTypeValue("before", true, new List<AnnotationParameterNode>(), isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Function };
            return ann;
        }
        private static AnnotationTypeValue MakeAfter()
        {
            var ann = new AnnotationTypeValue("after", true, new List<AnnotationParameterNode>(), isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Function };
            return ann;
        }
        private static AnnotationTypeValue MakeParameterized()
        {
            var paramVals = new AnnotationParameterNode(Tok("values"), null, null, false);
            var ann = new AnnotationTypeValue("parameterized", true, new List<AnnotationParameterNode> { paramVals }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Function };
            return ann;
        }
        private static AnnotationTypeValue MakeExpectedThrows()
        {
            var paramType = new AnnotationParameterNode(Tok("type"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var ann = new AnnotationTypeValue("expected_throws", true, new List<AnnotationParameterNode> { paramType }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Function };
            return ann;
        }
        private static AnnotationTypeValue MakeSkip()
        {
            var paramReason = new AnnotationParameterNode(Tok("reason"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var ann = new AnnotationTypeValue("skip", true, new List<AnnotationParameterNode> { paramReason }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Function };
            return ann;
        }

        private static AnnotationTypeValue MakeRequires()
        {
            var paramCond = new AnnotationParameterNode(Tok("condition"), new Types.TypeDescriptor("string"), null, false);
            var paramMsg = new AnnotationParameterNode(Tok("message"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var ann = new AnnotationTypeValue("requires", true, new List<AnnotationParameterNode> { paramCond, paramMsg }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind>
            {
                AnnotationTargetKind.Function,
                AnnotationTargetKind.Method,
                AnnotationTargetKind.Constructor
            };
            ann.IsRepeatable = true;
            return ann;
        }
        private static AnnotationTypeValue MakeEnsures()
        {
            var paramCond = new AnnotationParameterNode(Tok("condition"), new Types.TypeDescriptor("string"), null, false);
            var paramMsg = new AnnotationParameterNode(Tok("message"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var ann = new AnnotationTypeValue("ensures", true, new List<AnnotationParameterNode> { paramCond, paramMsg }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind>
            {
                AnnotationTargetKind.Function,
                AnnotationTargetKind.Method,
                AnnotationTargetKind.Constructor
            };
            ann.IsRepeatable = true;
            return ann;
        }
        private static AnnotationTypeValue MakeInvariant()
        {
            var paramCond = new AnnotationParameterNode(Tok("condition"), new Types.TypeDescriptor("string"), null, false);
            var paramMsg = new AnnotationParameterNode(Tok("message"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var ann = new AnnotationTypeValue("invariant", true, new List<AnnotationParameterNode> { paramCond, paramMsg }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind>
            {
                AnnotationTargetKind.Class,
                AnnotationTargetKind.Struct
            };
            ann.IsRepeatable = true;
            return ann;
        }

        private static AnnotationTypeValue MakeDerive()
        {
            var paramFlags = new AnnotationParameterNode(Tok("flags"), null, null, true);
            var ann = new AnnotationTypeValue("derive", true, new List<AnnotationParameterNode> { paramFlags }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Class };
            ann.IsRepeatable = false;
            return ann;
        }

        private static AnnotationTypeValue MakeReturns()
        {
            var paramAnns = new AnnotationParameterNode(Tok("annotations"), null, null, true);
            var ann = new AnnotationTypeValue(Returns, true, new List<AnnotationParameterNode> { paramAnns }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind>
            {
                AnnotationTargetKind.Function,
                AnnotationTargetKind.Method,
                AnnotationTargetKind.Constructor
            };
            ann.IsRepeatable = true;
            return ann;
        }

        private static AnnotationTypeValue MakeDeferred()
        {
            var ann = new AnnotationTypeValue(Deferred, true, new List<AnnotationParameterNode>(), isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Annotation };
            return ann;
        }

        private static AnnotationTypeValue MakeCoerce()
        {
            var paramStrategy = new AnnotationParameterNode(Tok("strategy"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var paramFn = new AnnotationParameterNode(Tok("handler"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var ann = new AnnotationTypeValue(Coerce, true, new List<AnnotationParameterNode> { paramStrategy, paramFn }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind>
            {
                AnnotationTargetKind.Parameter,
                AnnotationTargetKind.Variable,
                AnnotationTargetKind.Field,
                AnnotationTargetKind.StaticField
            };
            ann.IsRepeatable = true;
            ann.BuiltInCoercer = (inst, value, ctx) =>
            {
                var strategy = (inst.Get("strategy") as StringValue)?.Value;
                var fnName = (inst.Get("handler") as StringValue)?.Value;
                if (strategy != null)
                {
                    var (newVal, msg) = CoercerRegistry.Apply(strategy, value, ctx);
                    return (newVal, msg);
                }
                if (fnName != null)
                {
                    var fnSym = ctx.SymbolTable.Get(fnName);
                    if (fnSym is not BaseFunctionValue bfn)
                        return (null, $"coercer function '{fnName}' not defined");
                    var execRes = bfn.Execute(new List<RuntimeValue> { value, inst });
                    if (execRes.Error != null) return (null, execRes.Error.Details);
                    return (execRes.Value, null);
                }
                return (value, null);
            };
            return ann;
        }

        public static void RegisterAll(SymbolTable symbolTable)
        {
            foreach (var ann in CreateAll())
            {
                symbolTable.Set(ann.AnnotationName, ann, isLet: true, declaredType: null, isStaticallyTyped: false, isPublic: true);
            }
        }

        private static Token Tok(string name) => new Token(TokenType.IDENTIFIER, name, new Position(0, 0, 0, "<builtin>", string.Empty));

        private static AnnotationTypeValue MakeTarget()
        {
            var paramSpread = new AnnotationParameterNode(Tok("kinds"), null, null, true);
            var ann = new AnnotationTypeValue(Target, true, new List<AnnotationParameterNode> { paramSpread }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Annotation };
            ann.IsRepeatable = true;
            return ann;
        }

        private static AnnotationTypeValue MakeRepeatable()
        {
            var ann = new AnnotationTypeValue(Repeatable, true, new List<AnnotationParameterNode>(), isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Annotation };
            return ann;
        }

        private static AnnotationTypeValue MakeInherited()
        {
            var ann = new AnnotationTypeValue(Inherited, true, new List<AnnotationParameterNode>(), isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Annotation };
            return ann;
        }

        private static AnnotationTypeValue MakeSealed()
        {
            var ann = new AnnotationTypeValue(Sealed, true, new List<AnnotationParameterNode>(), isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Annotation };
            return ann;
        }

        private static AnnotationTypeValue MakeComposes()
        {
            var paramSpread = new AnnotationParameterNode(Tok("annotations"), null, null, true);
            var ann = new AnnotationTypeValue(Composes, true, new List<AnnotationParameterNode> { paramSpread }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Annotation };
            ann.IsRepeatable = true;
            return ann;
        }

        private static AnnotationTypeValue MakePriority()
        {
            var paramValue = new AnnotationParameterNode(Tok("value"), new Types.TypeDescriptor("int"), null, false);
            var ann = new AnnotationTypeValue(Priority, true, new List<AnnotationParameterNode> { paramValue }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Annotation };
            return ann;
        }

        private static AnnotationTypeValue MakeIntercept()
        {
            var paramBefore = new AnnotationParameterNode(Tok("before"), new Types.TypeDescriptor("string"), null, false);
            var paramAfter = new AnnotationParameterNode(Tok("after"), new Types.TypeDescriptor("string"), null, false);
            paramBefore = new AnnotationParameterNode(Tok("before"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            paramAfter = new AnnotationParameterNode(Tok("after"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var ann = new AnnotationTypeValue(Intercept, true, new List<AnnotationParameterNode> { paramBefore, paramAfter }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Annotation };
            return ann;
        }

        private static AnnotationTypeValue MakeDeprecated()
        {
            var paramReason = new AnnotationParameterNode(Tok("reason"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var ann = new AnnotationTypeValue(Deprecated, true, new List<AnnotationParameterNode> { paramReason }, isBuiltIn: true);
            ann.AllowedTargets = null;
            return ann;
        }

        private static AnnotationTypeValue MakeValidator()
        {
            var paramCheck = new AnnotationParameterNode(Tok("check"), new Types.TypeDescriptor("string"), null, false);
            var paramMsg = new AnnotationParameterNode(Tok("message"), new Types.TypeDescriptor("string"), MakeNullDefaultNode(), false);
            var ann = new AnnotationTypeValue(Validator, true, new List<AnnotationParameterNode> { paramCheck, paramMsg }, isBuiltIn: true);
            ann.AllowedTargets = new HashSet<AnnotationTargetKind> { AnnotationTargetKind.Annotation };
            return ann;
        }

        private static Parser.Nodes.AstNode MakeNullDefaultNode()
        {
            return new Parser.Nodes.Primitives.NullNode(new Token(TokenType.KEYWORD, Keyword.Null, new Position(0, 0, 0, "<builtin>", string.Empty)));
        }
    }
}

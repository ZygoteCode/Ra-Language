using System.Collections.Generic;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public sealed class TestResult
    {
        public string TestName { get; }
        public string? ParameterLabel { get; }
        public bool Passed { get; }
        public bool Skipped { get; }
        public string? FailureMessage { get; }

        public TestResult(string name, string? paramLabel, bool passed, bool skipped, string? failure)
        {
            TestName = name; ParameterLabel = paramLabel; Passed = passed; Skipped = skipped; FailureMessage = failure;
        }

        public string Format()
        {
            var label = ParameterLabel != null ? $"{TestName}[{ParameterLabel}]" : TestName;
            if (Skipped) return $"SKIP  {label}: {FailureMessage}";
            if (Passed) return $"PASS  {label}";
            return $"FAIL  {label}: {FailureMessage}";
        }
    }

    public static class TestRunner
    {
        public static List<TestResult> RunAll(Context context)
        {
            var results = new List<TestResult>();

            var beforeKeys = CollectFnsWithAnnotation("before", context);
            var afterKeys = CollectFnsWithAnnotation("after", context);
            var testKeys = CollectFnsWithAnnotation("test", context);

            foreach (var (fnName, _) in testKeys)
            {
                RunSingleTest(fnName, beforeKeys, afterKeys, context, results);
            }

            return results;
        }

        private static List<(string fnName, AnnotationInstanceValue ann)> CollectFnsWithAnnotation(
            string annName, Context ctx)
        {
            var list = new List<(string, AnnotationInstanceValue)>();
            foreach (var key in MetadataRegistry.Global.Keys)
            {
                if (!key.StartsWith("fn:", System.StringComparison.Ordinal)) continue;
                var fnName = key.Substring(3);
                foreach (var ann in MetadataRegistry.Global.GetByKey(key))
                {
                    if (ann.DefinitionName == annName)
                        list.Add((fnName, ann));
                }
            }
            return list;
        }

        private static void RunSingleTest(
            string fnName,
            List<(string, AnnotationInstanceValue)> beforeKeys,
            List<(string, AnnotationInstanceValue)> afterKeys,
            Context ctx,
            List<TestResult> results)
        {
            var skipAnn = MetadataRegistry.Global.FindEffective(
                MetadataTarget.BuildKey(AnnotationTargetKind.Function, null, fnName),
                "skip",
                MetadataKeyResolver.ForContext(ctx));
            if (skipAnn != null)
            {
                var reason = (skipAnn.Get("reason") as StringValue)?.Value ?? "skipped";
                results.Add(new TestResult(fnName, null, false, true, reason));
                return;
            }

            var paramAnn = MetadataRegistry.Global.FindEffective(
                MetadataTarget.BuildKey(AnnotationTargetKind.Function, null, fnName),
                "parameterized",
                MetadataKeyResolver.ForContext(ctx));

            var fn = ctx.SymbolTable.Get(fnName) as BaseFunctionValue;
            if (fn == null)
            {
                results.Add(new TestResult(fnName, null, false, false, $"function '{fnName}' not found"));
                return;
            }

            if (paramAnn == null)
            {
                RunOnce(fn, fnName, null, new List<RuntimeValue>(), beforeKeys, afterKeys, ctx, results);
                return;
            }

            var values = paramAnn.Get("values");
            if (values is ListValue lv)
            {
                int i = 0;
                foreach (var v in lv.Elements)
                {
                    var args = v is ListValue innerLv ? new List<RuntimeValue>(innerLv.Elements) : new List<RuntimeValue> { v };
                    RunOnce(fn, fnName, $"#{i}={v}", args, beforeKeys, afterKeys, ctx, results);
                    i++;
                }
            }
            else
            {
                results.Add(new TestResult(fnName, null, false, false, "@parameterized requires 'values' to be a list"));
            }
        }

        private static void RunOnce(
            BaseFunctionValue fn,
            string fnName,
            string? paramLabel,
            List<RuntimeValue> args,
            List<(string fnName, AnnotationInstanceValue ann)> beforeKeys,
            List<(string fnName, AnnotationInstanceValue ann)> afterKeys,
            Context ctx,
            List<TestResult> results)
        {
            foreach (var (beforeName, _) in beforeKeys)
            {
                var beforeFn = ctx.SymbolTable.Get(beforeName) as BaseFunctionValue;
                if (beforeFn == null) continue;
                var beforeRes = beforeFn.Execute(new List<RuntimeValue>());
                if (beforeRes.Error != null)
                {
                    results.Add(new TestResult(fnName, paramLabel, false, false, $"@before '{beforeName}' failed: {beforeRes.Error.Details}"));
                    return;
                }
            }

            var expectedThrows = MetadataRegistry.Global.FindEffective(
                MetadataTarget.BuildKey(AnnotationTargetKind.Function, null, fnName),
                "expected_throws",
                MetadataKeyResolver.ForContext(ctx));

            var execRes = fn.Execute(args);
            string? failureMsg = null;
            bool passed = true;

            if (expectedThrows != null)
            {
                if (execRes.Error == null)
                {
                    passed = false;
                    failureMsg = $"expected throw of '{(expectedThrows.Get("type") as StringValue)?.Value ?? "any"}' but no error";
                }
                else
                {
                    var expectedType = (expectedThrows.Get("type") as StringValue)?.Value;
                    if (expectedType != null && !execRes.Error.ErrorName.Contains(expectedType, System.StringComparison.OrdinalIgnoreCase)
                        && !execRes.Error.Details.Contains(expectedType, System.StringComparison.OrdinalIgnoreCase))
                    {
                        passed = false;
                        failureMsg = $"expected throw matching '{expectedType}' but got: {execRes.Error.Details}";
                    }
                }
            }
            else if (execRes.Error != null)
            {
                passed = false;
                failureMsg = execRes.Error.Details;
            }

            foreach (var (afterName, _) in afterKeys)
            {
                var afterFn = ctx.SymbolTable.Get(afterName) as BaseFunctionValue;
                if (afterFn == null) continue;
                afterFn.Execute(new List<RuntimeValue>());
            }

            results.Add(new TestResult(fnName, paramLabel, passed, false, failureMsg));
        }
    }
}

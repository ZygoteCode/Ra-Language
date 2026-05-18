using System.Collections.Generic;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Values.Functions
{
    public class BuiltInFunctionValue : BaseFunctionValue
    {
        public sealed override RuntimeValueType Type => RuntimeValueType.Function;
        public BuiltInFunctionValue(string name) : base(name) { }


        public sealed override RuntimeResult Execute(List<RuntimeValue> args)
        {
            var res = new RuntimeResult();
            var execCtx = GenerateNewContext();

            RuntimeResult methodResult;
            List<string> argNames;

            switch (Name)
            {
                case "print": argNames = new List<string> { "value" }; methodResult = ExecutePrint(execCtx, args, argNames, res); break;
                case "print_ret": argNames = new List<string> { "value" }; methodResult = ExecutePrintRet(execCtx, args, argNames, res); break;
                case "exists": argNames = new List<string> { "symbol" }; methodResult = ExecuteExists(execCtx, args, argNames, res); break;
                case "field_exists": argNames = new List<string> { "type", "symbol" }; methodResult = ExecuteFieldExists(execCtx, args, argNames, res); break;
                case "drop": argNames = new List<string> { "symbol" }; methodResult = ExecuteDrop(execCtx, args, argNames, res); break;
                case "is_public": argNames = new List<string> { "symbol" }; methodResult = ExecuteIsPublic(execCtx, args, argNames, res); break;
                case "is_field_public": argNames = new List<string> { "type", "symbol" }; methodResult = ExecuteIsFieldPublic(execCtx, args, argNames, res); break;
                case "is_field_static": argNames = new List<string> { "type", "symbol" }; methodResult = ExecuteIsFieldStatic(execCtx, args, argNames, res); break;
                case "annotations_of": argNames = new List<string> { "__subj" }; methodResult = ExecuteAnnotationsOf(execCtx, args, argNames, res); break;
                case "has_annotation": argNames = new List<string> { "__subj", "__ann" }; methodResult = ExecuteHasAnnotation(execCtx, args, argNames, res); break;
                case "annotation_arg": argNames = new List<string> { "__subj", "__ann", "__key" }; methodResult = ExecuteAnnotationArg(execCtx, args, argNames, res); break;
                case "annotation_targets": argNames = new List<string>(); methodResult = ExecuteAnnotationTargets(execCtx, args, argNames, res); break;
                case "validate": argNames = new List<string> { "__val", "__ann" }; methodResult = ExecuteValidate(execCtx, args, argNames, res); break;
                case "validate_target": argNames = new List<string> { "__val", "__key" }; methodResult = ExecuteValidateTarget(execCtx, args, argNames, res); break;
                case "validate_deferred": argNames = new List<string>(); methodResult = ExecuteValidateDeferred(execCtx, args, argNames, res); break;
                case "coerce_value": argNames = new List<string> { "__val", "__key" }; methodResult = ExecuteCoerceValue(execCtx, args, argNames, res); break;
                case "run_tests": argNames = new List<string>(); methodResult = ExecuteRunTests(execCtx, args, argNames, res); break;
                default: return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"No execute_{Name} method defined", Context));
            }

            return methodResult;
        }

        private RuntimeResult ExecuteCommon(Context execCtx, List<RuntimeValue> args, List<string> argNames, RuntimeResult res, Func<Context, RuntimeResult> action)
        {
            res.Register(CheckAndPopulateArgs(argNames, args, execCtx));
            if (res.ShouldReturn()) return res;
            var ret = res.Register(action(execCtx));
            if (res.ShouldReturn()) return res;
            return res.Success(ret);
        }

        private RuntimeResult ExecutePrint(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var value = c.SymbolTable.Get("value");

            string output;
            if (value.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue)value;
                output = instance.TryCallToString().value;
            }
            else if (value.Type == RuntimeValueType.StructInstance)
            {
                var instance = (RaLanguage.Interpreter.Values.Structs.StructInstanceValue)value;
                output = instance.TryCallToString().value;
            }
            else
            {
                output = value.ToString();
            }

            Console.WriteLine(output);
            return new RuntimeResult().Success(new NullValue().SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecutePrintRet(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var value = c.SymbolTable.Get("value");

            string output;
            if (value.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue)value;
                output = instance.TryCallToString().value;
            }
            else if (value.Type == RuntimeValueType.StructInstance)
            {
                var instance = (RaLanguage.Interpreter.Values.Structs.StructInstanceValue)value;
                output = instance.TryCallToString().value;
            }
            else
            {
                output = value.ToString();
            }

            Console.WriteLine(output);
            return new RuntimeResult().Success(new StringValue(output).SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteExists(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var value = c.SymbolTable.Get("symbol");
            SymbolEntry? retrieved = c.SymbolTable.GetEntry(value.ToString());
            return new RuntimeResult().Success(new BooleanValue(retrieved != null).SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteDrop(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var value = c.SymbolTable.Get("symbol");
            string valueStr = value.ToString();
            SymbolEntry? retrieved = c.SymbolTable.GetEntry(valueStr);

            if (retrieved == null)
            {
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The symbol '{valueStr}' is not defined", Context));
            }

            c.SymbolTable.Remove(valueStr);
            return new RuntimeResult().Success(new NullValue().SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteIsPublic(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var value = c.SymbolTable.Get("symbol");
            string valueStr = value.ToString();
            SymbolEntry? retrieved = c.SymbolTable.GetEntry(valueStr);

            if (retrieved == null)
            {
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The symbol '{valueStr}' is not defined", Context));
            }

            return new RuntimeResult().Success(new BooleanValue(retrieved.IsPublic).SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteIsFieldPublic(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var symbol = c.SymbolTable.Get("symbol");
            string symbolStr = symbol.ToString();
            var theType = c.SymbolTable.Get("type");

            if (theType.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue)theType;

                if (!instance.HasField(symbolStr))
                {
                    return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The symbol '{symbolStr}' is not defined in type", Context));
                }

                return new RuntimeResult().Success(new BooleanValue(instance.IsFieldPublic(symbolStr)).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            else if (theType.Type == RuntimeValueType.ClassType)
            {
                var instance = (RaLanguage.Interpreter.Values.Primitives.ClassTypeValue)theType;

                if (!instance.HasField(symbolStr))
                {
                    return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The symbol '{symbolStr}' is not defined in type", Context));
                }

                return new RuntimeResult().Success(new BooleanValue(instance.IsStaticFieldPublic(symbolStr) || instance.IsFieldPublic(symbolStr)).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            else if (theType.Type == RuntimeValueType.StructInstance)
            {
                var instance = (RaLanguage.Interpreter.Values.Structs.StructInstanceValue)theType;

                if (!instance.HasField(symbolStr))
                {
                    return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The symbol '{symbolStr}' is not defined in type", Context));
                }

                return new RuntimeResult().Success(new BooleanValue(instance.IsFieldPublic(symbolStr)).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            else if (theType.Type == RuntimeValueType.StructType)
            {
                var instance = (RaLanguage.Interpreter.Values.Structs.StructTypeValue)theType;

                if (!instance.HasField(symbolStr))
                {
                    return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The symbol '{symbolStr}' is not defined in type", Context));
                }

                return new RuntimeResult().Success(new BooleanValue(instance.IsFieldPublic(symbolStr)).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            else
            {
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The specified type is not valid", Context));
            }
        });

        private RuntimeResult ExecuteFieldExists(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var symbol = c.SymbolTable.Get("symbol");
            string symbolStr = symbol.ToString();
            var theType = c.SymbolTable.Get("type");

            if (theType.Type == RuntimeValueType.ClassInstance)
            {
                var instance = (RaLanguage.Interpreter.Values.Primitives.ClassInstanceValue)theType;
                return new RuntimeResult().Success(new BooleanValue(instance.HasField(symbolStr)).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            else if (theType.Type == RuntimeValueType.ClassType)
            {
                var instance = (RaLanguage.Interpreter.Values.Primitives.ClassTypeValue)theType;
                return new RuntimeResult().Success(new BooleanValue(instance.HasField(symbolStr)).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            else if (theType.Type == RuntimeValueType.StructInstance)
            {
                var instance = (RaLanguage.Interpreter.Values.Structs.StructInstanceValue)theType;
                return new RuntimeResult().Success(new BooleanValue(instance.HasField(symbolStr)).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            else if (theType.Type == RuntimeValueType.StructType)
            {
                var instance = (RaLanguage.Interpreter.Values.Structs.StructTypeValue)theType;
                return new RuntimeResult().Success(new BooleanValue(instance.HasField(symbolStr)).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            else
            {
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The specified type is not valid", Context));
            }
        });

        private RuntimeResult ExecuteIsFieldStatic(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var symbol = c.SymbolTable.Get("symbol");
            string symbolStr = symbol.ToString();
            var theType = c.SymbolTable.Get("type");

            if (theType.Type == RuntimeValueType.ClassType)
            {
                var instance = (RaLanguage.Interpreter.Values.Primitives.ClassTypeValue)theType;

                if (!instance.HasField(symbolStr))
                {
                    return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The symbol '{symbolStr}' is not defined in type", Context));
                }

                return new RuntimeResult().Success(new BooleanValue(instance.HasStaticField(symbolStr)).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            else
            {
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"The specified type is not valid", Context));
            }
        });

        private RuntimeResult ExecuteAnnotationsOf(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var subj = c.SymbolTable.Get("__subj");
            var key = ResolveMetadataKey(subj);
            if (key == null)
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, $"Cannot resolve metadata target for value of type '{subj.Type}'", Context));

            var anns = MetadataRegistry.Global.GetEffective(key, MetadataKeyResolver.ForContext(c));
            var list = new List<RuntimeValue>();
            foreach (var a in anns) list.Add(a);
            return new RuntimeResult().Success(new ListValue(list).SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteHasAnnotation(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var subj = c.SymbolTable.Get("__subj");
            var ann = c.SymbolTable.Get("__ann");
            var key = ResolveMetadataKey(subj);
            if (key == null)
                return new RuntimeResult().Success(new BooleanValue(false).SetContext(ctx).SetPos(PositionStart, PositionEnd));

            var nameStr = ann is StringValue sv ? sv.Value : ann.ToString() ?? "";
            var has = MetadataRegistry.Global.HasAnnotationEffective(key, nameStr, MetadataKeyResolver.ForContext(c));
            return new RuntimeResult().Success(new BooleanValue(has).SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteAnnotationArg(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var subj = c.SymbolTable.Get("__subj");
            var ann = c.SymbolTable.Get("__ann");
            var keyArg = c.SymbolTable.Get("__key");
            var targetKey = ResolveMetadataKey(subj);
            if (targetKey == null)
                return new RuntimeResult().Success(new NullValue().SetContext(ctx).SetPos(PositionStart, PositionEnd));

            var nameStr = ann is StringValue sv ? sv.Value : ann.ToString() ?? "";
            var keyStr = keyArg is StringValue kv ? kv.Value : keyArg.ToString() ?? "";
            var found = MetadataRegistry.Global.FindEffective(targetKey, nameStr, MetadataKeyResolver.ForContext(c));
            if (found != null)
            {
                var v = found.Get(keyStr);
                if (v != null) return new RuntimeResult().Success(v.Copy().SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            return new RuntimeResult().Success(new NullValue().SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteAnnotationTargets(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var keys = new List<RuntimeValue>();
            foreach (var k in MetadataRegistry.Global.Keys)
            {
                keys.Add(new StringValue(k).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            return new RuntimeResult().Success(new ListValue(keys).SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteValidate(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var val = c.SymbolTable.Get("__val");
            var ann = c.SymbolTable.Get("__ann");
            if (ann is not AnnotationInstanceValue inst)
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, "validate(value, ann) requires an annotation instance as second argument", Context));
            var err = AnnotationValidator.Validate(inst, val, "value", c);
            if (err != null)
                return new RuntimeResult().Success(new StringValue(err.Details).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            return new RuntimeResult().Success(new NullValue().SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteValidateTarget(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var val = c.SymbolTable.Get("__val");
            var keyArg = c.SymbolTable.Get("__key");
            if (keyArg is not StringValue ks)
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, "validate_target(value, key) requires a string key", Context));
            var err = AnnotationValidator.ValidateTarget(ks.Value, val, ks.Value, c);
            if (err != null)
                return new RuntimeResult().Success(new StringValue(err.Details).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            return new RuntimeResult().Success(new NullValue().SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteValidateDeferred(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var errs = AnnotationValidator.DrainAndRunDeferred();
            var list = new List<RuntimeValue>();
            foreach (var e in errs)
            {
                list.Add(new StringValue(e.Details).SetContext(ctx).SetPos(PositionStart, PositionEnd));
            }
            return new RuntimeResult().Success(new ListValue(list).SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteRunTests(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var results = TestRunner.RunAll(c);
            int passed = 0, failed = 0, skipped = 0;
            foreach (var r in results)
            {
                Console.WriteLine(r.Format());
                if (r.Skipped) skipped++;
                else if (r.Passed) passed++;
                else failed++;
            }
            Console.WriteLine($"\nResults: {passed} passed, {failed} failed, {skipped} skipped");

            var summary = new List<(RuntimeValue, RuntimeValue)>
            {
                (new StringValue("passed").SetContext(c).SetPos(PositionStart, PositionEnd),
                 new IntegerValue(passed).SetContext(c).SetPos(PositionStart, PositionEnd)),
                (new StringValue("failed").SetContext(c).SetPos(PositionStart, PositionEnd),
                 new IntegerValue(failed).SetContext(c).SetPos(PositionStart, PositionEnd)),
                (new StringValue("skipped").SetContext(c).SetPos(PositionStart, PositionEnd),
                 new IntegerValue(skipped).SetContext(c).SetPos(PositionStart, PositionEnd))
            };
            return new RuntimeResult().Success(new MapValue(summary).SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private RuntimeResult ExecuteCoerceValue(Context ctx, List<RuntimeValue> args, List<string> names, RuntimeResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var val = c.SymbolTable.Get("__val");
            var keyArg = c.SymbolTable.Get("__key");
            if (keyArg is not StringValue ks)
                return new RuntimeResult().Failure(new RuntimeError(PositionStart, PositionEnd, "coerce_value(value, key) requires a string key", Context));
            var (newVal, err) = AnnotationValidator.CoerceTarget(ks.Value, val, ks.Value, c);
            if (err != null)
                return new RuntimeResult().Failure(err);
            return new RuntimeResult().Success(newVal.Copy().SetContext(ctx).SetPos(PositionStart, PositionEnd));
        });

        private static string? ResolveMetadataKey(RuntimeValue target)
        {
            if (target is StringValue sv) return sv.Value;
            if (target is FunctionValue fv) return fv.MetadataKey ?? MetadataTarget.BuildKey(AnnotationTargetKind.Function, null, fv.Name);
            if (target is ClassTypeValue ctv) return MetadataTarget.BuildKey(AnnotationTargetKind.Class, null, ctv.ClassName);
            if (target is ClassInstanceValue civ) return MetadataTarget.BuildKey(AnnotationTargetKind.Class, null, civ.Definition.ClassName);
            if (target is AnnotationTypeValue atv) return MetadataTarget.BuildKey(AnnotationTargetKind.Annotation, null, atv.AnnotationName);
            if (target is Structs.StructTypeValue stv) return MetadataTarget.BuildKey(AnnotationTargetKind.Struct, null, stv.StructName);
            if (target is Structs.StructInstanceValue siv) return MetadataTarget.BuildKey(AnnotationTargetKind.Struct, null, siv.Definition.StructName);
            return null;
        }

        public sealed override RuntimeValue Copy()
        {
            return new BuiltInFunctionValue(Name).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public sealed override string ToString() => $"<built-in function {Name}>";
    }
}
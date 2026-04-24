using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
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

        public sealed override RuntimeValue Copy()
        {
            return new BuiltInFunctionValue(Name).SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public sealed override string ToString() => $"<built-in function {Name}>";
    }
}
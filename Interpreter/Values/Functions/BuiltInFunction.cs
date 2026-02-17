using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values.Primitives;

namespace RaLanguage.Interpreter.Values.Functions
{
    public class BuiltInFunction : BaseFunction
    {
        public BuiltInFunction(string name) : base(name) { }

        public override RTResult Execute(List<RuntimeValue> args)
        {
            var res = new RTResult();
            var execCtx = GenerateNewContext();

            // Reflection-less dispatch for performance and simplicity given fixed builtins
            RTResult methodResult;
            List<string> argNames;

            switch (Name)
            {
                case "print": argNames = new List<string> { "value" }; methodResult = ExecutePrint(execCtx, args, argNames, res); break;
                case "print_ret": argNames = new List<string> { "value" }; methodResult = ExecutePrintRet(execCtx, args, argNames, res); break;
                case "input": argNames = new List<string>(); methodResult = ExecuteInput(execCtx, args, argNames, res); break;
                case "input_int": argNames = new List<string>(); methodResult = ExecuteInputInt(execCtx, args, argNames, res); break;
                case "clear": argNames = new List<string>(); methodResult = ExecuteClear(execCtx, args, argNames, res); break;
                case "is_number": argNames = new List<string> { "value" }; methodResult = ExecuteIsNumber(execCtx, args, argNames, res); break;
                case "is_string": argNames = new List<string> { "value" }; methodResult = ExecuteIsString(execCtx, args, argNames, res); break;
                case "is_list": argNames = new List<string> { "value" }; methodResult = ExecuteIsList(execCtx, args, argNames, res); break;
                case "is_function": argNames = new List<string> { "value" }; methodResult = ExecuteIsFunction(execCtx, args, argNames, res); break;
                case "append": argNames = new List<string> { "list", "value" }; methodResult = ExecuteAppend(execCtx, args, argNames, res); break;
                case "pop": argNames = new List<string> { "list", "index" }; methodResult = ExecutePop(execCtx, args, argNames, res); break;
                case "extend": argNames = new List<string> { "listA", "listB" }; methodResult = ExecuteExtend(execCtx, args, argNames, res); break;
                case "len": argNames = new List<string> { "list" }; methodResult = ExecuteLen(execCtx, args, argNames, res); break;
                case "run": argNames = new List<string> { "fn" }; methodResult = ExecuteRun(execCtx, args, argNames, res); break;
                default: return res.Failure(new RuntimeError(PosStart, PosEnd, $"No execute_{Name} method defined", Context));
            }

            return methodResult;
        }

        private RTResult ExecuteCommon(Context execCtx, List<RuntimeValue> args, List<string> argNames, RTResult res, Func<Context, RTResult> action)
        {
            res.Register(CheckAndPopulateArgs(argNames, args, execCtx));
            if (res.ShouldReturn()) return res;
            var ret = res.Register(action(execCtx));
            if (res.ShouldReturn()) return res;
            return res.Success(ret);
        }

        // --- Wrappers to match signature ---
        private RTResult ExecutePrint(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            Console.WriteLine(c.SymbolTable.Get("value"));
            return new RTResult().Success(Number.Null);
        });

        private RTResult ExecutePrintRet(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            return new RTResult().Success(new StringVal(c.SymbolTable.Get("value").ToString()));
        });

        private RTResult ExecuteInput(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            string text = Console.ReadLine() ?? "";
            return new RTResult().Success(new StringVal(text));
        });

        private RTResult ExecuteInputInt(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            while (true)
            {
                string text = Console.ReadLine() ?? "";
                if (int.TryParse(text, out int val)) return new RTResult().Success(new Number(val));
                Console.WriteLine($"'{text}' must be an integer. Try again!");
            }
        });

        private RTResult ExecuteClear(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            Console.Clear();
            return new RTResult().Success(Number.Null);
        });

        private RTResult ExecuteIsNumber(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            return new RTResult().Success(c.SymbolTable.Get("value") is Number ? Number.True : Number.False);
        });

        private RTResult ExecuteIsString(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            return new RTResult().Success(c.SymbolTable.Get("value") is StringVal ? Number.True : Number.False);
        });

        private RTResult ExecuteIsList(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            return new RTResult().Success(c.SymbolTable.Get("value") is ListVal ? Number.True : Number.False);
        });

        private RTResult ExecuteIsFunction(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            return new RTResult().Success(c.SymbolTable.Get("value") is BaseFunction ? Number.True : Number.False);
        });

        private RTResult ExecuteAppend(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var list = c.SymbolTable.Get("list");
            var value = c.SymbolTable.Get("value");
            if (list is not ListVal l) return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, "First argument must be list", c));
            l.Elements.Add(value);
            return new RTResult().Success(Number.Null);
        });

        private RTResult ExecutePop(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var list = c.SymbolTable.Get("list");
            var index = c.SymbolTable.Get("index");
            if (list is not ListVal l) return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, "First argument must be list", c));
            if (index is not Number n) return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, "Second argument must be number", c));

            try
            {
                var el = l.Elements[(int)n.Value];
                l.Elements.RemoveAt((int)n.Value);
                return new RTResult().Success(el);
            }
            catch
            {
                return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, "Element at this index could not be removed from list because index is out of bounds", c));
            }
        });

        private RTResult ExecuteExtend(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var listA = c.SymbolTable.Get("listA");
            var listB = c.SymbolTable.Get("listB");
            if (listA is not ListVal lA) return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, "First argument must be list", c));
            if (listB is not ListVal lB) return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, "Second argument must be list", c));
            lA.Elements.AddRange(lB.Elements);
            return new RTResult().Success(Number.Null);
        });

        private RTResult ExecuteLen(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var list = c.SymbolTable.Get("list");
            if (list is not ListVal l) return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, "Argument must be list", c));
            return new RTResult().Success(new Number(l.Elements.Count));
        });

        private RTResult ExecuteRun(Context ctx, List<RuntimeValue> args, List<string> names, RTResult res) => ExecuteCommon(ctx, args, names, res, c => {
            var fn = c.SymbolTable.Get("fn");
            if (fn is not StringVal s) return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, "Argument must be string", c));

            try
            {
                if (!System.IO.File.Exists(s.Value)) return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, $"File not found: {s.Value}", c));
                string script = System.IO.File.ReadAllText(s.Value);
                var (val, err) = Program.Run(s.Value, script);
                if (err != null) return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, $"Failed to finish executing script \"{s.Value}\"\n" + err.AsString(), c));
                return new RTResult().Success(Number.Null);
            }
            catch (Exception ex)
            {
                return new RTResult().Failure(new RuntimeError(PosStart, PosEnd, $"Failed to load script \"{s.Value}\"\n" + ex.Message, c));
            }
        });

        public override RuntimeValue Copy()
        {
            return new BuiltInFunction(Name).SetContext(Context).SetPos(PosStart, PosEnd);
        }

        public override string ToString() => $"<built-in function {Name}>";
    }
}
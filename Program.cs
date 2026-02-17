using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RaLanguage
{
    public class Program
    {
        public static SymbolTable GlobalSymbolTable;

        static Program()
        {
            GlobalSymbolTable = new SymbolTable();
            GlobalSymbolTable.Set("NULL", NumberValue.Null);
            GlobalSymbolTable.Set("FALSE", NumberValue.False);
            GlobalSymbolTable.Set("TRUE", NumberValue.True);
            GlobalSymbolTable.Set("MATH_PI", NumberValue.MathPI);
            GlobalSymbolTable.Set("PRINT", new BuiltInFunctionValue("print"));
            GlobalSymbolTable.Set("PRINT_RET", new BuiltInFunctionValue("print_ret"));
            GlobalSymbolTable.Set("INPUT", new BuiltInFunctionValue("input"));
            GlobalSymbolTable.Set("INPUT_INT", new BuiltInFunctionValue("input_int"));
            GlobalSymbolTable.Set("CLEAR", new BuiltInFunctionValue("clear"));
            GlobalSymbolTable.Set("CLS", new BuiltInFunctionValue("clear"));
            GlobalSymbolTable.Set("IS_NUM", new BuiltInFunctionValue("is_number"));
            GlobalSymbolTable.Set("IS_STR", new BuiltInFunctionValue("is_string"));
            GlobalSymbolTable.Set("IS_LIST", new BuiltInFunctionValue("is_list"));
            GlobalSymbolTable.Set("IS_FUN", new BuiltInFunctionValue("is_function"));
            GlobalSymbolTable.Set("APPEND", new BuiltInFunctionValue("append"));
            GlobalSymbolTable.Set("POP", new BuiltInFunctionValue("pop"));
            GlobalSymbolTable.Set("EXTEND", new BuiltInFunctionValue("extend"));
            GlobalSymbolTable.Set("LEN", new BuiltInFunctionValue("len"));
            GlobalSymbolTable.Set("RUN", new BuiltInFunctionValue("run"));
        }

        public static (RuntimeValue?, Error?) Run(string fn, string text)
        {
            var lexer = new Lexer.Lexer(fn, text);
            var (tokens, error) = lexer.MakeTokens();

            if (error != null)
            {
                return (null, error);
            }

            var parser = new Parser.Parser(tokens);
            var ast = parser.Parse();

            if (ast.Error != null)
            {
                return (null, ast.Error);
            }

            var interpreter = new Interpreter.Interpreter();
            var context = new Context("<program>");
            context.SymbolTable = GlobalSymbolTable;
            var result = interpreter.Visit(ast.Node, context);

            return (result.Value, result.Error);
        }

        public static void Main(string[] args)
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
            Process.GetCurrentProcess().PriorityBoostEnabled = true;
            Console.WriteLine("[Ra Language] Warming up JIT...");

            for (int i = 0; i < 10; i++)
            {
                Run("<stdin>", "VAR a = 5; VAR b = [5, 3, 2]; FUN c() -> VAR eee = 7; VAR d = c; d(); IF (5 == 5) AND (6 == 6) OR (7 == 7) THEN VAR bbb = 5 ELSE VAR bbb = 7");
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Console.Title = "Ra Language | Made by https://github.com/ZygoteCode/";
            string text = File.ReadAllText("main.ra");
            var (result, error) = Run("<stdin>", text);

            if (error != null)
            {
                Console.WriteLine(error.AsString());
            }
            else if (result != null)
            {
                if (result is ListValue l && l.Elements.Count == 1)
                {
                    Console.WriteLine(l.Elements[0]);
                }
                else
                {
                    Console.WriteLine(result);
                }
            }

            Console.WriteLine($"[Ra Language] Execution of \"main.ra\" took {stopwatch.ElapsedMilliseconds}ms.");
            Console.ReadLine();
        }
    }
}
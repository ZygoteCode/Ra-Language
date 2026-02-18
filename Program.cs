using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using System.Diagnostics;

namespace RaLanguage
{
    public class Program
    {
        public static SymbolTable GlobalSymbolTable;

        static Program()
        {
            GlobalSymbolTable = new SymbolTable();
            GlobalSymbolTable.Set("null", NumberValue.Null);
            GlobalSymbolTable.Set("false", NumberValue.False);
            GlobalSymbolTable.Set("true", NumberValue.True);
            GlobalSymbolTable.Set("math_pi", NumberValue.MathPI);
            GlobalSymbolTable.Set("print", new BuiltInFunctionValue("print"));
            GlobalSymbolTable.Set("print_ret", new BuiltInFunctionValue("print_ret"));
            GlobalSymbolTable.Set("input", new BuiltInFunctionValue("input"));
            GlobalSymbolTable.Set("input_int", new BuiltInFunctionValue("input_int"));
            GlobalSymbolTable.Set("clear", new BuiltInFunctionValue("clear"));
            GlobalSymbolTable.Set("cls", new BuiltInFunctionValue("clear"));
            GlobalSymbolTable.Set("is_num", new BuiltInFunctionValue("is_number"));
            GlobalSymbolTable.Set("is_str", new BuiltInFunctionValue("is_string"));
            GlobalSymbolTable.Set("is_list", new BuiltInFunctionValue("is_list"));
            GlobalSymbolTable.Set("is_fn", new BuiltInFunctionValue("is_function"));
            GlobalSymbolTable.Set("append", new BuiltInFunctionValue("append"));
            GlobalSymbolTable.Set("pop", new BuiltInFunctionValue("pop"));
            GlobalSymbolTable.Set("extend", new BuiltInFunctionValue("extend"));
            GlobalSymbolTable.Set("len", new BuiltInFunctionValue("len"));
            GlobalSymbolTable.Set("run", new BuiltInFunctionValue("run"));
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
            Console.Title = "Ra Language | Made by https://github.com/ZygoteCode/";
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
            Process.GetCurrentProcess().PriorityBoostEnabled = true;

            Console.WriteLine("[Ra Language] Support the project on GitHub: https://github.com/ZygoteCode/RaLanguage/");
            Console.WriteLine("[Ra Language] Warming up JIT...");

            for (int i = 0; i < 10; i++)
            {
                Run("<stdin>", "VAR a = 5; VAR b = [5, 3, 2]; FUN c() -> VAR eee = 7; VAR d = c; d(); IF (5 == 5) AND (6 == 6) OR (7 == 7) THEN VAR bbb = 5 ELSE VAR bbb = 7");
            }

            while (true)
            {
                start: Console.WriteLine("Please, choose from the following execution methods:\r\n" +
                 "\r\n[1] Execute one time" +
                 "\r\n[2] Execute every time you press ENTER" +
                 "\r\n[3] Hot restart execution");

                string input = Console.ReadLine();

                if (input != "1" && input != "2" && input != "3")
                {
                    continue;
                }

                Console.Clear();

                switch (input)
                {
                    case "1":
                        string text = File.ReadAllText("main.ra");
                        Stopwatch stopwatch = new Stopwatch();
                        stopwatch.Start();

                        var (result, error) = Run("<stdin>", text);

                        if (error != null)
                        {
                            Console.WriteLine(error.AsString());
                        }
                        else
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

                        Console.WriteLine($"[Ra Language] Execution of \"main.ra\" took {stopwatch.ElapsedMilliseconds}ms / {stopwatch.ElapsedTicks} ticks / {stopwatch.Elapsed.TotalNanoseconds} nanoseconds.");
                        Console.ReadLine();
                        Console.Clear();
                        continue;
                    case "2":
                        repeat: string text1 = File.ReadAllText("main.ra");
                        Stopwatch stopwatch1 = new Stopwatch();
                        stopwatch1.Start();

                        var (result1, error1) = Run("<stdin>", text1);

                        if (error1 != null)
                        {
                            Console.WriteLine(error1.AsString());
                        }
                        else
                        {
                            if (result1 is ListValue l && l.Elements.Count == 1)
                            {
                                Console.WriteLine(l.Elements[0]);
                            }
                            else
                            {
                                Console.WriteLine(result1);
                            }
                        }

                        Console.WriteLine($"[Ra Language] Execution of \"main.ra\" took {stopwatch1.ElapsedMilliseconds}ms / {stopwatch1.ElapsedTicks} ticks / {stopwatch1.Elapsed.TotalNanoseconds} nanoseconds.");
                        Console.WriteLine("[Ra Language] Press ENTER to execute again, or DEL/BACKSPACE to go back.");
                        console:  ConsoleKey readKey = Console.ReadKey().Key;
                        
                        if (readKey.Equals(ConsoleKey.Enter))
                        {
                            Console.Clear();
                            goto repeat;
                        }
                        else if (readKey.Equals(ConsoleKey.Delete) || readKey.Equals(ConsoleKey.Backspace))
                        {
                            Console.Clear();
                            goto start;
                        }

                        goto console;
                    case "3":
                        string originalText = "";

                        while (true)
                        {
                            Thread.Sleep(1);

                            try
                            {
                                string newText = File.ReadAllText("main.ra");

                                if (newText != originalText)
                                {
                                    originalText = newText;
                                }
                                else
                                {
                                    continue;
                                }

                                Console.Clear();
                                Stopwatch stopwatch2 = new Stopwatch();
                                stopwatch2.Start();

                                var (result2, error2) = Run("<stdin>", originalText);

                                if (error2 != null)
                                {
                                    Console.WriteLine(error2.AsString());
                                }
                                else
                                {
                                    if (result2 is ListValue l && l.Elements.Count == 1)
                                    {
                                        Console.WriteLine(l.Elements[0]);
                                    }
                                    else
                                    {
                                        Console.WriteLine(result2);
                                    }
                                }

                                Console.WriteLine($"[Ra Language] Execution of \"main.ra\" took {stopwatch2.ElapsedMilliseconds}ms / {stopwatch2.ElapsedTicks} ticks / {stopwatch2.Elapsed.TotalNanoseconds} nanoseconds.");
                            }
                            catch
                            {

                            }
                        }
                    default:
                        Process.GetCurrentProcess().Kill();
                        break;
                }
            }
        }
    }
}
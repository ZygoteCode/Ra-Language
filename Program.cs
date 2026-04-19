using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Visitors.Imports;
using System.Diagnostics;

namespace RaLanguage
{
    public class Program
    {
        public static SymbolTable GlobalSymbolTable;

        static Program()
        {
            InitializeSymbolTable();
        }

        private static void InitializeSymbolTable()
        {
            GlobalSymbolTable = new SymbolTable();
            GlobalSymbolTable.Set("print", new BuiltInFunctionValue("print"));
            
            string basePath = Directory.GetCurrentDirectory();
            ImportNodeVisitor.InitializeModuleManager(basePath);
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

            var currentProcess = Process.GetCurrentProcess();
            currentProcess.PriorityClass = ProcessPriorityClass.RealTime;
            currentProcess.PriorityBoostEnabled = true;

            Console.WriteLine("[Ra Language] Support the project on GitHub: https://github.com/ZygoteCode/RaLanguage/");
            Console.WriteLine("[Ra Language] Warming up JIT...");

            for (int i = 0; i < 1000; i++)
            {
                Run("<stdin>", "var a = 5; var b = [5, 3, 2]; fn c() => var eee = 7; var d = c; d(); if 5 == 5 && 6 == 6 or 7 == 7: var bbb = 5 else: var bbb = 7; typeof null; nameof a; var aaa = \"testing\"; const nevertouch = 7; final bbbbbbb;");
            }

            bool exitProgram = false;
            while (!exitProgram)
            {
                Console.WriteLine("Please, choose from the following execution methods:\r\n" +
                                 "\r\n[1] Execute one time" +
                                 "\r\n[2] Execute every time you press ENTER" +
                                 "\r\n[3] Hot restart execution" +
                                 "\r\n[0] Exit");

                string input = Console.ReadLine();

                if (input == "0") break;
                if (input != "1" && input != "2" && input != "3") continue;

                Console.Clear();

                switch (input)
                {
                    case "1":
                        ExecuteMainFile();
                        Console.WriteLine("\nPress ENTER to return to menu...");
                        Console.ReadLine();
                        Console.Clear();
                        break;

                    case "2":
                        bool backToMenu = false;
                        while (!backToMenu)
                        {
                            ExecuteMainFile();
                            Console.WriteLine("\n[Ra Language] Press ENTER to execute again, or DEL/BACKSPACE to go back.");

                            bool validKey = false;
                            while (!validKey)
                            {
                                ConsoleKey readKey = Console.ReadKey(true).Key;
                                if (readKey == ConsoleKey.Enter)
                                {
                                    Console.Clear();
                                    validKey = true;
                                }
                                else if (readKey == ConsoleKey.Delete || readKey == ConsoleKey.Backspace)
                                {
                                    Console.Clear();
                                    validKey = true;
                                    backToMenu = true;
                                }
                            }
                        }
                        break;

                    case "3":
                        Console.WriteLine("[Ra Language] Hot Restart active. Monitoring 'main.ra'...");
                        string lastContent = "";
                        while (true)
                        {
                            Thread.Sleep(100);
                            try
                            {
                                string currentContent = File.ReadAllText("main.ra");
                                if (currentContent != lastContent)
                                {
                                    Console.Clear();
                                    lastContent = currentContent;
                                    ExecuteMainFile(currentContent);
                                }
                            }
                            catch
                            {

                            }
                        }

                    default:
                        currentProcess.Kill();
                        break;
                }
            }
        }

        private static void ExecuteMainFile(string content = null)
        {
            InitializeSymbolTable();
            try
            {
                string text = content ?? File.ReadAllText("main.ra");
                Stopwatch sw = Stopwatch.StartNew();

                var (result, error) = Run("<stdin>", text);
                sw.Stop();

                if (error != null)
                {
                    Console.WriteLine(error.ToString());
                }

                Console.WriteLine($"[Ra Language] Execution took {sw.ElapsedMilliseconds}ms / {sw.ElapsedTicks} ticks / {sw.Elapsed.TotalNanoseconds}ns.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Could not read file: {ex.Message}");
            }
        }
    }
}
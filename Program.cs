using RaLanguage.Errors;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Namespaces;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Visitors.Imports;
using System.Collections.Generic;
using System.Diagnostics;

namespace RaLanguage
{
    public class Program
    {
        public static SymbolTable GlobalSymbolTable;
        private static string[] _builtInFunctions = new string[]
        {
            "print",
            "print_ret",
            "exists",
            "field_exists",
            "drop",
            "is_public",
            "is_field_public",
            "is_field_static",
            "annotations_of",
            "has_annotation",
            "annotation_arg",
            "annotation_targets",
            "validate",
            "validate_target",
            "validate_deferred",
            "coerce_value",
            "run_tests",
            "sleep",
            "yield_now",
            "gather",
            "race",
            "timeout",
            "cancel",
            "is_cancelled",
            "is_completed",
            "task_status",
            "current_task",
            "channel",
            "channel_send",
            "channel_recv",
            "channel_close",
            "channel_is_closed",
            "channel_count",
            "to_task",
            "run_blocking",
            "task_result",
            "select"
        };

        static Program()
        {
            InitializeSymbolTable();
        }

        public static SymbolTable BuiltinSymbolTable;

        private static void InitializeSymbolTable()
        {
            BuiltinSymbolTable = new SymbolTable();

            foreach (string builtInFunction in _builtInFunctions)
            {
                BuiltinSymbolTable.Set(builtInFunction, new BuiltInFunctionValue(builtInFunction));
            }

            BuiltInAnnotations.RegisterAll(BuiltinSymbolTable);

            BuiltInRegistry.EnsureInitialized();
            foreach (string registryBuiltin in BuiltInRegistry.AllNames)
            {
                if (BuiltinSymbolTable.GetEntry(registryBuiltin) == null)
                {
                    BuiltinSymbolTable.Set(registryBuiltin, new BuiltInFunctionValue(registryBuiltin));
                }
            }
            MetadataRegistry.Global.Clear();
            NamespaceRegistry.Global.Clear();

            GlobalSymbolTable = new SymbolTable(BuiltinSymbolTable);

            string projectRoot = Directory.GetCurrentDirectory();
            string stdRoot = ResolveStdRoot(projectRoot);
            ImportNodeVisitor.InitializeModuleManager(projectRoot, stdRoot, () => BuiltinSymbolTable);
            ImportNodeVisitor.ResetCache();
        }

        private static string ResolveStdRoot(string projectRoot)
        {
            string exeStd = Path.Combine(AppContext.BaseDirectory, "std");
            if (Directory.Exists(exeStd))
            {
                return exeStd;
            }

            string projectStd = Path.Combine(projectRoot, "std");
            if (Directory.Exists(projectStd))
            {
                return projectStd;
            }

            return exeStd;
        }

        public static (RuntimeValue?, Error?) Run(string fn, string text)
        {
            var lexer = new Lexer.Lexer(fn, text);
            var (tokens, lexerDiagnostics) = lexer.MakeTokens();

            if (lexerDiagnostics.HasErrors)
            {
                PrintDiagnostics(lexerDiagnostics);
                Console.WriteLine($"[Ra Language] Compilation aborted: lexing failed ({lexerDiagnostics.Summary()}).");
                return (null, null);
            }

            var parser = new Parser.Parser(tokens);
            var parseResult = parser.Parse();

            if (parseResult.HasErrors)
            {
                PrintDiagnostics(parseResult.Diagnostics);
                Console.WriteLine($"[Ra Language] Compilation aborted: parsing failed ({parseResult.Diagnostics.Summary()}).");
                return (null, null);
            }

            if (lexerDiagnostics.HasWarnings) PrintDiagnostics(lexerDiagnostics, onlyWarnings: true);
            if (parseResult.Diagnostics.HasWarnings) PrintDiagnostics(parseResult.Diagnostics, onlyWarnings: true);

            DeriveTransformer.Apply(parseResult.Node);

            var staticDiagnostics = StaticAnalyzer.Analyze(parseResult.Node, GlobalSymbolTable);
            if (staticDiagnostics.Count > 0)
            {
                Console.WriteLine($"[StaticAnalyzer] {staticDiagnostics.Count} warning(s) found:");
                foreach (var d in staticDiagnostics) Console.WriteLine(d);
            }

            var interpreter = new Interpreter.Interpreter();
            var context = new Context(fn);
            context.SymbolTable = GlobalSymbolTable;
            var result = interpreter.Visit(parseResult.Node, context);

            return (result.Value, result.Error);
        }

        private const int MaxRenderedDiagnostics = 5;

        private static void PrintDiagnostics(Errors.DiagnosticBag bag, bool onlyWarnings = false)
        {
            int shown = 0;
            int total = 0;
            foreach (var d in bag.Diagnostics)
            {
                if (onlyWarnings && d.Severity != Errors.DiagnosticSeverity.Warning) continue;
                total++;
                if (shown >= MaxRenderedDiagnostics) continue;
                Console.WriteLine(d);
                Console.WriteLine();
                shown++;
            }
            int omitted = total - shown;
            if (omitted > 0)
            {
                Console.WriteLine($"... {omitted} additional diagnostic{(omitted == 1 ? "" : "s")} omitted ...");
                Console.WriteLine();
            }
        }

        public static void Main(string[] args)
        {
            Console.Title = "Ra Language | Made by https://github.com/ZygoteCode/";

            var currentProcess = Process.GetCurrentProcess();
            currentProcess.PriorityClass = ProcessPriorityClass.RealTime;
            currentProcess.PriorityBoostEnabled = true;

            if (args.Length > 0)
            {
                string totalArgs = "";

                foreach (string arg in args)
                {
                    if (totalArgs == "")
                    {
                        totalArgs = arg;
                    }
                    else
                    {
                        totalArgs += $" {arg}";
                    }
                }

                while (totalArgs.StartsWith(" ") || totalArgs.StartsWith("\"") || totalArgs.StartsWith("'") || totalArgs.StartsWith('\t'))
                {
                    totalArgs = totalArgs.Substring(1);
                }

                while (totalArgs.EndsWith(" ") || totalArgs.EndsWith("\"") || totalArgs.EndsWith("'") || totalArgs.EndsWith('\t'))
                {
                    totalArgs = totalArgs.Substring(0, totalArgs.Length - 1);
                }

                if (!File.Exists(totalArgs))
                {
                    Console.WriteLine("The specified file does not exist.");
                    return;
                }

                if (!Path.GetExtension(totalArgs).ToLower().Equals(".ra"))
                {
                    Console.WriteLine("The specified file has not a valid extension.");
                    return;
                }

                ExecuteMainFile(totalArgs, false);
                return;
            }

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
                                    ExecuteMainFile();
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

        private static void ExecuteMainFile(string fileName = "main.ra", bool diagnostics = true)
        {
            InitializeSymbolTable();
            try
            {
                string text = File.ReadAllText(fileName);
                Stopwatch sw = Stopwatch.StartNew();

                var (result, error) = Run(fileName, text);
                sw.Stop();

                if (error != null)
                {
                    Console.WriteLine(error.ToString());
                }

                if (diagnostics)
                {
                    Console.WriteLine($"[Ra Language] Execution took {sw.ElapsedMilliseconds}ms / {sw.ElapsedTicks} ticks / {sw.Elapsed.TotalNanoseconds}ns.");
                }
            }
            catch (Exception ex)
            {
                if (diagnostics)
                {
                    Console.WriteLine($"[Error] Could not read file: {ex.Message}");
                }
            }
        }
    }
}
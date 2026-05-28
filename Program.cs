using RaLanguage.Errors;
using RaLanguage.Interpreter;
using RaLanguage.Interpreter.Archive;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Pipeline;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Namespaces;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Visitors.Imports;
using RaLanguage.Interpreter.Vm;
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

        public static void InitializeSymbolTable()
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
            RegisterBuiltinAdts(BuiltinSymbolTable);
            MetadataRegistry.Global.Clear();
            NamespaceRegistry.Global.Clear();
            // M88: clear the cross-module mutated-names registry so a
            // previous run's loaded modules do not over-constrain the
            // next run's LICM. Same lifetime as `MetadataRegistry.Global`.
            Interpreter.Modules.ModuleManager.GlobalMutatedNames.Clear();
            // Wipe the process-wide extension-field slot allocator +
            // descriptor table. Without this, menu mode [2]/[3] cycles
            // would re-run the same `extend T { var x }` block on each
            // iteration and mint fresh slot indices — they climb
            // unbounded, ExtFieldSlots arrays grow proportionally, and
            // per-instance footprint balloons. Reset() is idempotent.
            Interpreter.Runtime.ExtensionFieldStorage.Reset();

            GlobalSymbolTable = new SymbolTable(BuiltinSymbolTable);

            string projectRoot = Directory.GetCurrentDirectory();
            string stdRoot = ResolveStdRoot(projectRoot);
            ImportNodeVisitor.InitializeModuleManager(projectRoot, stdRoot, () => BuiltinSymbolTable);
            ImportNodeVisitor.ResetCache();
        }

        // Pre-installs the built-in algebraic data types every Ra program
        // can rely on: `Result<T, E>` and `Option<T>`. They live in the
        // builtin symbol table so user code does not have to import them
        // and so the `?` operator's reliance on the `Result` shape is
        // guaranteed.
        private static void RegisterBuiltinAdts(SymbolTable builtins)
        {
            // Result<T, E> { Ok(T), Err(E) }
            var resultVariants = new List<RaLanguage.Interpreter.Values.Primitives.EnumVariantInfo>
            {
                new("Ok", 0, 0,
                    new List<RaLanguage.Types.TypeDescriptor> { RaLanguage.Types.TypeDescriptor.TypeParameter("T") }),
                new("Err", 1, 1,
                    new List<RaLanguage.Types.TypeDescriptor> { RaLanguage.Types.TypeDescriptor.TypeParameter("E") }),
            };
            var resultEnum = new RaLanguage.Interpreter.Values.Primitives.EnumTypeValue(
                "Result", resultVariants,
                new List<string> { "T", "E" });
            builtins.Set("Result", resultEnum);

            // Option<T> { Some(T), None }
            var optionVariants = new List<RaLanguage.Interpreter.Values.Primitives.EnumVariantInfo>
            {
                new("Some", 0, 0,
                    new List<RaLanguage.Types.TypeDescriptor> { RaLanguage.Types.TypeDescriptor.TypeParameter("T") }),
                new("None", 1, 1, null),
            };
            var optionEnum = new RaLanguage.Interpreter.Values.Primitives.EnumTypeValue(
                "Option", optionVariants,
                new List<string> { "T" });
            builtins.Set("Option", optionEnum);
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

        public static ValueResult Run(string fn, string text)
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

            // M89: lower "simple" match expressions to equivalent
            // if/elif/else chains. The IR compiler optimises if-chains
            // aggressively (jump-table layout, typed-accumulator merging,
            // peephole on comparisons); the visitor path for match pays
            // per-arm dispatch + a fresh SymbolTable. Eligibility is
            // conservative — see MatchSimplifier — so any pattern with
            // bindings / guards / nominal shape stays on the visitor path.
            Interpreter.Runtime.Patterns.MatchSimplifier.Apply(parseResult.Node);

            // Resolver pass: assigns a BindingId / BindingKind to every identifier
            // node in the tree and computes static closure captures on each
            // FunctionDefinitionNode. Annotations are advisory — runtime visitors
            // still tolerate Unresolved bindings via the existing name-lookup
            // fallback — so this pass is safe to run unconditionally. Must come
            // AFTER DeriveTransformer (which can rewrite the AST) and BEFORE the
            // analyzers that consume the post-derive shape.
            Resolver.Resolve(parseResult.Node);

            var staticDiagnostics = StaticAnalyzer.Analyze(parseResult.Node, GlobalSymbolTable);
            if (staticDiagnostics.Count > 0)
            {
                Console.WriteLine($"[StaticAnalyzer] {staticDiagnostics.Count} warning(s) found:");
                foreach (var d in staticDiagnostics) Console.WriteLine(d);
            }

            var narrowingDiagnostics = Interpreter.Runtime.Narrowing.NarrowingAnalyzer.Analyze(parseResult.Node);
            if (narrowingDiagnostics.Count > 0)
            {
                Console.WriteLine($"[NarrowingAnalyzer] {narrowingDiagnostics.Count} warning(s) found:");
                foreach (var d in narrowingDiagnostics)
                    Console.WriteLine(d.ToString().Replace("[StaticAnalyzer]", "[NarrowingAnalyzer]"));
            }

            var borrowDiagnostics = Interpreter.Runtime.Borrowing.BorrowChecker.Analyze(parseResult.Node);
            if (borrowDiagnostics.Count > 0)
            {
                Console.WriteLine($"[BorrowChecker] {borrowDiagnostics.Count} issue(s) found:");
                foreach (var d in borrowDiagnostics)
                    Console.WriteLine(d.ToString().Replace("[StaticAnalyzer]", "[BorrowChecker]"));
            }

            var interpreter = new Interpreter.Interpreter();
            var context = new Context(fn);
            context.SymbolTable = GlobalSymbolTable;
            // Top-level host frame: the dispatch loop awaits real ValueTask
            // continuations, so `await` inside Ra code suspends without
            // pinning a worker.
            var script = IrCompiler.CompileScript(parseResult.Node, fn);
            var vm = new VmExecutor(interpreter);
            var task = vm.RunScript(script, context);
            var result = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
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
            // Run the entry on a worker thread with a fat stack. The dispatch
            // loop is iterative, but user-level function calls still recur
            // through Apply helpers; 32 MB of headroom keeps deep user
            // recursion safe.
            Exception? threadEx = null;
            var worker = new System.Threading.Thread(() =>
            {
                try { MainCore(args); }
                catch (Exception ex) { threadEx = ex; }
            }, 32 * 1024 * 1024);
            worker.Start();
            worker.Join();
            if (threadEx != null) throw threadEx;
        }

        private static void MainCore(string[] args)
        {
            Console.Title = "Ra Language | Made by https://github.com/ZygoteCode/";

            // The fiber runtime currently uses sync-over-async wait inside `await`
            // (each pending await pins a thread-pool worker). With the default min
            // thread count (~CPU count) and 1-2 threads/sec injection rate, fan-out
            // of more than (min-count) blocking fibers serializes. Pre-warm the pool
            // so high-fan-out `gather(...)` patterns run truly in parallel. We do
            // not lower the max; large values just expand the elastic ceiling.
            int cpu = Environment.ProcessorCount;
            int minWorkers = Math.Max(128, cpu * 16);
            int minIo = Math.Max(128, cpu * 16);
            try
            {
                System.Threading.ThreadPool.GetMinThreads(out var curW, out var curIo);
                if (minWorkers < curW) minWorkers = curW;
                if (minIo < curIo) minIo = curIo;
                System.Threading.ThreadPool.SetMinThreads(minWorkers, minIo);
            }
            catch { }

            var currentProcess = Process.GetCurrentProcess();

            if (args.Length > 0)
            {
                if (args.Length == 1 && string.Equals(args[0], "--bench", StringComparison.OrdinalIgnoreCase))
                {
                    RunMicrobenchmark();
                    return;
                }

                // M35: --dump-ir <file.ra> prints the compiled IR + constant
                // pool + Names table for the given source. Read-only debug
                // aid; does not execute the script.
                if (args.Length == 2 && string.Equals(args[0], "--dump-ir", StringComparison.OrdinalIgnoreCase))
                {
                    DumpIr(args[1]);
                    return;
                }

                // M54: --dump-cfg <file.ra> prints the control-flow graph
                // for the compiled script body.
                if (args.Length == 2 && string.Equals(args[0], "--dump-cfg", StringComparison.OrdinalIgnoreCase))
                {
                    DumpCfg(args[1]);
                    return;
                }


                // M50: --repl interactive top-level eval. Reads a line,
                // compiles + executes against the persistent
                // GlobalSymbolTable so subsequent lines see bindings made
                // earlier. Ctrl+C / Ctrl+D / `exit` to quit.
                if (args.Length == 1 && string.Equals(args[0], "--repl", StringComparison.OrdinalIgnoreCase))
                {
                    RunRepl();
                    return;
                }

                // --compile <entry.ra> [-o output.rac] [--no-compress]
                // Builds a .rac archive from the given entry, walking
                // imports transitively and validating each module
                // through the lex/parse pipeline.
                if (args.Length >= 2 && string.Equals(args[0], "--compile", StringComparison.OrdinalIgnoreCase))
                {
                    CompileArchiveCli(args);
                    return;
                }

                // --run-archive <file.rac>
                // Loads a .rac archive into the in-process runtime and
                // executes its entry module.
                if (args.Length == 2 && string.Equals(args[0], "--run-archive", StringComparison.OrdinalIgnoreCase))
                {
                    RunArchiveCli(args[1]);
                    return;
                }

                // --dump-archive-source <file.rac> <module-index>
                // Print the (possibly tree-shaken) source bytes of a
                // bundled module to stdout. Tree-shake debugging.
                if (args.Length == 3 && string.Equals(args[0], "--dump-archive-source", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(args[2], out int mi))
                    {
                        DumpArchiveSourceCli(args[1], mi);
                    }
                    else
                    {
                        Console.WriteLine($"[Ra Language] --dump-archive-source: '{args[2]}' is not an integer module index");
                        Environment.ExitCode = 1;
                    }
                    return;
                }

                // --bench-archive-open <file.rac> [iter]
                // Warm up + measure RacReader.Open over N iterations.
                // Validates the v1.1 "<1ms open" target. Skips execution
                // entirely; only the mmap + header + section-dir +
                // manifest path is timed.
                if (args.Length >= 2 && args.Length <= 3
                    && string.Equals(args[0], "--bench-archive-open", StringComparison.OrdinalIgnoreCase))
                {
                    int iter = 1000;
                    if (args.Length == 3 && int.TryParse(args[2], out int n) && n > 0) iter = n;
                    BenchArchiveOpenCli(args[1], iter);
                    return;
                }

                // --inspect-archive <file.rac>
                // Pretty-prints the archive header + manifest + section
                // directory.
                if (args.Length == 2 && string.Equals(args[0], "--inspect-archive", StringComparison.OrdinalIgnoreCase))
                {
                    InspectArchiveCli(args[1]);
                    return;
                }

                // Auto-detect `.rac` positional argument so `ra foo.rac`
                // just works. We keep this *after* the explicit flags so
                // an `--inspect-archive foo.rac` is not eaten here.
                if (args.Length == 1 && args[0].EndsWith(".rac", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(args[0]))
                {
                    RunArchiveCli(args[0]);
                    return;
                }

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

        // M54: dump CFG for a script. Same parse/compile pipeline as
        // --dump-ir then walks the basic-block decomposition.
        private static void DumpCfg(string path)
        {
            if (!File.Exists(path)) { Console.WriteLine($"[Ra Language] --dump-cfg: file not found: {path}"); return; }
            string text = File.ReadAllText(path);
            var lexer = new Lexer.Lexer(path, text);
            var (tokens, diag) = lexer.MakeTokens();
            if (diag.HasErrors) { PrintDiagnostics(diag); return; }
            var parser = new Parser.Parser(tokens);
            var parseResult = parser.Parse();
            if (parseResult.HasErrors) { PrintDiagnostics(parseResult.Diagnostics); return; }
            DeriveTransformer.Apply(parseResult.Node);
            Resolver.Resolve(parseResult.Node);
            InitializeSymbolTable();
            var fn = IrCompiler.CompileScript(parseResult.Node, path);
            // Note: fn.Analysis was attached during CompileScript and
            // reflects the PRE-rewrite SSA. Re-build below to show the
            // POST-rewrite shape (so dump-cfg prints the actual code
            // the dispatch loop will execute).
            var cfg = Interpreter.IR.Analysis.CfgBuilder.Build(fn);
            Console.Write(cfg.Dump());
            var dom = Interpreter.IR.Analysis.Dominators.Compute(cfg);
            Console.Write(dom.Dump());
            var ssa = Interpreter.IR.Analysis.SsaForm.Build(cfg, dom);
            Console.Write(ssa.Dump());
            var opt = Interpreter.IR.Analysis.SsaOptimizer.Run(ssa);
            Console.Write(opt.Dump());
            var gvn = Interpreter.IR.Analysis.GlobalValueNumbering.Run(ssa);
            Console.Write(gvn.Dump());
            var loops = Interpreter.IR.Analysis.LoopAnalysis.Run(ssa);
            Console.Write(loops.Dump());
            var sccp = Interpreter.IR.Analysis.Sccp.Run(ssa);
            Console.Write(sccp.Dump());
        }

        // M50: interactive REPL. Persistent GlobalSymbolTable across
        // inputs — `var x = 5` on one line makes `x` available on the
        // next. Each input is compiled + executed as a synthetic
        // top-level script via Run(). Errors print and the loop
        // continues. `exit` / Ctrl+C terminate.
        private static void RunRepl()
        {
            Console.WriteLine("[Ra Language] REPL. Type `exit` to quit.");
            InitializeSymbolTable();
            int counter = 0;
            while (true)
            {
                Console.Write(">>> ");
                string? line;
                try { line = Console.ReadLine(); }
                catch { return; }
                if (line == null) return;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line == "exit" || line == "quit") return;
                // Append `;` if user omitted — most Ra statements need it.
                if (!line.EndsWith(';') && !line.EndsWith('}')) line += ";";
                var (result, error) = Run($"<repl#{++counter}>", line);
                if (error != null) Console.WriteLine(error.ToString());
                else if (result != null && result.Type != Interpreter.Values.RuntimeValueType.Null)
                    Console.WriteLine(result.ToString());
            }
        }

        // M35: --dump-ir entry point. Runs the lex/parse/derive/resolve
        // pipeline then prints the IR for the script body. Skips static
        // analysis warnings + execution.
        private static void DumpIr(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Ra Language] --dump-ir: file not found: {path}");
                return;
            }
            string text = File.ReadAllText(path);
            var lexer = new Lexer.Lexer(path, text);
            var (tokens, diag) = lexer.MakeTokens();
            if (diag.HasErrors) { PrintDiagnostics(diag); return; }
            var parser = new Parser.Parser(tokens);
            var parseResult = parser.Parse();
            if (parseResult.HasErrors) { PrintDiagnostics(parseResult.Diagnostics); return; }
            DeriveTransformer.Apply(parseResult.Node);
            Resolver.Resolve(parseResult.Node);
            InitializeSymbolTable();
            var fn = IrCompiler.CompileScript(parseResult.Node, path);
            Console.WriteLine($"# IR dump for {path}");
            Console.WriteLine($"# LocalCount={fn.LocalCount} SlotCount={fn.SlotCount} Arity={fn.Arity} Code.Length={fn.Code.Length}");
            Console.WriteLine($"# Profile: InvocationCount={fn.InvocationCount} LoopBackEdgeCount={fn.LoopBackEdgeCount} IsHot={fn.IsHot}");
            Console.WriteLine();
            Console.WriteLine("# constants");
            for (int i = 0; i < fn.Consts.Length; i++)
                Console.WriteLine($"  c{i}: {fn.Consts[i]?.ToString() ?? "<null>"}");
            Console.WriteLine();
            Console.WriteLine("# names");
            for (int i = 0; i < fn.Names.Length; i++)
                Console.WriteLine($"  n{i}: {fn.Names[i]}");
            Console.WriteLine();
            // M40: dump the inferred per-slot type lattice.
            if (fn.SlotTypeHints != null)
            {
                Console.WriteLine("# slot type hints (M40)");
                for (int i = 0; i < fn.SlotTypeHints.Length; i++)
                {
                    var t = fn.SlotTypeHints[i];
                    if (t != RuntimeValueType.Null)
                        Console.WriteLine($"  s{i}: {t}");
                }
                Console.WriteLine();
            }

            Console.WriteLine("# code");
            for (int pc = 0; pc < fn.Code.Length; pc++)
            {
                uint instr = fn.Code[pc];
                var op = Encoding.DecodeOp(instr);
                byte a = Encoding.A(instr);
                byte b = Encoding.B(instr);
                byte c = Encoding.C(instr);
                ushort imm = Encoding.Imm16(instr);
                Console.WriteLine($"  {pc:0000}: {op,-18} a={a,-3} b={b,-3} c={c,-3} imm16={imm}");
            }
        }

        private static void RunMicrobenchmark()
        {
            // Two-phase microbenchmark: warmup to populate JIT + AOT inlining decisions, then
            // measured runs. Reports wall-clock time and managed-heap allocation delta per
            // benchmark so optimization passes have a numerical regression signal.
            string[] benches = { "bench_hotloop.ra", "bench_arithmetic.ra", "bench_counter.ra", "bench_hybrid_read.ra", "bench_while.ra", "bench_branchy.ra", "bench_dirty.ra", "bench_bindcmp.ra", "bench_invariant.ra" };

            Console.WriteLine("[Ra Language] Microbenchmark mode.");
            foreach (var bench in benches)
            {
                if (!File.Exists(bench))
                {
                    Console.WriteLine($"  skip: {bench} not found in {Directory.GetCurrentDirectory()}");
                    continue;
                }

                string text = File.ReadAllText(bench);

                // Warmup
                for (int i = 0; i < 3; i++)
                {
                    InitializeSymbolTable();
                    Run(bench, text);
                }

                const int Iterations = 5;
                long bestMs = long.MaxValue;
                long totalMs = 0;
                long totalTicks = 0;
                long allocBefore = GC.GetTotalAllocatedBytes(precise: false);

                for (int i = 0; i < Iterations; i++)
                {
                    InitializeSymbolTable();
                    var sw = Stopwatch.StartNew();
                    Run(bench, text);
                    sw.Stop();
                    if (sw.ElapsedMilliseconds < bestMs) bestMs = sw.ElapsedMilliseconds;
                    totalMs += sw.ElapsedMilliseconds;
                    totalTicks += sw.ElapsedTicks;
                }

                long allocAfter = GC.GetTotalAllocatedBytes(precise: false);
                double allocPerRunMb = (allocAfter - allocBefore) / (double)Iterations / 1_048_576.0;
                double avgMs = totalMs / (double)Iterations;
                double avgTicks = totalTicks / (double)Iterations;

                Console.WriteLine($"  {bench}: best={bestMs}ms avg={avgMs:F1}ms avg_ticks={avgTicks:F0} alloc/run={allocPerRunMb:F2}MB");
            }
        }

        // Archive CLI: `--compile <entry.ra> [-o output.rac] [--no-compress]`.
        // Parses flags, dispatches to RacPackager, prints summary.
        private static void CompileArchiveCli(string[] args)
        {
            string entry = args[1];
            string? output = null;
            bool compress = true;
            bool verbose = false;
            bool treeShake = true;
            bool sharedConstPool = true;

            for (int i = 2; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-o", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    output = args[++i];
                }
                else if (string.Equals(args[i], "--no-compress", StringComparison.OrdinalIgnoreCase))
                {
                    compress = false;
                }
                else if (string.Equals(args[i], "--verbose", StringComparison.OrdinalIgnoreCase))
                {
                    verbose = true;
                }
                else if (string.Equals(args[i], "--no-tree-shake", StringComparison.OrdinalIgnoreCase))
                {
                    treeShake = false;
                }
                else if (string.Equals(args[i], "--no-const-pool", StringComparison.OrdinalIgnoreCase))
                {
                    sharedConstPool = false;
                }
                else
                {
                    Console.WriteLine($"[Ra Language] --compile: unknown flag '{args[i]}'");
                    return;
                }
            }

            var opts = new RacBuildOptions
            {
                EntryFile = entry,
                OutputFile = output ?? "",
                Compress = compress,
                Verbose = verbose,
                TreeShakeStd = treeShake,
                SharedConstPoolEnabled = sharedConstPool,
            };

            var r = RacPackager.Build(opts);
            if (!r.Success)
            {
                Console.WriteLine("[Ra Language] Archive build FAILED.");
                foreach (var e in r.Errors) Console.WriteLine($"  error: {e}");
                foreach (var w in r.Warnings) Console.WriteLine($"  warn : {w}");
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine($"[Ra Language] Archive built: {r.OutputPath}");
            Console.WriteLine($"  modules : {r.ModuleCount}");
            Console.WriteLine($"  size    : {r.OutputSize:N0} bytes");
            Console.WriteLine($"  elapsed : {r.Elapsed.TotalMilliseconds:F1} ms");
            foreach (var w in r.Warnings) Console.WriteLine($"  warn : {w}");
        }

        // Archive CLI: `--run-archive <file.rac>` or `<file.rac>` positional.
        private static void RunArchiveCli(string archivePath)
        {
            if (!File.Exists(archivePath))
            {
                Console.WriteLine($"[Ra Language] archive not found: {archivePath}");
                Environment.ExitCode = 1;
                return;
            }
            var r = RacRunner.Run(new RacRunOptions
            {
                ArchivePath = archivePath,
                Diagnostics = true,
            });
            if (!r.Loaded)
            {
                Console.WriteLine($"[Ra Language] failed to load archive '{archivePath}':");
                foreach (var e in r.LoadErrors) Console.WriteLine($"  {e}");
                Environment.ExitCode = 1;
                return;
            }
            if (r.RuntimeError != null)
            {
                Console.WriteLine(r.RuntimeError.ToString());
            }
            Console.WriteLine(
                $"[Ra Language] Archive opened in {r.ArchiveOpenTime.TotalMilliseconds:F2}ms, "
                + $"loaded in {r.LoadTime.TotalMilliseconds:F2}ms, "
                + $"executed in {r.ExecTime.TotalMilliseconds:F2}ms.");
        }

        // Dump-archive-source: prints raw module source bytes after any
        // build-time rewrite (tree-shake).
        private static void DumpArchiveSourceCli(string archivePath, int moduleIndex)
        {
            if (!File.Exists(archivePath))
            {
                Console.WriteLine($"[Ra Language] archive not found: {archivePath}");
                Environment.ExitCode = 1;
                return;
            }
            using var a = Interpreter.Archive.RacReader.Open(archivePath);
            if (moduleIndex < 0 || moduleIndex >= a.Manifest.Modules.Count)
            {
                Console.WriteLine($"[Ra Language] module index {moduleIndex} out of range (0..{a.Manifest.Modules.Count - 1})");
                Environment.ExitCode = 1;
                return;
            }
            var m = a.Manifest.Modules[moduleIndex];
            byte[] payload = a.ReadSection(m.SourceSectionIndex);
            Console.Out.Write(System.Text.Encoding.UTF8.GetString(payload));
        }

        // Bench harness for `--bench-archive-open`. Times RacReader.Open
        // in a tight loop after a brief warm-up so the JIT + AOT path
        // stays primed across iterations. Reports best / median / mean
        // / p95 / p99 in microseconds. Validates v1.1 (#4) "<1ms open"
        // regardless of total archive size — for non-tiny archives the
        // win shows up here.
        private static void BenchArchiveOpenCli(string archivePath, int iter)
        {
            if (!File.Exists(archivePath))
            {
                Console.WriteLine($"[Ra Language] archive not found: {archivePath}");
                Environment.ExitCode = 1;
                return;
            }
            long size = new FileInfo(archivePath).Length;
            Console.WriteLine($"[Ra Language] --bench-archive-open: {archivePath} ({size:N0} bytes), iter={iter}");

            // Warm-up: 32 opens to prime the JIT + OS page cache.
            for (int i = 0; i < 32; i++)
            {
                using var w = Interpreter.Archive.RacReader.Open(archivePath);
            }

            long[] tickArr = new long[iter];
            for (int i = 0; i < iter; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                using (var a = Interpreter.Archive.RacReader.Open(archivePath))
                {
                    // Dispose immediately — we measure open + close + manifest decode.
                }
                long t1 = Stopwatch.GetTimestamp();
                tickArr[i] = t1 - t0;
            }
            Array.Sort(tickArr);
            double tickToUs = 1_000_000.0 / Stopwatch.Frequency;
            double best = tickArr[0] * tickToUs;
            double median = tickArr[iter / 2] * tickToUs;
            double p95 = tickArr[(int)(iter * 0.95)] * tickToUs;
            double p99 = tickArr[(int)(iter * 0.99)] * tickToUs;
            double sum = 0;
            for (int i = 0; i < iter; i++) sum += tickArr[i] * tickToUs;
            double mean = sum / iter;
            Console.WriteLine($"  best  : {best:F2} us");
            Console.WriteLine($"  median: {median:F2} us");
            Console.WriteLine($"  mean  : {mean:F2} us");
            Console.WriteLine($"  p95   : {p95:F2} us");
            Console.WriteLine($"  p99   : {p99:F2} us");
        }

        // Archive CLI: `--inspect-archive <file.rac>`.
        private static void InspectArchiveCli(string archivePath)
        {
            if (!File.Exists(archivePath))
            {
                Console.WriteLine($"[Ra Language] archive not found: {archivePath}");
                Environment.ExitCode = 1;
                return;
            }
            try
            {
                Console.Write(RacInspector.Describe(archivePath));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Ra Language] cannot inspect '{archivePath}': {ex.Message}");
                Environment.ExitCode = 1;
            }
        }

        private static void ExecuteMainFile(string fileName = "main.ra", bool diagnostics = true)
        {
            // M43: hot-restart integrity — drop the IR cache so the
            // freshly-reread script source is recompiled from scratch.
            // Stale AstNode → RaFunction entries from the previous run
            // would otherwise leak forever (memory) and could shadow
            // legitimate re-compilation if AST identity ever collided.
            Interpreter.Runtime.IrExpressionEvaluator.ClearCache();
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
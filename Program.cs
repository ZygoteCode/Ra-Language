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

        // Stream builtin names — registered alongside the static list above so
        // that BuiltinSymbolTable always contains a BuiltInFunctionValue per
        // user-visible name. Dispatch is routed by BuiltInFunctionValue.Execute
        // through StreamBuiltins.Execute / AsyncStreamBuiltins.Execute.
        private static readonly string[] _streamBuiltinNames = MergeNames(
            RaLanguage.Interpreter.Values.Functions.Builtins.StreamBuiltins.Names,
            RaLanguage.Interpreter.Values.Functions.Builtins.AsyncStreamBuiltins.Names);

        private static string[] MergeNames(params string[][] groups)
        {
            int total = 0;
            for (int i = 0; i < groups.Length; i++) total += groups[i].Length;
            var dst = new string[total];
            int j = 0;
            for (int i = 0; i < groups.Length; i++)
                for (int k = 0; k < groups[i].Length; k++)
                    dst[j++] = groups[i][k];
            return dst;
        }

        // The complete set of built-in *function* names the runtime
        // installs: the switch-dispatched directs (_builtInFunctions), the
        // async/stream families (_streamBuiltinNames), and every
        // BuiltInRegistry handler. This is the exact domain StdLibrary must
        // categorise — see `--selftest-stdlib`.
        public static IReadOnlyCollection<string> AllBuiltinFunctionNames()
        {
            BuiltInRegistry.EnsureInitialized();
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in _builtInFunctions) set.Add(n);
            foreach (var n in _streamBuiltinNames) set.Add(n);
            foreach (var n in BuiltInRegistry.AllNames) set.Add(n);
            return set;
        }

        static Program()
        {
            InitializeSymbolTable();
        }

        // Full store of every built-in: the 633 functions PLUS the always-on
        // core (annotation types, Result/Option). It is NOT a runtime parent
        // scope — it is the synthesis source for the virtual std modules and
        // the "known names" source for tooling. Built-in *functions* are
        // reachable at runtime only by importing the std module they live in.
        public static SymbolTable BuiltinSymbolTable;

        // The auto-available core scope — the runtime parent of every user
        // scope and module. Holds ONLY what is not a callable built-in
        // function: the annotation types (`@test`, `@derive`, …, needed at
        // parse/processing time) and the `Result` / `Option` ADTs (the `?`
        // operator depends on `Result`). Everything else must be imported.
        public static SymbolTable CoreSymbolTable;

        public static void InitializeSymbolTable()
        {
            BuiltinSymbolTable = new SymbolTable();

            foreach (string builtInFunction in _builtInFunctions)
            {
                BuiltinSymbolTable.Set(builtInFunction, new BuiltInFunctionValue(builtInFunction));
            }

            foreach (string streamBuiltin in _streamBuiltinNames)
            {
                BuiltinSymbolTable.Set(streamBuiltin, new BuiltInFunctionValue(streamBuiltin));
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

            // Build the always-available core scope: every built-in that is
            // NOT a callable function (the annotation types and the
            // Result/Option ADTs), carried over as the SAME instances from
            // BuiltinSymbolTable so there is no duplicate-type subtlety. The
            // 633 built-in functions are deliberately excluded — they are
            // import-only now (no auto-prelude). This is the one place the
            // "manual import" policy is enforced.
            CoreSymbolTable = new SymbolTable();
            foreach (string key in BuiltinSymbolTable.GetLocalKeys())
            {
                var entry = BuiltinSymbolTable.GetEntry(key);
                if (entry?.Value == null) continue;
                if (entry.Value is BuiltInFunctionValue) continue; // functions: import-only
                CoreSymbolTable.Set(key, entry.Value,
                    isLet: entry.IsLet,
                    declaredType: entry.DeclaredType,
                    isStaticallyTyped: entry.IsStaticallyTyped,
                    isPublic: entry.IsPublic);
            }

            GlobalSymbolTable = new SymbolTable(CoreSymbolTable);

            string projectRoot = Directory.GetCurrentDirectory();
            string stdRoot = ResolveStdRoot(projectRoot);
            // Two providers: module/user scopes parent off the core (no
            // functions); the virtual std-module synthesis pulls function
            // values from the full BuiltinSymbolTable.
            ImportNodeVisitor.InitializeModuleManager(projectRoot, stdRoot,
                coreProvider: () => CoreSymbolTable,
                functionStoreProvider: () => BuiltinSymbolTable);
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
                // A compile-time abort is a failure regardless of the caller.
                // Without this the CLI (path mode / run_tests) reports success
                // on a file that never even parsed.
                Environment.ExitCode = 1;
                return (null, null);
            }

            var parser = new Parser.Parser(tokens);
            var parseResult = parser.Parse();

            if (parseResult.HasErrors)
            {
                PrintDiagnostics(parseResult.Diagnostics);
                Console.WriteLine($"[Ra Language] Compilation aborted: parsing failed ({parseResult.Diagnostics.Summary()}).");
                Environment.ExitCode = 1;
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
            // PERF: inline single-use loop/block temporaries into the following
            // `if` condition (drops the boxing OP_DECLARE_LOCAL). Runs after the
            // Resolver (needs BindingIds) — semantics-preserving, so the
            // warning-only analyzers downstream see an equivalent tree.
            Interpreter.Runtime.Optimizations.SingleUseTempInliner.Apply(parseResult.Node);

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

        // Diagnostic-only twin of Run that executes the post-front-end AST
        // through the pure visitor tree-walk instead of compiling to IR + VM.
        // Identical lex/parse/derive/resolve pipeline; the ONLY difference is
        // the executor. Used by `--bench-ast` to measure the VM-vs-tree-walk
        // delta on the exact same build. NOT wired into the default run path.
        public static ValueResult RunAst(string fn, string text)
        {
            var lexer = new Lexer.Lexer(fn, text);
            var (tokens, lexerDiagnostics) = lexer.MakeTokens();
            if (lexerDiagnostics.HasErrors) { Environment.ExitCode = 1; return (null, null); }

            var parser = new Parser.Parser(tokens);
            var parseResult = parser.Parse();
            if (parseResult.HasErrors) { Environment.ExitCode = 1; return (null, null); }

            DeriveTransformer.Apply(parseResult.Node);
            Interpreter.Runtime.Patterns.MatchSimplifier.Apply(parseResult.Node);
            Resolver.Resolve(parseResult.Node);
            // PERF: inline single-use loop/block temporaries into the following
            // `if` condition (drops the boxing OP_DECLARE_LOCAL). Runs after the
            // Resolver (needs BindingIds) — semantics-preserving, so the
            // warning-only analyzers downstream see an equivalent tree.
            Interpreter.Runtime.Optimizations.SingleUseTempInliner.Apply(parseResult.Node);

            var interpreter = new Interpreter.Interpreter();
            var context = new Context(fn);
            context.SymbolTable = GlobalSymbolTable;
            var result = interpreter.VisitBlocking(parseResult.Node, context);
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
            // through Apply helpers; a fat stack keeps deep user recursion safe.
            // 64 MB (was 32): the L8 async-opcode lowering adds a few `await`
            // points to the recursive `Execute` MoveNext (Await/ForAwait/Yield),
            // which grows the per-recursion-level async frame; doubling the stack
            // restores headroom (M85 overflowed at 32 MB with +1 await point, so
            // 64 MB amply covers +3; the reservation is virtual — only touched
            // pages commit). test_deep_recursion @2000 is the gate.
            Exception? threadEx = null;
            var worker = new System.Threading.Thread(() =>
            {
                try { MainCore(args); }
                catch (Exception ex) { threadEx = ex; }
            }, 64 * 1024 * 1024);
            worker.Start();
            worker.Join();
            if (threadEx != null) throw threadEx;
        }

        private static void MainCore(string[] args)
        {
            // Language Server mode. Branch BEFORE any stdout-touching setup
            // (Console.Title can emit terminal escape bytes; the JIT warmup and
            // real-time priority are equally undesirable for an editor backend).
            // STDOUT must stay a pure JSON-RPC channel from the very first byte.
            if (args.Length >= 1 && string.Equals(args[0], "--lsp", StringComparison.OrdinalIgnoreCase))
            {
                Environment.Exit(RaLanguage.LanguageServer.Cli.LspEntryPoint.Run(args));
                return;
            }

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
                if (args.Length >= 1 && string.Equals(args[0], "--bench", StringComparison.OrdinalIgnoreCase))
                {
                    RunMicrobenchmark(astPath: false, customBenches: args.Length > 1 ? args[1..] : null);
                    return;
                }

                // Diagnostic harness: run the bench corpus (or arbitrary files
                // passed as extra args) through the pure AST-visitor path
                // (interpreter.VisitBlocking) instead of the IR+VM pipeline.
                // Same front-end passes, swapped executor — the apples-to-apples
                // baseline for the VM-vs-tree-walk delta.
                if (args.Length >= 1 && string.Equals(args[0], "--bench-ast", StringComparison.OrdinalIgnoreCase))
                {
                    RunMicrobenchmark(astPath: true, customBenches: args.Length > 1 ? args[1..] : null);
                    return;
                }

                // --bench-lex [file.ra] [iter]
                // Isolated lexer throughput + allocation benchmark. With no
                // file argument it builds a corpus from every tests/**/*.ra
                // and bench/*.ra next to the executable. Measures the lexer
                // alone (no parse / no VM) so a Lexer-only change shows up
                // cleanly in ns/token, MB/s and bytes-allocated-per-iteration.
                if (args.Length >= 1 && string.Equals(args[0], "--bench-lex", StringComparison.OrdinalIgnoreCase))
                {
                    string? lexFile = null;
                    int lexIter = 0;
                    for (int ai = 1; ai < args.Length; ai++)
                    {
                        if (int.TryParse(args[ai], out int n) && n > 0) lexIter = n;
                        else lexFile = args[ai];
                    }
                    BenchLexCli(lexFile, lexIter);
                    return;
                }

                // --bench-parse [file.ra] [iter]
                // Isolated parser throughput + allocation benchmark. Lexing is
                // performed ONCE per file up-front (outside the timed loop), so
                // only the tokens -> AST parse step is measured. With no file
                // argument it builds the corpus from every tests/**/*.ra and
                // bench/*.ra next to the executable, lexing each file
                // independently. The per-pass parse-error count is printed and
                // is a hard regression guard: any parser change that alters it
                // has changed behaviour, not just performance.
                if (args.Length >= 1 && string.Equals(args[0], "--bench-parse", StringComparison.OrdinalIgnoreCase))
                {
                    string? parseFile = null;
                    int parseIter = 0;
                    for (int ai = 1; ai < args.Length; ai++)
                    {
                        if (int.TryParse(args[ai], out int n) && n > 0) parseIter = n;
                        else parseFile = args[ai];
                    }
                    BenchParseCli(parseFile, parseIter);
                    return;
                }

                // --parity <file.ra> <Kind[,Kind...] | all>
                // L0 differential parity oracle (RA_FULL_IR_LOWERING_PLAN §4b).
                // Runs the file twice — once IR-native, once with the named
                // AST-node kind(s) forced to the OP_NATIVE_DEFINE visitor
                // fallback — and asserts byte-identical observable behaviour
                // (stdout + exit code + error message). Exit 0 = MATCH, 1 =
                // MISMATCH / usage error. This is the gate every later lowering
                // phase uses before deleting a fallback.
                if (args.Length >= 2 && string.Equals(args[0], "--parity", StringComparison.OrdinalIgnoreCase))
                {
                    RunParityCli(args[1], args.Length >= 3 ? args[2] : "all");
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

                // --run-archive <file.rac> [--strict-signature]
                //                          [--trusted-keys <dir>]
                //                          [--require-trusted-key]
                // Loads a .rac archive into the in-process runtime and
                // executes its entry module.
                if (args.Length >= 2 && string.Equals(args[0], "--run-archive", StringComparison.OrdinalIgnoreCase))
                {
                    RunArchiveCli(args, startIdx: 1);
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

                // --keygen <out-prefix> [--algo ed25519|rsa-pss-2048|rsa-pss-4096|ecdsa-p256]
                // Writes <prefix>.priv (PEM private key) and
                // <prefix>.pub (PEM public key) for the requested
                // algorithm. Default algorithm is Ed25519.
                if (args.Length >= 2 && string.Equals(args[0], "--keygen", StringComparison.OrdinalIgnoreCase))
                {
                    KeygenCli(args);
                    return;
                }

                // --sign-archive <input.rac> --key <path> [--signer <id>]
                //                [--mode embedded|fingerprint] [-o <out>]
                // Re-packs an existing archive with a signature
                // appended. Output defaults to <input>.signed.rac.
                if (args.Length >= 4 && string.Equals(args[0], "--sign-archive", StringComparison.OrdinalIgnoreCase))
                {
                    SignArchiveCli(args);
                    return;
                }

                // --verify-signature <archive> [--trusted-keys <dir>]
                //                    [--strict] [--require-trusted-key]
                // Verifies a signature without executing the archive.
                if (args.Length >= 2 && string.Equals(args[0], "--verify-signature", StringComparison.OrdinalIgnoreCase))
                {
                    VerifySignatureCli(args);
                    return;
                }

                // --test-ed25519
                // Run RFC 8032 §7.1 known-answer tests against the
                // vendored Ed25519 implementation. Exit code 0 on
                // PASS, 1 on any FAIL.
                if (args.Length == 1 && string.Equals(args[0], "--test-ed25519", StringComparison.OrdinalIgnoreCase))
                {
                    TestEd25519KatCli();
                    return;
                }

                // --verify-bytecode <archive.rac>
                // Standalone structural verifier. Walks every module's
                // RaFunction, validates operand bounds / jump targets
                // / EH ranges / AST-ref pool indices, prints PASS or
                // the full diagnostic report.
                if (args.Length == 2 && string.Equals(args[0], "--verify-bytecode", StringComparison.OrdinalIgnoreCase))
                {
                    VerifyBytecodeCli(args[1]);
                    return;
                }

                // --test-verifier
                // Construct synthetic broken RaFunction instances
                // (out-of-range jump, bad slot, bad const, malformed
                // EH) and confirm the verifier catches each with the
                // expected diagnostic. Exit code 0 on PASS.
                if (args.Length == 1 && string.Equals(args[0], "--test-verifier", StringComparison.OrdinalIgnoreCase))
                {
                    TestVerifierCli();
                    return;
                }

                // --selftest-stdlib
                // Proves the std-library taxonomy (StdLibrary) covers
                // EXACTLY the live built-in function set: every built-in
                // maps to a std.prelude.* / std.sys.* module, and every
                // manifest name is a real built-in. Exit 0 on PASS, 1 on
                // any gap. Mirrors --test-verifier / --test-ed25519.
                if (args.Length == 1 && string.Equals(args[0], "--selftest-stdlib", StringComparison.OrdinalIgnoreCase))
                {
                    SelfTestStdLibCli();
                    return;
                }

                // --inspect-precompiled <archive.rac>
                // Counts FunctionDefinitionNode / StructMethod /
                // TraitMethod / Classes.OperatorDefinitionNode whose
                // CompiledBody was hydrated by the v4 deserializer.
                // A non-zero count proves the runtime can skip the
                // lazy AST→IR compile entirely on first dispatch.
                if (args.Length == 2 && string.Equals(args[0], "--inspect-precompiled", StringComparison.OrdinalIgnoreCase))
                {
                    InspectPrecompiledCli(args[1]);
                    return;
                }

                // Auto-detect `.rac` positional argument so `ra foo.rac`
                // just works. We keep this *after* the explicit flags so
                // an `--inspect-archive foo.rac` is not eaten here.
                if (args.Length == 1 && args[0].EndsWith(".rac", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(args[0]))
                {
                    RunArchiveCli(new[] { args[0] }, startIdx: 0);
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
            // PERF: inline single-use loop/block temporaries into the following
            // `if` condition (drops the boxing OP_DECLARE_LOCAL). Runs after the
            // Resolver (needs BindingIds) — semantics-preserving, so the
            // warning-only analyzers downstream see an equivalent tree.
            Interpreter.Runtime.Optimizations.SingleUseTempInliner.Apply(parseResult.Node);
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
            // PERF: inline single-use loop/block temporaries into the following
            // `if` condition (drops the boxing OP_DECLARE_LOCAL). Runs after the
            // Resolver (needs BindingIds) — semantics-preserving, so the
            // warning-only analyzers downstream see an equivalent tree.
            Interpreter.Runtime.Optimizations.SingleUseTempInliner.Apply(parseResult.Node);
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

        private static void RunMicrobenchmark(bool astPath = false, string[]? customBenches = null)
        {
            // Two-phase microbenchmark: warmup to populate JIT + AOT inlining decisions, then
            // measured runs. Reports wall-clock time and managed-heap allocation delta per
            // benchmark so optimization passes have a numerical regression signal.
            // bench_calls / bench_method added to cover the function- and
            // method-call paths — the corpus previously had ZERO call-heavy
            // benches, which is exactly why a multi-KB-per-call allocation
            // regression in the shared call machinery went unmeasured.
            string[] benches = customBenches ?? new[] { "bench_hotloop.ra", "bench_arithmetic.ra", "bench_counter.ra", "bench_hybrid_read.ra", "bench_while.ra", "bench_branchy.ra", "bench_dirty.ra", "bench_bindcmp.ra", "bench_invariant.ra", "bench_calls.ra", "bench_method.ra", "bench_strcat.ra" };

            Console.WriteLine($"[Ra Language] Microbenchmark mode{(astPath ? " (AST-visitor path)" : "")}.");
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
                    if (astPath) RunAst(bench, text); else Run(bench, text);
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
                    if (astPath) RunAst(bench, text); else Run(bench, text);
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

        // L0 parity oracle (RA_FULL_IR_LOWERING_PLAN §4b). Compiles + runs a
        // file twice and diffs the observable transcript: once fully IR-native,
        // once with the requested AST-node kind(s) forced to the
        // OP_NATIVE_DEFINE visitor fallback. A MATCH proves the IR lowering is
        // behaviourally identical to the visitor spec for that program — the
        // precondition for deleting the fallback in a later migration phase.
        private static void RunParityCli(string file, string kindsArg)
        {
            if (!file.EndsWith(".ra", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                Console.WriteLine($"[Ra Language] --parity: file not found or not .ra: {file}");
                Environment.ExitCode = 1;
                return;
            }

            // Resolve the kind list. `all` = every fallback-routable kind, i.e.
            // run the WHOLE program through the visitor layer and compare to the
            // all-native baseline (the strongest single differential).
            var kinds = new List<RaLanguage.Parser.Nodes.AstNodeType>();
            if (string.Equals(kindsArg, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (RaLanguage.Parser.Nodes.AstNodeType k in Enum.GetValues<RaLanguage.Parser.Nodes.AstNodeType>())
                    if (IrCompiler.IsFallbackRoutable(k)) kinds.Add(k);
            }
            else
            {
                foreach (var tok in kindsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Enum.TryParse<RaLanguage.Parser.Nodes.AstNodeType>(tok, ignoreCase: true, out var k))
                    {
                        Console.WriteLine($"[Ra Language] --parity: unknown AstNodeType '{tok}'");
                        Environment.ExitCode = 1;
                        return;
                    }
                    if (!IrCompiler.IsFallbackRoutable(k))
                    {
                        Console.WriteLine($"[Ra Language] --parity: '{k}' has no visitor fallback route (not forceable)");
                        Environment.ExitCode = 1;
                        return;
                    }
                    kinds.Add(k);
                }
            }

            string text = File.ReadAllText(file);
            string baseline = CaptureRun(file, text, null);
            string forced = CaptureRun(file, text, kinds);

            if (string.Equals(baseline, forced, StringComparison.Ordinal))
            {
                Console.WriteLine($"[Ra Language] --parity MATCH: {file} (forced {kinds.Count} kind(s): {kindsArg})");
                Environment.ExitCode = 0;
            }
            else
            {
                Console.WriteLine($"[Ra Language] --parity MISMATCH: {file} (forced {kindsArg})");
                Console.WriteLine(FirstDiff(baseline, forced));
                Console.WriteLine("--- NATIVE transcript ---");
                Console.WriteLine(baseline);
                Console.WriteLine("--- FORCED transcript ---");
                Console.WriteLine(forced);
                Environment.ExitCode = 1;
            }
        }

        // Runs `Run` with Console captured to a StringWriter and the requested
        // force-fallback set applied, returning a deterministic transcript:
        // captured stdout + an [[EXIT n]] line + an [[ERROR msg]] line when the
        // run returned an Error. Resets the symbol table + IR sub-expression
        // cache first (so the two runs are independent) and always restores
        // Console.Out + clears the force set.
        private static string CaptureRun(string fn, string text, IEnumerable<RaLanguage.Parser.Nodes.AstNodeType>? force)
        {
            InitializeSymbolTable();
            IrExpressionEvaluator.ClearCache();
            if (force != null) IrCompiler.SetForceFallback(force);
            else IrCompiler.ClearForceFallback();

            var sw = new StringWriter();
            var prevOut = Console.Out;
            int prevExit = Environment.ExitCode;
            Console.SetOut(sw);
            Environment.ExitCode = 0;
            Error? err = null;
            try
            {
                var (_, e) = Run(fn, text);
                err = e;
            }
            catch (Exception ex)
            {
                sw.Write($"\n[[HOST-EXCEPTION {ex.GetType().Name}: {ex.Message}]]");
            }
            finally
            {
                Console.SetOut(prevOut);
                IrCompiler.ClearForceFallback();
            }

            int exit = Environment.ExitCode;
            Environment.ExitCode = prevExit;
            var t = sw.ToString();
            t += $"\n[[EXIT {exit}]]";
            if (err != null) t += $"\n[[ERROR {err.Details}]]";
            return t;
        }

        private static string FirstDiff(string a, string b)
        {
            var la = a.Replace("\r\n", "\n").Split('\n');
            var lb = b.Replace("\r\n", "\n").Split('\n');
            int n = Math.Min(la.Length, lb.Length);
            for (int i = 0; i < n; i++)
                if (!string.Equals(la[i], lb[i], StringComparison.Ordinal))
                    return $"first diff @ line {i + 1}:\n  native: {la[i]}\n  forced: {lb[i]}";
            if (la.Length != lb.Length)
                return $"length differs: native={la.Length} lines, forced={lb.Length} lines";
            return "(transcripts differ only in trailing content)";
        }

        // Archive CLI: `--compile <entry.ra> [-o output.rac] [--no-compress]
        //               [--sign --sign-key <path> [--signer <id>]
        //                [--sign-mode embedded|fingerprint]]`.
        // Parses flags, dispatches to RacPackager, prints summary.
        private static void CompileArchiveCli(string[] args)
        {
            string entry = args[1];
            string? output = null;
            string? stdRootFlag = null;
            bool compress = true;
            bool verbose = false;
            bool treeShake = true;
            bool sharedConstPool = true;
            string? signKeyPath = null;
            string signerId = "";
            RacSignatureKeyMode signMode = RacSignatureKeyMode.Embedded;
            RacCodecKind codec = RacCodecKind.Zstd;
            int zstdLevel = 11;

            for (int i = 2; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-o", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    output = args[++i];
                }
                else if (string.Equals(args[i], "--std-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    // Override the standard-library root the packager resolves
                    // `std.*` physical imports against (default: <exe>/std then
                    // <project>/std). Lets a build target a specific std tree,
                    // e.g. a test fixture's own std/ folder.
                    stdRootFlag = args[++i];
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
                else if (string.Equals(args[i], "--sign-key", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    signKeyPath = args[++i];
                }
                else if (string.Equals(args[i], "--signer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    signerId = args[++i];
                }
                else if (string.Equals(args[i], "--sign-mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string m = args[++i];
                    if (string.Equals(m, "embedded", StringComparison.OrdinalIgnoreCase)) signMode = RacSignatureKeyMode.Embedded;
                    else if (string.Equals(m, "fingerprint", StringComparison.OrdinalIgnoreCase)) signMode = RacSignatureKeyMode.Fingerprint;
                    else { Console.WriteLine($"[Ra Language] --sign-mode: must be 'embedded' or 'fingerprint', got '{m}'"); return; }
                }
                else if (string.Equals(args[i], "--codec", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string c = args[++i];
                    if (string.Equals(c, "zstd", StringComparison.OrdinalIgnoreCase)) codec = RacCodecKind.Zstd;
                    else if (string.Equals(c, "deflate", StringComparison.OrdinalIgnoreCase)) codec = RacCodecKind.Deflate;
                    else { Console.WriteLine($"[Ra Language] --codec: must be 'zstd' or 'deflate', got '{c}'"); return; }
                }
                else if (string.Equals(args[i], "--zstd-level", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out zstdLevel) || zstdLevel < 1 || zstdLevel > 22)
                    {
                        Console.WriteLine($"[Ra Language] --zstd-level: must be integer in [1, 22]");
                        return;
                    }
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
                StdRoot = stdRootFlag,
                Compress = compress,
                Verbose = verbose,
                TreeShakeStd = treeShake,
                SharedConstPoolEnabled = sharedConstPool,
                SignKeyPath = signKeyPath,
                SignerId = signerId,
                SignKeyMode = signMode,
                Codec = codec,
                ZstdLevel = zstdLevel,
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
        // Optional flags:
        //   --strict-signature        refuse archives that do not verify
        //   --trusted-keys <dir>      directory of *.pub trust store
        //   --require-trusted-key     even for embedded-key signatures,
        //                             demand the key be in the trust store
        private static void RunArchiveCli(string[] args, int startIdx)
        {
            string archivePath = args[startIdx];
            bool strictSig = false;
            string? trustedKeysDir = null;
            bool requireTrustedKey = false;
            bool verifyBytecode = true;
            for (int i = startIdx + 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--strict-signature", StringComparison.OrdinalIgnoreCase))
                {
                    strictSig = true;
                }
                else if (string.Equals(args[i], "--trusted-keys", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    trustedKeysDir = args[++i];
                }
                else if (string.Equals(args[i], "--require-trusted-key", StringComparison.OrdinalIgnoreCase))
                {
                    requireTrustedKey = true;
                }
                else if (string.Equals(args[i], "--no-verify-bytecode", StringComparison.OrdinalIgnoreCase))
                {
                    verifyBytecode = false;
                }
                else
                {
                    Console.WriteLine($"[Ra Language] --run-archive: unknown flag '{args[i]}'");
                    Environment.ExitCode = 1;
                    return;
                }
            }
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
                StrictSignature = strictSig,
                TrustedKeysDir = trustedKeysDir,
                RequireTrustedKey = requireTrustedKey,
                VerifyBytecode = verifyBytecode,
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

        // --keygen <prefix> [--algo ed25519|rsa-pss-2048|rsa-pss-4096|ecdsa-p256]
        private static void KeygenCli(string[] args)
        {
            string prefix = args[1];
            var algo = RacSignatureAlgorithm.Ed25519;
            for (int i = 2; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--algo", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string s = args[++i].ToLowerInvariant();
                    algo = s switch
                    {
                        "ed25519" => RacSignatureAlgorithm.Ed25519,
                        "rsa-pss-2048" => RacSignatureAlgorithm.RsaPss2048Sha256,
                        "rsa-pss-4096" => RacSignatureAlgorithm.RsaPss4096Sha256,
                        "ecdsa-p256" => RacSignatureAlgorithm.EcdsaP256Sha256,
                        _ => throw new ArgumentException($"--keygen --algo: unknown algorithm '{s}'"),
                    };
                }
                else
                {
                    Console.WriteLine($"[Ra Language] --keygen: unknown flag '{args[i]}'");
                    Environment.ExitCode = 1;
                    return;
                }
            }
            try
            {
                var pair = RacKeyStore.Generate(algo);
                string privPath = prefix + ".priv";
                string pubPath = prefix + ".pub";
                RacKeyStore.WriteKeyPair(pair, privPath, pubPath);
                Console.WriteLine($"[Ra Language] keygen wrote:");
                Console.WriteLine($"  algorithm  : {RacSigner.DescribeAlgorithm(algo)}");
                Console.WriteLine($"  fingerprint: {pair.FingerprintHex()}");
                Console.WriteLine($"  private key: {privPath}");
                Console.WriteLine($"  public  key: {pubPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Ra Language] keygen failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }

        // --sign-archive <input.rac> --sign-key <path> [--signer <id>]
        //                [--sign-mode embedded|fingerprint] [-o <out>]
        // Re-packs an existing archive with a signature appended.
        private static void SignArchiveCli(string[] args)
        {
            string input = args[1];
            string? signKeyPath = null;
            string signerId = "";
            string? output = null;
            RacSignatureKeyMode mode = RacSignatureKeyMode.Embedded;
            for (int i = 2; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--sign-key", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    signKeyPath = args[++i];
                else if (string.Equals(args[i], "--signer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    signerId = args[++i];
                else if (string.Equals(args[i], "-o", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    output = args[++i];
                else if (string.Equals(args[i], "--sign-mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string m = args[++i];
                    if (string.Equals(m, "embedded", StringComparison.OrdinalIgnoreCase)) mode = RacSignatureKeyMode.Embedded;
                    else if (string.Equals(m, "fingerprint", StringComparison.OrdinalIgnoreCase)) mode = RacSignatureKeyMode.Fingerprint;
                    else { Console.WriteLine($"[Ra Language] --sign-archive: bad --sign-mode '{m}'"); Environment.ExitCode = 1; return; }
                }
                else
                {
                    Console.WriteLine($"[Ra Language] --sign-archive: unknown flag '{args[i]}'");
                    Environment.ExitCode = 1;
                    return;
                }
            }
            if (signKeyPath == null)
            {
                Console.WriteLine("[Ra Language] --sign-archive: --sign-key is required");
                Environment.ExitCode = 1;
                return;
            }
            if (!File.Exists(input))
            {
                Console.WriteLine($"[Ra Language] archive not found: {input}");
                Environment.ExitCode = 1;
                return;
            }
            output ??= System.IO.Path.ChangeExtension(input, ".signed.rac");
            try
            {
                RacKeyPair signKey = RacKeyStore.LoadPrivateKey(signKeyPath);
                // Resign by re-packing: load all section payloads,
                // build a new writer with the same archive flags +
                // runtime requirements, append signature.
                using var source = Interpreter.Archive.RacReader.Open(input);
                var writer = new RacWriter
                {
                    ArchiveFlags = source.Header.Flags & ~RacFlags.Signed, // re-add Signed via SignWith
                    RaRuntimeRequired = source.Header.RaRuntimeRequired,
                    RaRuntimeBuiltWith = source.Header.RaRuntimeBuiltWith,
                };
                for (int i = 0; i < source.Sections.Count; i++)
                {
                    var entry = source.Sections[i];
                    if (entry.Kind == RacSectionKind.Signature) continue; // discard old sig
                    byte[] payload = source.ReadSection(i);
                    bool compressed = entry.IsCompressed;
                    writer.AddSection(entry.Kind, payload, compress: compressed, mustUnderstand: entry.MustUnderstand);
                }
                writer.SignWith(signKey, signerId, mode);
                using (var outFs = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    writer.Finish(outFs);
                }
                long size = new FileInfo(output).Length;
                Console.WriteLine($"[Ra Language] signed archive written: {output}");
                Console.WriteLine($"  algorithm  : {RacSigner.DescribeAlgorithm(signKey.Algorithm)}");
                Console.WriteLine($"  fingerprint: {signKey.FingerprintHex()}");
                Console.WriteLine($"  signer id  : {(string.IsNullOrEmpty(signerId) ? "(none)" : signerId)}");
                Console.WriteLine($"  key mode   : {mode}");
                Console.WriteLine($"  size       : {size:N0} bytes");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Ra Language] --sign-archive failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }

        // Run RFC 8032 §7.1 known-answer vectors against the vendored
        // Ed25519 implementation. Catches a regression in field /
        // scalar arithmetic the moment it lands.
        private static void TestEd25519KatCli()
        {
            (string Sk, string Pk, string M, string Sig)[] vectors = new[]
            {
                ( // §7.1 TEST 1
                    "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
                    "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
                    "",
                    "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
                ( // §7.1 TEST 2
                    "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
                    "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
                    "72",
                    "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
                ( // §7.1 TEST 3
                    "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
                    "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
                    "af82",
                    "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"),
            };
            int pass = 0, fail = 0;
            for (int i = 0; i < vectors.Length; i++)
            {
                var v = vectors[i];
                byte[] sk = HexToBytes(v.Sk);
                byte[] pkExpected = HexToBytes(v.Pk);
                byte[] msg = HexToBytes(v.M);
                byte[] sigExpected = HexToBytes(v.Sig);

                byte[] pkGot = Interpreter.Archive.Crypto.Ed25519.GetPublicKey(sk);
                bool pkOk = pkGot.AsSpan().SequenceEqual(pkExpected);
                byte[] sigGot = Interpreter.Archive.Crypto.Ed25519.Sign(msg, sk, pkGot);
                bool sigOk = sigGot.AsSpan().SequenceEqual(sigExpected);
                bool verifyOk = Interpreter.Archive.Crypto.Ed25519.Verify(msg, sigGot, pkGot);
                bool verifyReject = !Interpreter.Archive.Crypto.Ed25519.Verify(msg,
                    TweakLastByte(sigGot), pkGot);

                bool ok = pkOk && sigOk && verifyOk && verifyReject;
                Console.WriteLine(
                    $"  TEST {i + 1}: pubKey={(pkOk ? "OK" : "FAIL")} sig={(sigOk ? "OK" : "FAIL")} verify={(verifyOk ? "OK" : "FAIL")} reject-bad-sig={(verifyReject ? "OK" : "FAIL")}");
                if (ok) pass++; else fail++;
            }
            Console.WriteLine($"[Ra Language] --test-ed25519: {pass} passed, {fail} failed");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        // --selftest-stdlib implementation. Builds the taxonomy, prints a
        // per-module member count, and asserts the manifest is complete
        // (no uncategorised built-in) and exact (no phantom entry).
        private static void SelfTestStdLibCli()
        {
            InitializeSymbolTable();
            var live = AllBuiltinFunctionNames();
            RaLanguage.Interpreter.Modules.StdLibrary.Audit(live, out var uncategorized, out var phantom);

            Console.WriteLine($"[Ra Language] --selftest-stdlib: {live.Count} built-in functions across "
                + $"{RaLanguage.Interpreter.Modules.StdLibrary.AllModulePaths.Count} virtual std modules.");
            foreach (var m in RaLanguage.Interpreter.Modules.StdLibrary.SortedModulePaths())
            {
                var members = RaLanguage.Interpreter.Modules.StdLibrary.ModuleMembers(m);
                Console.WriteLine($"  {m,-26} {members?.Count ?? 0}");
            }

            bool ok = true;
            if (uncategorized.Count > 0)
            {
                ok = false;
                Console.WriteLine($"FAIL: {uncategorized.Count} uncategorised built-in(s) (no std module): {string.Join(", ", uncategorized)}");
            }
            if (phantom.Count > 0)
            {
                ok = false;
                Console.WriteLine($"FAIL: {phantom.Count} phantom manifest name(s) (not a live built-in): {string.Join(", ", phantom)}");
            }
            Console.WriteLine(ok
                ? "OK  std-library taxonomy is complete and exact."
                : "[Ra Language] --selftest-stdlib FAILED.");
            Environment.ExitCode = ok ? 0 : 1;
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static byte[] TweakLastByte(byte[] src)
        {
            byte[] dst = (byte[])src.Clone();
            dst[dst.Length - 1] ^= 0xFF;
            return dst;
        }

        // Synthetic bad-RaFunction battery: each case constructs an
        // intentionally malformed RaFunction and asserts the verifier
        // surfaces a diagnostic whose Message contains the expected
        // marker string. Catches verifier regressions before they
        // ship — a verifier that misses one of these would let a
        // tampered archive crash the dispatch loop.
        private static void TestVerifierCli()
        {
            int pass = 0, fail = 0;

            // Helper to encode a 3-address instruction.
            uint Mk(Interpreter.IR.Opcode op, byte a, byte b, byte c)
                => (uint)op | ((uint)a << 8) | ((uint)b << 16) | ((uint)c << 24);
            uint MkImm(Interpreter.IR.Opcode op, byte a, ushort imm)
                => (uint)op | ((uint)a << 8) | ((uint)imm << 16);

            // 1. Slot index out of range.
            {
                var fn = new Interpreter.IR.RaFunction("test1")
                {
                    LocalCount = 4,
                    SlotCount = 0,
                    Code = new uint[] { Mk(Interpreter.IR.Opcode.Move, 9, 0, 0), Mk(Interpreter.IR.Opcode.RetNull, 0, 0, 0) },
                    Consts = Array.Empty<RuntimeValue?>(),
                    Names = Array.Empty<string>(),
                };
                pass += AssertVerifyContains(fn, "test1 (bad slot)", "Move", "slot 9 out of range", ref fail);
            }
            // 2. Out-of-range jump.
            {
                var fn = new Interpreter.IR.RaFunction("test2")
                {
                    LocalCount = 4,
                    SlotCount = 0,
                    Code = new uint[] { MkImm(Interpreter.IR.Opcode.Jmp, 0, unchecked((ushort)(short)1000)) },
                    Consts = Array.Empty<RuntimeValue?>(),
                    Names = Array.Empty<string>(),
                };
                pass += AssertVerifyContains(fn, "test2 (bad jump)", "Jmp", "jump target", ref fail);
            }
            // 3. Const index out of range.
            {
                var fn = new Interpreter.IR.RaFunction("test3")
                {
                    LocalCount = 4,
                    SlotCount = 0,
                    Code = new uint[] { MkImm(Interpreter.IR.Opcode.LoadConst, 0, 99), Mk(Interpreter.IR.Opcode.RetNull, 0, 0, 0) },
                    Consts = Array.Empty<RuntimeValue?>(),
                    Names = Array.Empty<string>(),
                };
                pass += AssertVerifyContains(fn, "test3 (bad const idx)", "LoadConst", "const index 99 out of range", ref fail);
            }
            // 4. EH table: StartPc >= EndPc.
            {
                var fn = new Interpreter.IR.RaFunction("test4")
                {
                    LocalCount = 4,
                    SlotCount = 0,
                    Code = new uint[] { Mk(Interpreter.IR.Opcode.RetNull, 0, 0, 0) },
                    Consts = Array.Empty<RuntimeValue?>(),
                    Names = Array.Empty<string>(),
                    EhTable = new[] { new Interpreter.IR.ExceptionHandler(5, 3, -1, -1, 0, 0) },
                };
                pass += AssertVerifyContains(fn, "test4 (inverted EH range)", "EhTable", "StartPc 5 >= EndPc 3", ref fail);
            }
            // 5. EH table: CatchPc beyond Code.Length.
            {
                var fn = new Interpreter.IR.RaFunction("test5")
                {
                    LocalCount = 4,
                    SlotCount = 0,
                    Code = new uint[] { Mk(Interpreter.IR.Opcode.RetNull, 0, 0, 0) },
                    Consts = Array.Empty<RuntimeValue?>(),
                    Names = Array.Empty<string>(),
                    EhTable = new[] { new Interpreter.IR.ExceptionHandler(0, 1, 99, -1, 0, 0) },
                };
                pass += AssertVerifyContains(fn, "test5 (catch pc beyond code)", "EhTable", "CatchPc 99 >= Code.Length 1", ref fail);
            }
            // 6. Name index out of range (LoadGlobal).
            {
                var fn = new Interpreter.IR.RaFunction("test6")
                {
                    LocalCount = 4,
                    SlotCount = 0,
                    Code = new uint[] { MkImm(Interpreter.IR.Opcode.LoadGlobal, 0, 7), Mk(Interpreter.IR.Opcode.RetNull, 0, 0, 0) },
                    Consts = Array.Empty<RuntimeValue?>(),
                    Names = new[] { "a", "b" },
                };
                pass += AssertVerifyContains(fn, "test6 (bad name idx)", "LoadGlobal", "name index 7 out of range", ref fail);
            }
            // 7. Frame-slot index (LoadLocalS) beyond SlotCount.
            {
                var fn = new Interpreter.IR.RaFunction("test7")
                {
                    LocalCount = 4,
                    SlotCount = 2,
                    Code = new uint[] { MkImm(Interpreter.IR.Opcode.LoadLocalS, 0, 5), Mk(Interpreter.IR.Opcode.RetNull, 0, 0, 0) },
                    Consts = Array.Empty<RuntimeValue?>(),
                    Names = Array.Empty<string>(),
                };
                pass += AssertVerifyContains(fn, "test7 (bad frame-slot idx)", "LoadLocalS", "frame-slot index 5 out of range", ref fail);
            }
            // 8. Negative jump landing before code start.
            {
                var fn = new Interpreter.IR.RaFunction("test8")
                {
                    LocalCount = 4,
                    SlotCount = 0,
                    Code = new uint[]
                    {
                        Mk(Interpreter.IR.Opcode.RetNull, 0, 0, 0),
                        MkImm(Interpreter.IR.Opcode.Jmp, 0, unchecked((ushort)(short)-100)),
                    },
                    Consts = Array.Empty<RuntimeValue?>(),
                    Names = Array.Empty<string>(),
                };
                pass += AssertVerifyContains(fn, "test8 (negative jump underflow)", "Jmp", "jump target", ref fail);
            }
            // 9. Clean valid function — must report OK.
            {
                var fn = new Interpreter.IR.RaFunction("test9")
                {
                    LocalCount = 2,
                    SlotCount = 0,
                    Code = new uint[]
                    {
                        Mk(Interpreter.IR.Opcode.LoadNull, 0, 0, 0),
                        Mk(Interpreter.IR.Opcode.Halt, 0, 0, 0),
                    },
                    Consts = Array.Empty<RuntimeValue?>(),
                    Names = Array.Empty<string>(),
                };
                var res = Interpreter.Archive.RacBytecodeVerifier.Verify(fn);
                if (res.Ok) { Console.WriteLine("  TEST 9 (clean): OK"); pass++; }
                else { Console.WriteLine($"  TEST 9 (clean): FAIL (expected OK, got {res.Count} diagnostics)"); fail++; }
            }

            Console.WriteLine($"[Ra Language] --test-verifier: {pass} passed, {fail} failed");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        private static int AssertVerifyContains(Interpreter.IR.RaFunction fn, string label, string expectedOp,
            string expectedSubstring, ref int failCounter)
        {
            var res = Interpreter.Archive.RacBytecodeVerifier.Verify(fn, recurseChildren: false);
            if (res.Ok)
            {
                Console.WriteLine($"  {label}: FAIL (verifier did not flag the issue)");
                failCounter++;
                return 0;
            }
            foreach (var d in res.Diagnostics)
            {
                if (d.OpcodeName.IndexOf(expectedOp, StringComparison.Ordinal) >= 0
                    && d.Message.IndexOf(expectedSubstring, StringComparison.Ordinal) >= 0)
                {
                    Console.WriteLine($"  {label}: OK ({d})");
                    return 1;
                }
            }
            Console.WriteLine($"  {label}: FAIL (expected '{expectedOp}' / '{expectedSubstring}' in diagnostics:");
            foreach (var d in res.Diagnostics) Console.WriteLine($"    {d}");
            Console.WriteLine(")");
            failCounter++;
            return 0;
        }

        // --inspect-precompiled <archive.rac>
        // Counts AST nodes whose CompiledBody was hydrated by the
        // v4 deserializer. Non-zero proves the runtime can skip the
        // lazy AST→IR compile entirely on first dispatch.
        private static void InspectPrecompiledCli(string archivePath)
        {
            if (!File.Exists(archivePath))
            {
                Console.WriteLine($"[Ra Language] archive not found: {archivePath}");
                Environment.ExitCode = 1;
                return;
            }
            try
            {
                using var archive = Interpreter.Archive.RacReader.Open(archivePath);
                var manifest = archive.Manifest;
                int totalFn = 0, hitFn = 0;
                int totalSm = 0, hitSm = 0;
                int totalTm = 0, hitTm = 0;
                int totalOp = 0, hitOp = 0;
                for (int i = 0; i < manifest.Modules.Count; i++)
                {
                    var m = manifest.Modules[i];
                    if (m.BytecodeSectionIndex < 0) continue;
                    byte[] payload = archive.ReadSection(m.BytecodeSectionIndex);
                    var fn = Interpreter.Archive.ModuleBytecodeIo.Deserialize(payload, archive.SharedConstPool);
                    CountPrecompiled(fn, new HashSet<Interpreter.IR.RaFunction>(),
                        ref totalFn, ref hitFn, ref totalSm, ref hitSm,
                        ref totalTm, ref hitTm, ref totalOp, ref hitOp);
                }
                Console.WriteLine($"[Ra Language] archive: {archivePath}");
                Console.WriteLine($"  FunctionDefinitionNode  : {hitFn}/{totalFn} pre-compiled");
                Console.WriteLine($"  StructMethodDefinition  : {hitSm}/{totalSm} pre-compiled");
                Console.WriteLine($"  TraitMethodDefinition   : {hitTm}/{totalTm} pre-compiled");
                Console.WriteLine($"  OperatorDefinition      : {hitOp}/{totalOp} pre-compiled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Ra Language] --inspect-precompiled failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }

        private static void CountPrecompiled(Interpreter.IR.RaFunction fn,
            HashSet<Interpreter.IR.RaFunction> seen,
            ref int totalFn, ref int hitFn,
            ref int totalSm, ref int hitSm,
            ref int totalTm, ref int hitTm,
            ref int totalOp, ref int hitOp)
        {
            if (fn == null || !seen.Add(fn)) return;
            if (fn.FuncDefRefs != null)
            {
                foreach (var node in fn.FuncDefRefs)
                {
                    if (node == null) continue;
                    totalFn++;
                    if (node.CompiledBody != null)
                    {
                        hitFn++;
                        CountPrecompiled(node.CompiledBody, seen,
                            ref totalFn, ref hitFn, ref totalSm, ref hitSm,
                            ref totalTm, ref hitTm, ref totalOp, ref hitOp);
                    }
                }
            }
            if (fn.DefineRefs != null)
            {
                foreach (var node in fn.DefineRefs)
                {
                    switch (node)
                    {
                        case Parser.Nodes.Classes.ClassDefinitionNode c:
                            foreach (var m in c.Methods)
                            {
                                totalFn++;
                                if (m.CompiledBody != null) { hitFn++;
                                    CountPrecompiled(m.CompiledBody, seen,
                                        ref totalFn, ref hitFn, ref totalSm, ref hitSm,
                                        ref totalTm, ref hitTm, ref totalOp, ref hitOp); }
                            }
                            foreach (var op in c.Operators)
                            {
                                totalOp++;
                                if (op.CompiledBody != null) { hitOp++;
                                    CountPrecompiled(op.CompiledBody, seen,
                                        ref totalFn, ref hitFn, ref totalSm, ref hitSm,
                                        ref totalTm, ref hitTm, ref totalOp, ref hitOp); }
                            }
                            break;
                        case Parser.Nodes.Structs.StructDefinitionNode s:
                            foreach (var m in s.Methods)
                            {
                                totalSm++;
                                if (m.CompiledBody != null) { hitSm++;
                                    CountPrecompiled(m.CompiledBody, seen,
                                        ref totalFn, ref hitFn, ref totalSm, ref hitSm,
                                        ref totalTm, ref hitTm, ref totalOp, ref hitOp); }
                            }
                            foreach (var op in s.Operators)
                            {
                                totalOp++;
                                if (op.CompiledBody != null) { hitOp++;
                                    CountPrecompiled(op.CompiledBody, seen,
                                        ref totalFn, ref hitFn, ref totalSm, ref hitSm,
                                        ref totalTm, ref hitTm, ref totalOp, ref hitOp); }
                            }
                            break;
                        case Parser.Nodes.Traits.TraitDefinitionNode t:
                            foreach (var m in t.Methods)
                            {
                                totalTm++;
                                if (m.CompiledBody != null) { hitTm++;
                                    CountPrecompiled(m.CompiledBody, seen,
                                        ref totalFn, ref hitFn, ref totalSm, ref hitSm,
                                        ref totalTm, ref hitTm, ref totalOp, ref hitOp); }
                            }
                            break;
                        case Parser.Nodes.Classes.ExtensionDefinitionNode e:
                            foreach (var m in e.Methods)
                            {
                                totalFn++;
                                if (m.CompiledBody != null) { hitFn++;
                                    CountPrecompiled(m.CompiledBody, seen,
                                        ref totalFn, ref hitFn, ref totalSm, ref hitSm,
                                        ref totalTm, ref hitTm, ref totalOp, ref hitOp); }
                            }
                            foreach (var op in e.Operators)
                            {
                                totalOp++;
                                if (op.CompiledBody != null) { hitOp++;
                                    CountPrecompiled(op.CompiledBody, seen,
                                        ref totalFn, ref hitFn, ref totalSm, ref hitSm,
                                        ref totalTm, ref hitTm, ref totalOp, ref hitOp); }
                            }
                            break;
                    }
                }
            }
        }

        // --verify-bytecode <archive.rac>
        // Walks every module's RaFunction tree and validates operand
        // bounds / jump targets / EH ranges / AST-ref pool indices.
        // Exit code 0 on PASS, 1 on FAIL.
        private static void VerifyBytecodeCli(string archivePath)
        {
            if (!File.Exists(archivePath))
            {
                Console.WriteLine($"[Ra Language] archive not found: {archivePath}");
                Environment.ExitCode = 1;
                return;
            }
            int verified = 0;
            var allDiags = new List<Interpreter.Archive.RacVerifyDiagnostic>();
            try
            {
                using var archive = Interpreter.Archive.RacReader.Open(archivePath);
                var manifest = archive.Manifest;
                for (int i = 0; i < manifest.Modules.Count; i++)
                {
                    var m = manifest.Modules[i];
                    if (m.BytecodeSectionIndex < 0)
                    {
                        Console.WriteLine($"[Ra Language] module #{i} '{m.LogicalPath}': skipped (no bytecode section)");
                        continue;
                    }
                    Interpreter.IR.RaFunction fn;
                    try
                    {
                        byte[] payload = archive.ReadSection(m.BytecodeSectionIndex);
                        fn = Interpreter.Archive.ModuleBytecodeIo.Deserialize(payload, archive.SharedConstPool);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Ra Language] module #{i} '{m.LogicalPath}': FAILED to deserialize bytecode: {ex.Message}");
                        Environment.ExitCode = 1;
                        return;
                    }
                    var res = Interpreter.Archive.RacBytecodeVerifier.Verify(fn);
                    verified++;
                    if (res.Ok)
                    {
                        Console.WriteLine($"[Ra Language] module #{i} '{m.LogicalPath}': OK");
                    }
                    else
                    {
                        Console.WriteLine($"[Ra Language] module #{i} '{m.LogicalPath}': FAILED ({res.Count} diagnostic{(res.Count == 1 ? "" : "s")})");
                        foreach (var d in res.Diagnostics)
                        {
                            Console.WriteLine(d.ToString());
                            allDiags.Add(d);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Ra Language] --verify-bytecode failed: {ex.Message}");
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine($"[Ra Language] verified {verified} module(s); {allDiags.Count} diagnostic(s) total");
            if (allDiags.Count > 0) Environment.ExitCode = 1;
        }

        // --verify-signature <archive> [--trusted-keys <dir>] [--strict] [--require-trusted-key]
        private static void VerifySignatureCli(string[] args)
        {
            string archivePath = args[1];
            string? trustedKeysDir = null;
            bool strict = false;
            bool requireTrustedKey = false;
            for (int i = 2; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--trusted-keys", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    trustedKeysDir = args[++i];
                else if (string.Equals(args[i], "--strict", StringComparison.OrdinalIgnoreCase))
                    strict = true;
                else if (string.Equals(args[i], "--require-trusted-key", StringComparison.OrdinalIgnoreCase))
                    requireTrustedKey = true;
                else
                {
                    Console.WriteLine($"[Ra Language] --verify-signature: unknown flag '{args[i]}'");
                    Environment.ExitCode = 1;
                    return;
                }
            }
            if (!File.Exists(archivePath))
            {
                Console.WriteLine($"[Ra Language] archive not found: {archivePath}");
                Environment.ExitCode = 1;
                return;
            }
            try
            {
                using var archive = Interpreter.Archive.RacReader.Open(archivePath);
                RacTrustStore? store = null;
                if (!string.IsNullOrEmpty(trustedKeysDir))
                {
                    store = RacKeyStore.LoadTrustStore(trustedKeysDir!);
                    Console.WriteLine($"[Ra Language] trust store: {store.Count} key(s) from {trustedKeysDir}");
                }
                var res = archive.VerifySignature(store);
                Console.WriteLine($"[Ra Language] signature status: {res.Status}");
                if (res.Section != null)
                {
                    Console.WriteLine($"  algorithm  : {RacSigner.DescribeAlgorithm(res.Section.Algorithm)}");
                    Console.WriteLine($"  key mode   : {res.Section.KeyMode}");
                    Console.WriteLine($"  signer id  : {(string.IsNullOrEmpty(res.Section.SignerId) ? "(none)" : res.Section.SignerId)}");
                    Console.WriteLine($"  fingerprint: {RacIntegrity.FormatHex(res.Section.Fingerprint)}");
                }
                if (res.Detail != null) Console.WriteLine($"  detail     : {res.Detail}");
                if (res.TrustedKey != null) Console.WriteLine($"  trusted by : {res.TrustedKey.SourcePath}");
                bool ok = res.Status == RacSignatureStatus.Valid
                    && (!requireTrustedKey || res.IsTrustedByStore);
                if (!ok && strict) Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Ra Language] --verify-signature failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
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

        // Bench harness for `--bench-lex`. Lexes a fixed in-memory source
        // buffer in a tight loop and reports throughput + allocation. The
        // lexer runs in complete isolation (no parser, no static analysis,
        // no VM) so a Lexer-only optimisation is measured without downstream
        // noise. Allocation is sampled via GC.GetAllocatedBytesForCurrentThread
        // across a batch, divided by iteration count.
        private static void BenchLexCli(string? file, int iter)
        {
            string text;
            string label;
            if (!string.IsNullOrEmpty(file))
            {
                if (!File.Exists(file))
                {
                    Console.WriteLine($"[Ra Language] --bench-lex: file not found: {file}");
                    Environment.ExitCode = 1;
                    return;
                }
                text = File.ReadAllText(file);
                label = file;
            }
            else
            {
                // Build a corpus from the test + bench .ra files shipped next
                // to the executable. Falls back to the CWD if not present.
                var roots = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "tests"),
                    Path.Combine(AppContext.BaseDirectory, "bench"),
                    Path.Combine(Directory.GetCurrentDirectory(), "tests"),
                    Path.Combine(Directory.GetCurrentDirectory(), "bench"),
                };
                var sb = new System.Text.StringBuilder(1 << 20);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int fileCount = 0;
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var path in Directory.EnumerateFiles(root, "*.ra", SearchOption.AllDirectories))
                    {
                        var full = Path.GetFullPath(path);
                        if (!seen.Add(full)) continue;
                        try { sb.Append(File.ReadAllText(path)); sb.Append('\n'); fileCount++; }
                        catch { }
                    }
                }
                if (fileCount == 0)
                {
                    Console.WriteLine("[Ra Language] --bench-lex: no corpus found (looked for tests/ and bench/ next to the exe and in the CWD); pass an explicit .ra file");
                    Environment.ExitCode = 1;
                    return;
                }
                text = sb.ToString();
                label = $"corpus ({fileCount} files)";
            }

            int charCount = text.Length;
            long byteCount = (long)charCount * 2;
            if (iter <= 0) iter = Math.Max(50, (int)(40_000_000L / Math.Max(1, charCount)));

            // Warm-up (also gives us the token count for ns/token).
            int tokenCount = 0;
            var hist = new Dictionary<RaLanguage.Lexer.Tokens.TokenType, int>();
            for (int i = 0; i < Math.Min(16, iter); i++)
            {
                var lx = new RaLanguage.Lexer.Lexer("bench", text);
                var (tk, _) = lx.MakeTokens();
                tokenCount = tk.Count;
                if (i == 0)
                    foreach (var t in tk)
                        hist[t.Type] = hist.TryGetValue(t.Type, out var cnt) ? cnt + 1 : 1;
            }

            Console.WriteLine($"[Ra Language] --bench-lex: {label}");
            Console.WriteLine($"  source : {charCount:N0} chars ({byteCount / 1024.0:N1} KiB UTF-16), {tokenCount:N0} tokens/iter, iter={iter:N0}");
            var top = new List<KeyValuePair<RaLanguage.Lexer.Tokens.TokenType, int>>(hist);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var histLine = new System.Text.StringBuilder("  tokens : ");
            for (int i = 0; i < Math.Min(8, top.Count); i++)
                histLine.Append($"{top[i].Key}={top[i].Value:N0}  ");
            Console.WriteLine(histLine.ToString());

            var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();

            long[] tickArr = new long[iter];
            for (int i = 0; i < iter; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var lx = new RaLanguage.Lexer.Lexer("bench", text);
                var (tk, _) = lx.MakeTokens();
                long t1 = Stopwatch.GetTimestamp();
                tickArr[i] = t1 - t0;
                if (tk.Count == int.MinValue) Console.WriteLine("unreachable"); // defeat dead-code elimination
            }

            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            var gcAfter = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

            Array.Sort(tickArr);
            double tickToNs = 1_000_000_000.0 / Stopwatch.Frequency;
            double best = tickArr[0] * tickToNs;
            double median = tickArr[iter / 2] * tickToNs;
            double p95 = tickArr[(int)(iter * 0.95)] * tickToNs;
            double sum = 0;
            for (int i = 0; i < iter; i++) sum += tickArr[i] * tickToNs;
            double mean = sum / iter;

            double bestMs = best / 1_000_000.0;
            double medMs = median / 1_000_000.0;
            double mbPerSec = (byteCount / (best / 1_000_000_000.0)) / (1024.0 * 1024.0);
            double nsPerToken = tokenCount > 0 ? best / tokenCount : 0;
            long allocPerIter = (allocAfter - allocBefore) / iter;
            double allocPerToken = tokenCount > 0 ? (double)allocPerIter / tokenCount : 0;

            Console.WriteLine($"  best   : {bestMs:F3} ms/iter   ({mbPerSec:N0} MiB/s, {nsPerToken:F1} ns/token)");
            Console.WriteLine($"  median : {medMs:F3} ms/iter");
            Console.WriteLine($"  mean   : {mean / 1_000_000.0:F3} ms/iter");
            Console.WriteLine($"  p95    : {p95 / 1_000_000.0:F3} ms/iter");
            Console.WriteLine($"  alloc  : {allocPerIter:N0} B/iter ({allocPerToken:F1} B/token)");
            Console.WriteLine($"  GC     : gen0 {gcAfter.Item1 - gcBefore.Item1}, gen1 {gcAfter.Item2 - gcBefore.Item2}, gen2 {gcAfter.Item3 - gcBefore.Item3} (over {iter:N0} iters)");
        }

        // Bench harness for `--bench-parse`. Lexes each corpus file ONCE
        // up-front, then drives the parser (tokens -> AST) in a tight loop with
        // a freshly-constructed Parser per pass so the token index resets. The
        // lexer, static analysis and VM never run inside the timed region, so a
        // parser-only optimisation shows up cleanly in ns/token, MB/s and
        // bytes-allocated-per-iteration. The per-pass parse-error total is a
        // behaviour fingerprint: a pure perf change MUST leave it unchanged.
        private static void BenchParseCli(string? file, int iter)
        {
            // Lex the corpus once into a stable list of (label, tokens, chars).
            // Each entry is parsed with its own Parser instance per pass.
            var units = new List<(string Label, List<RaLanguage.Lexer.Tokens.Token> Tokens, int Chars)>();
            long totalChars = 0;

            if (!string.IsNullOrEmpty(file))
            {
                if (!File.Exists(file))
                {
                    Console.WriteLine($"[Ra Language] --bench-parse: file not found: {file}");
                    Environment.ExitCode = 1;
                    return;
                }
                string t = File.ReadAllText(file);
                var lx = new RaLanguage.Lexer.Lexer("bench", t);
                var (tk, ld) = lx.MakeTokens();
                if (ld.HasErrors)
                {
                    Console.WriteLine($"[Ra Language] --bench-parse: lexing failed for {file}");
                    Environment.ExitCode = 1;
                    return;
                }
                units.Add((file, tk, t.Length));
                totalChars = t.Length;
            }
            else
            {
                var roots = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "tests"),
                    Path.Combine(AppContext.BaseDirectory, "bench"),
                    Path.Combine(Directory.GetCurrentDirectory(), "tests"),
                    Path.Combine(Directory.GetCurrentDirectory(), "bench"),
                };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var path in Directory.EnumerateFiles(root, "*.ra", SearchOption.AllDirectories))
                    {
                        var full = Path.GetFullPath(path);
                        if (!seen.Add(full)) continue;
                        try
                        {
                            string t = File.ReadAllText(path);
                            var lx = new RaLanguage.Lexer.Lexer("bench", t);
                            var (tk, ld) = lx.MakeTokens();
                            if (ld.HasErrors) continue; // skip files that don't even lex
                            units.Add((Path.GetFileName(path), tk, t.Length));
                            totalChars += t.Length;
                        }
                        catch { }
                    }
                }
                if (units.Count == 0)
                {
                    Console.WriteLine("[Ra Language] --bench-parse: no corpus found (looked for tests/ and bench/ next to the exe and in the CWD); pass an explicit .ra file");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            long totalTokens = 0;
            foreach (var u in units) totalTokens += u.Tokens.Count;
            long byteCount = totalChars * 2;
            if (iter <= 0) iter = Math.Max(50, (int)(20_000_000L / Math.Max(1, totalTokens)));

            // Warm-up + capture the behaviour fingerprint (parse-error total).
            int errFingerprint = 0;
            for (int i = 0; i < Math.Min(8, iter); i++)
            {
                int errs = 0;
                foreach (var u in units)
                {
                    var p = new Parser.Parser(u.Tokens);
                    var pr = p.Parse();
                    if (pr.HasErrors) errs += pr.Diagnostics.ErrorCount;
                }
                errFingerprint = errs;
            }

            Console.WriteLine($"[Ra Language] --bench-parse: corpus ({units.Count} files)");
            Console.WriteLine($"  source : {totalChars:N0} chars ({byteCount / 1024.0:N1} KiB UTF-16), {totalTokens:N0} tokens/iter, iter={iter:N0}");
            Console.WriteLine($"  parse  : {errFingerprint:N0} parse-errors/pass (behaviour fingerprint — must be stable across perf changes)");

            var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();

            long[] tickArr = new long[iter];
            int sink = 0;
            for (int i = 0; i < iter; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                for (int u = 0; u < units.Count; u++)
                {
                    var p = new Parser.Parser(units[u].Tokens);
                    var pr = p.Parse();
                    if (pr.Node != null) sink++;
                }
                long t1 = Stopwatch.GetTimestamp();
                tickArr[i] = t1 - t0;
            }
            if (sink == int.MinValue) Console.WriteLine("unreachable"); // defeat DCE

            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            var gcAfter = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

            Array.Sort(tickArr);
            double tickToNs = 1_000_000_000.0 / Stopwatch.Frequency;
            double best = tickArr[0] * tickToNs;
            double median = tickArr[iter / 2] * tickToNs;
            double p95 = tickArr[(int)(iter * 0.95)] * tickToNs;
            double sum = 0;
            for (int i = 0; i < iter; i++) sum += tickArr[i] * tickToNs;
            double mean = sum / iter;

            double bestMs = best / 1_000_000.0;
            double medMs = median / 1_000_000.0;
            double mbPerSec = (byteCount / (best / 1_000_000_000.0)) / (1024.0 * 1024.0);
            double nsPerToken = totalTokens > 0 ? best / totalTokens : 0;
            long allocPerIter = (allocAfter - allocBefore) / iter;
            double allocPerToken = totalTokens > 0 ? (double)allocPerIter / totalTokens : 0;

            Console.WriteLine($"  best   : {bestMs:F3} ms/iter   ({mbPerSec:N0} MiB/s, {nsPerToken:F1} ns/token)");
            Console.WriteLine($"  median : {medMs:F3} ms/iter");
            Console.WriteLine($"  mean   : {mean / 1_000_000.0:F3} ms/iter");
            Console.WriteLine($"  p95    : {p95 / 1_000_000.0:F3} ms/iter");
            Console.WriteLine($"  alloc  : {allocPerIter:N0} B/iter ({allocPerToken:F1} B/token)");
            Console.WriteLine($"  GC     : gen0 {gcAfter.Item1 - gcBefore.Item1}, gen1 {gcAfter.Item2 - gcBefore.Item2}, gen2 {gcAfter.Item3 - gcBefore.Item3} (over {iter:N0} iters)");
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
            // Each run owns its exit code: clear any failure recorded by a
            // previous menu iteration so a clean run reports success, and a
            // failing run (compile abort inside Run, or an uncaught runtime
            // error below) reports a non-zero code the shell / CI can observe.
            Environment.ExitCode = 0;
            try
            {
                string text = File.ReadAllText(fileName);
                Stopwatch sw = Stopwatch.StartNew();

                var (result, error) = Run(fileName, text);
                sw.Stop();

                if (error != null)
                {
                    Console.WriteLine(error.ToString());
                    Environment.ExitCode = 1;
                }

                if (diagnostics)
                {
                    Console.WriteLine($"[Ra Language] Execution took {sw.ElapsedMilliseconds}ms / {sw.ElapsedTicks} ticks / {sw.Elapsed.TotalNanoseconds}ns.");
                }
            }
            catch (Exception ex)
            {
                // A managed exception escaped the run pipeline (file read, or
                // an internal interpreter/VM/FFI failure). Surface it on stderr
                // regardless of the diagnostics flag — silently swallowing it
                // (the old behaviour) left path-mode failures invisible.
                Environment.ExitCode = 1;
                Console.Error.WriteLine($"[Ra Language] Unhandled error: {ex}");
            }
        }
    }
}
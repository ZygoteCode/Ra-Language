using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace RaLanguage.Interpreter.Runtime.Csharp
{
    /// <summary>
    /// Compiles and executes inline C# source via Roslyn scripting.
    ///
    /// Architectural notes:
    /// * Each block is materialised once into a <see cref="Script{TResult}"/> and cached
    ///   keyed by <see cref="CsharpExecutionOptions"/>. Subsequent invocations skip
    ///   recompilation entirely.
    /// * Compilation diagnostics are collected up-front so the visitor can surface them
    ///   as Ra <see cref="Errors.Types.RuntimeError"/>s with accurate file/line spans.
    /// * Roslyn scripting depends on System.Reflection.Emit. NativeAOT compilation strips
    ///   dynamic-code support, so the executor short-circuits with a clear error in that
    ///   environment. The capability gate also lets users probe support at runtime via
    ///   <see cref="IsSupported"/>.
    /// </summary>
    public static class CsharpExecutor
    {
        private static readonly ConcurrentDictionary<CsharpExecutionOptions, Script<object>> s_scriptCache
            = new ConcurrentDictionary<CsharpExecutionOptions, Script<object>>();

        private static readonly Assembly[] s_baseAssemblies = ResolveBaseAssemblies();

        // Roslyn scripting genuinely requires System.Reflection.Emit. Under a NativeAOT
        // *publish* the runtime cannot service that, and the script engine throws
        // PlatformNotSupportedException at compile time. But the `PublishAot=true` flag
        // *also* lowers `RuntimeFeature.IsDynamicCodeSupported` to `false` for JIT runs
        // (so that AOT-bound code does not accidentally use codegen) — meaning that
        // signal is not a reliable runtime gate. The executor instead optimistically
        // tries the compile and converts the platform exception into a friendly error.
        public static bool IsSupported => true;

        public static (object? Value, Exception? Error, IReadOnlyList<string> Diagnostics) Execute(CsharpExecutionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            Script<object> script;
            try
            {
                script = s_scriptCache.GetOrAdd(options, BuildScript);
            }
            catch (CsharpCompileException cce)
            {
                return (null, cce, cce.Diagnostics);
            }
            catch (PlatformNotSupportedException pex)
            {
                return (null, BuildAotUnsupportedException(pex), Array.Empty<string>());
            }
            catch (Exception ex) when (IsAotCodegenFailure(ex))
            {
                return (null, BuildAotUnsupportedException(ex), Array.Empty<string>());
            }
            catch (Exception ex)
            {
                return (null, ex, Array.Empty<string>());
            }

            try
            {
                ScriptState<object> state = RunScriptSync(script);
                return (state.ReturnValue, null, Array.Empty<string>());
            }
            catch (CompilationErrorException cee)
            {
                var diagnostics = cee.Diagnostics.Select(d => d.ToString()).ToArray();
                return (null, cee, diagnostics);
            }
            catch (PlatformNotSupportedException pex)
            {
                return (null, BuildAotUnsupportedException(pex), Array.Empty<string>());
            }
            catch (AggregateException agg) when (agg.InnerExceptions.Count == 1)
            {
                return (null, new CsharpRuntimeException(agg.InnerExceptions[0].Message, agg.InnerExceptions[0]), Array.Empty<string>());
            }
            catch (Exception ex)
            {
                return (null, new CsharpRuntimeException(ex.Message, ex), Array.Empty<string>());
            }
        }

        private static CsharpUnsupportedException BuildAotUnsupportedException(Exception inner)
        {
            return new CsharpUnsupportedException(
                "inline csharp { ... } blocks require dynamic code generation, which is not " +
                "available in this build. Roslyn scripting depends on System.Reflection.Emit, " +
                "which NativeAOT publish artifacts cannot service. Run the interpreter as a JIT " +
                "build (dotnet run / dotnet build) to enable inline C# execution. " +
                "Underlying error: " + inner.Message);
        }

        private static bool IsAotCodegenFailure(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is PlatformNotSupportedException) return true;
                string m = cur.Message ?? "";
                if (m.Contains("Reflection.Emit", StringComparison.OrdinalIgnoreCase)) return true;
                if (m.Contains("dynamic code", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static void ClearCache() => s_scriptCache.Clear();

        public static int CachedScriptCount => s_scriptCache.Count;

        private static Script<object> BuildScript(CsharpExecutionOptions options)
        {
            var resolvedRefs = new List<MetadataReference>();
            var resolveErrors = new List<string>();
            foreach (var refSpec in options.References)
            {
                try
                {
                    var mr = ResolveReference(refSpec);
                    if (mr != null) resolvedRefs.Add(mr);
                }
                catch (Exception ex)
                {
                    resolveErrors.Add($"cannot resolve csharp reference '{refSpec}': {ex.Message}");
                }
            }

            if (resolveErrors.Count > 0)
                throw new CsharpCompileException(string.Join("; ", resolveErrors), resolveErrors);

            var scriptOptions = ScriptOptions.Default
                .WithReferences(s_baseAssemblies)
                .AddReferences(resolvedRefs)
                .WithImports(BuildImports(options.Usings))
                .WithEmitDebugInformation(false)
                .WithAllowUnsafe(true)
                .WithCheckOverflow(false);

            var script = CSharpScript.Create<object>(options.Source, scriptOptions, globalsType: null);

            var compilation = script.GetCompilation();
            var diags = compilation.GetDiagnostics();
            var errors = new List<string>();
            foreach (var d in diags)
            {
                if (d.Severity == DiagnosticSeverity.Error)
                    errors.Add(d.ToString());
            }

            if (errors.Count > 0)
                throw new CsharpCompileException("csharp compile errors: " + string.Join("; ", errors), errors);

            return script;
        }

        private static ScriptState<object> RunScriptSync(Script<object> script)
        {
            // Run the script synchronously without forcing TaskScheduler.RunSynchronously,
            // which deadlocks if the script awaits another awaitable on the same scheduler.
            // GetAwaiter().GetResult() unwraps AggregateException into the inner exception.
            return script.RunAsync(globals: null, cancellationToken: CancellationToken.None)
                         .GetAwaiter()
                         .GetResult();
        }

        private static IEnumerable<string> BuildImports(IReadOnlyList<string> userImports)
        {
            // Defaults mirror what `dotnet-script` ships with so the common case "compute
            // a value, return it" works out of the box. Users still need to opt in to
            // namespaces like System.Net.Http or Newtonsoft.Json.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ns in new[]
            {
                "System",
                "System.Collections",
                "System.Collections.Generic",
                "System.Linq",
                "System.Text",
                "System.IO",
                "System.Globalization",
                "System.Threading",
                "System.Threading.Tasks",
                "System.Numerics"
            })
            {
                if (seen.Add(ns)) yield return ns;
            }

            foreach (var u in userImports)
            {
                if (!string.IsNullOrWhiteSpace(u) && seen.Add(u)) yield return u;
            }
        }

        private static MetadataReference? ResolveReference(string spec)
        {
            // Accept three shapes:
            //   1) absolute path -> use directly
            //   2) relative path -> resolve against CWD then AppContext.BaseDirectory
            //   3) simple assembly name (with or without .dll) -> Assembly.Load
            if (string.IsNullOrWhiteSpace(spec)) return null;

            if (Path.IsPathRooted(spec) && File.Exists(spec))
                return MetadataReference.CreateFromFile(spec);

            string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), spec);
            if (File.Exists(cwdPath))
                return MetadataReference.CreateFromFile(cwdPath);

            string basePath = Path.Combine(AppContext.BaseDirectory, spec);
            if (File.Exists(basePath))
                return MetadataReference.CreateFromFile(basePath);

            string trimmed = spec.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? spec.Substring(0, spec.Length - 4)
                : spec;

            try
            {
                var asm = Assembly.Load(new AssemblyName(trimmed));
                if (!string.IsNullOrEmpty(asm.Location))
                    return MetadataReference.CreateFromFile(asm.Location);
                return MetadataReference.CreateFromImage(System.IO.File.ReadAllBytes(asm.Location));
            }
            catch
            {
                throw new FileNotFoundException($"could not locate '{spec}' as a path, app-base path, or loaded assembly");
            }
        }

        private static Assembly[] ResolveBaseAssemblies()
        {
            var set = new HashSet<Assembly>();
            void Add(Assembly? a) { if (a != null) set.Add(a); }

            Add(typeof(object).Assembly);
            Add(typeof(Console).Assembly);
            Add(typeof(System.Linq.Enumerable).Assembly);
            Add(typeof(System.Collections.Generic.List<>).Assembly);
            Add(typeof(System.Text.StringBuilder).Assembly);
            Add(typeof(System.IO.File).Assembly);
            Add(typeof(System.Threading.Tasks.Task).Assembly);
            Add(typeof(System.Globalization.CultureInfo).Assembly);
            Add(typeof(System.Numerics.BigInteger).Assembly);
            Add(typeof(Uri).Assembly);
            Add(typeof(System.Text.RegularExpressions.Regex).Assembly);
            Add(typeof(System.Runtime.InteropServices.Marshal).Assembly);
            Add(typeof(System.Reflection.Assembly).Assembly);

            // Add System.Runtime + netstandard explicitly — Roslyn scripting needs both visible
            // even when the host process doesn't have them as direct references.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (name == "System.Runtime" || name == "netstandard" || name == "System.Private.CoreLib")
                    Add(asm);
            }

            return set.ToArray();
        }
    }
}

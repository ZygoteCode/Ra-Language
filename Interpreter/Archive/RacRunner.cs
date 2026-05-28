using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Interpreter.Modules;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Visitors.Imports;

namespace RaLanguage.Interpreter.Archive
{
    public sealed class RacRunOptions
    {
        public string ArchivePath { get; set; } = "";
        public bool Diagnostics { get; set; } = true;
    }

    public sealed class RacRunResult
    {
        public bool Loaded { get; init; }
        public bool Executed { get; init; }
        public TimeSpan LoadTime { get; init; }
        public TimeSpan ExecTime { get; init; }
        public RuntimeValue? Value { get; init; }
        public Error? RuntimeError { get; init; }
        public List<string> LoadErrors { get; init; } = new();
    }

    // Loads a .rac archive into the in-process runtime and executes the
    // entry module. Sequence:
    //
    //   1. Open archive (validates header + manifest hash).
    //   2. Read each ModuleSource section, verify section hash AND the
    //      manifest-recorded source hash (defense in depth).
    //   3. Mount every module source into VirtualFs at the manifest's
    //      AbsoluteVirtualPath.
    //   4. Re-initialise the standard SymbolTable and ModuleManager,
    //      rooting them at the virtual entry directory.
    //   5. Invoke the standard `Program.Run` pipeline against the entry
    //      source. ModuleResolver.Resolve and ModuleManager.Load now hit
    //      the overlay before disk, so `import "./greet.ra"` resolves
    //      against the bundled module set without writing any file.
    //   6. Unmount the overlay on the way out so future direct runs in
    //      the same process see a clean filesystem.
    public static class RacRunner
    {
        public static RacRunResult Run(RacRunOptions opts)
        {
            var loadSw = Stopwatch.StartNew();
            var errors = new List<string>();

            RacArchive archive;
            try { archive = RacReader.Open(opts.ArchivePath); }
            catch (Exception ex)
            {
                errors.Add($"failed to open archive: {ex.Message}");
                loadSw.Stop();
                return new RacRunResult
                {
                    Loaded = false,
                    Executed = false,
                    LoadTime = loadSw.Elapsed,
                    LoadErrors = errors,
                };
            }

            try
            {
                var manifest = archive.Manifest;
                if (manifest.Modules.Count == 0)
                {
                    errors.Add("archive contains no modules");
                    return Failed(loadSw, errors);
                }

                // Each module's virtual path may collide with real paths
                // on this machine — push everything under a per-run
                // namespace so the overlay never shadows on-disk files
                // by accident. We do not need to touch the disk at all.
                string runId = Guid.NewGuid().ToString("N");
                string virtualRoot = NormaliseRoot(Path.Combine(
                    Path.GetTempPath(), "ra-rac", runId));

                // Build the (virtual-archive-path → host-virtual-path)
                // remap; the host paths are what the resolver actually
                // sees, since builds may have been done on a different
                // OS.
                var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? entryHostPath = null;

                for (int i = 0; i < manifest.Modules.Count; i++)
                {
                    var m = manifest.Modules[i];
                    string hostPath = MapToHost(virtualRoot, m);
                    remap[m.AbsoluteVirtualPath] = hostPath;
                    if (i == manifest.EntryModuleIndex) entryHostPath = hostPath;
                }
                if (entryHostPath == null)
                {
                    errors.Add("archive has no entry module");
                    return Failed(loadSw, errors);
                }

                // Read + verify each source section, then mount it into
                // VirtualFs under the host-mapped path.
                var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < manifest.Modules.Count; i++)
                {
                    var m = manifest.Modules[i];
                    if (m.SourceSectionIndex < 0)
                    {
                        errors.Add($"module #{i} ('{m.LogicalPath}') has no source section");
                        return Failed(loadSw, errors);
                    }
                    byte[] payload = archive.ReadSection(m.SourceSectionIndex);
                    byte[] srcHash = RacIntegrity.Hash(payload);
                    if (!RacIntegrity.Equal(srcHash, m.SourceHash))
                    {
                        errors.Add(
                            $"module #{i} ('{m.LogicalPath}') hash mismatch between section payload and manifest record");
                        return Failed(loadSw, errors);
                    }
                    string sourceText = Encoding.UTF8.GetString(payload);
                    string hostPath = remap[m.AbsoluteVirtualPath];
                    contents[hostPath] = sourceText;
                }

                // Mount overlay.
                VirtualFs.Clear();
                foreach (var kvp in contents)
                {
                    VirtualFs.Mount(kvp.Key, kvp.Value);
                }

                loadSw.Stop();

                // Drive the standard pipeline. We mimic ExecuteMainFile
                // closely but read from the overlay rather than the
                // disk, and root the module manager at the virtual
                // project root.
                IrExpressionEvaluator.ClearCache();
                Program.InitializeSymbolTable();

                // Override the module manager with one rooted at the
                // virtual project directory of the entry. Std root
                // continues to point at the embedded archive's std/
                // subtree if any, otherwise at the executable-relative
                // path (where there are no entries — std imports
                // bundled in the archive are addressed by absolute
                // host path anyway).
                string virtualProject = Path.GetDirectoryName(entryHostPath) ?? virtualRoot;
                string virtualStd = Path.Combine(virtualRoot, "std");
                if (!Directory.Exists(virtualStd))
                {
                    // The directory does not exist on the host disk;
                    // that's fine — std modules resolve via the
                    // overlay using their archived absolute path.
                    virtualStd = virtualRoot;
                }
                ImportNodeVisitor.InitializeModuleManager(
                    virtualProject,
                    virtualStd,
                    () => Program.BuiltinSymbolTable);
                ImportNodeVisitor.ResetCache();

                string entrySource = contents[entryHostPath];
                var execSw = Stopwatch.StartNew();
                ValueResult run;
                try
                {
                    run = Program.Run(entryHostPath, entrySource);
                }
                catch (Exception ex)
                {
                    execSw.Stop();
                    errors.Add($"runtime exception: {ex.Message}");
                    return new RacRunResult
                    {
                        Loaded = true,
                        Executed = false,
                        LoadTime = loadSw.Elapsed,
                        ExecTime = execSw.Elapsed,
                        LoadErrors = errors,
                    };
                }
                execSw.Stop();

                return new RacRunResult
                {
                    Loaded = true,
                    Executed = true,
                    LoadTime = loadSw.Elapsed,
                    ExecTime = execSw.Elapsed,
                    Value = run.Item1,
                    RuntimeError = run.Item2,
                };
            }
            finally
            {
                archive.Dispose();
                VirtualFs.Clear();
            }
        }

        private static RacRunResult Failed(Stopwatch loadSw, List<string> errors)
        {
            if (loadSw.IsRunning) loadSw.Stop();
            return new RacRunResult
            {
                Loaded = false,
                Executed = false,
                LoadTime = loadSw.Elapsed,
                LoadErrors = errors,
            };
        }

        private static string MapToHost(string virtualRoot, RacModuleRecord record)
        {
            // Preserve the original directory structure beneath a
            // per-kind subdir so relative imports from the entry
            // (`import "./sibling.ra"`) still hit the right virtual
            // path. Std modules already carry a `std/` prefix in their
            // LogicalPath, so they land under `<virtualRoot>/std/...`.
            string logical = record.LogicalPath;
            if (string.IsNullOrEmpty(logical)) logical = $"mod_{record.Index}.ra";
            // Sanitise each path segment independently — backslash and
            // forward slash both delimit, anything else illegal on the
            // host is rewritten to underscore.
            string[] segments = logical.Split(new[] { '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries);
            string host;
            if (record.Kind == RacModuleKind.StdLib
                && segments.Length > 0
                && string.Equals(segments[0], "std", StringComparison.OrdinalIgnoreCase))
            {
                // Avoid double "std/std" — the LogicalPath already
                // includes the std root.
                host = virtualRoot;
                foreach (var s in segments) host = Path.Combine(host, SanitiseFileName(s));
            }
            else
            {
                host = Path.Combine(virtualRoot, "src");
                foreach (var s in segments) host = Path.Combine(host, SanitiseFileName(s));
            }
            return Path.GetFullPath(host);
        }

        private static string SanitiseFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string NormaliseRoot(string p) => Path.GetFullPath(p);
    }
}

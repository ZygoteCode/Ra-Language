using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Interpreter.IR;
using RaLanguage.Interpreter.Modules;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Visitors.Imports;
using RaLanguage.Interpreter.Vm;

namespace RaLanguage.Interpreter.Archive
{
    public sealed class RacRunOptions
    {
        public string ArchivePath { get; set; } = "";
        public bool Diagnostics { get; set; } = true;

        // v1.2 (#sig): signature verification policy.
        //
        //   StrictSignature       — reject archives that fail any of:
        //                           missing signature, malformed
        //                           section, unsupported algorithm,
        //                           bad signature math, or (when
        //                           RequireTrustedKey is on) embedded
        //                           key not present in the trust
        //                           store.
        //   TrustedKeysDir        — directory of *.pub PEM files
        //                           indexed by SHA-256 fingerprint.
        //                           Required for Fingerprint-mode
        //                           signatures and for the
        //                           RequireTrustedKey embedded check.
        //   RequireTrustedKey     — even in Embedded mode, demand the
        //                           embedded key's fingerprint be in
        //                           the trust store. Default off so a
        //                           self-signed archive verifies
        //                           against itself (tamper-evident but
        //                           not signer-authenticated).
        public bool StrictSignature { get; set; }
        public string? TrustedKeysDir { get; set; }
        public bool RequireTrustedKey { get; set; }

        // v1.2 (#verify): structural bytecode verification pass.
        // Defaults on so a corrupt or maliciously crafted RaFunction
        // tree fails fast with a precise diagnostic rather than a
        // late IndexOutOfRangeException inside the dispatch loop.
        // Operators may opt out via --no-verify-bytecode for a tiny
        // load-time win on a fully-trusted archive (the verifier
        // already runs O(Code.Length) per module).
        public bool VerifyBytecode { get; set; } = true;
    }

    public sealed class RacRunResult
    {
        public bool Loaded { get; init; }
        public bool Executed { get; init; }
        public TimeSpan LoadTime { get; init; }
        // Sub-timing: just the RacReader.Open call (mmap + header +
        // section dir hash + manifest decode). v1.1 (#4) targets <1ms
        // for this regardless of total archive size.
        public TimeSpan ArchiveOpenTime { get; init; }
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
            var openSw = Stopwatch.StartNew();
            var errors = new List<string>();
            TimeSpan archiveOpenElapsed = TimeSpan.Zero;

            RacArchive archive;
            try { archive = RacReader.Open(opts.ArchivePath); }
            catch (Exception ex)
            {
                openSw.Stop();
                archiveOpenElapsed = openSw.Elapsed;
                errors.Add($"failed to open archive: {ex.Message}");
                loadSw.Stop();
                return new RacRunResult
                {
                    Loaded = false,
                    Executed = false,
                    LoadTime = loadSw.Elapsed,
                    ArchiveOpenTime = archiveOpenElapsed,
                    LoadErrors = errors,
                };
            }
            openSw.Stop();
            archiveOpenElapsed = openSw.Elapsed;

            // v1.2 (#sig): verify signature before any payload work.
            // A bad signature short-circuits the whole load so we
            // never deserialize bytecode from a tampered archive.
            RacTrustStore? trustStore = null;
            if (!string.IsNullOrEmpty(opts.TrustedKeysDir))
            {
                try { trustStore = RacKeyStore.LoadTrustStore(opts.TrustedKeysDir!); }
                catch (Exception ex)
                {
                    errors.Add($"failed to load trust store '{opts.TrustedKeysDir}': {ex.Message}");
                    archive.Dispose();
                    return Failed(loadSw, errors, archiveOpenElapsed);
                }
            }
            var sigResult = archive.VerifySignature(trustStore);
            if (opts.StrictSignature)
            {
                bool ok;
                switch (sigResult.Status)
                {
                    case RacSignatureStatus.Valid:
                        // In RequireTrustedKey mode, embedded keys
                        // additionally need to appear in the trust
                        // store. The verifier already cross-checked
                        // when a trust store was supplied.
                        ok = !opts.RequireTrustedKey || sigResult.IsTrustedByStore;
                        if (!ok)
                            errors.Add($"--strict-signature: archive is signed but the signing key is not in the trust store");
                        break;
                    case RacSignatureStatus.Missing:
                        ok = false;
                        errors.Add("--strict-signature: archive is unsigned");
                        break;
                    case RacSignatureStatus.Malformed:
                        ok = false;
                        errors.Add($"--strict-signature: signature section is malformed ({sigResult.Detail})");
                        break;
                    case RacSignatureStatus.AlgorithmUnsupported:
                        ok = false;
                        errors.Add($"--strict-signature: loader does not support signature algorithm ({sigResult.Detail})");
                        break;
                    case RacSignatureStatus.UntrustedKey:
                        ok = false;
                        errors.Add($"--strict-signature: signing key is not trusted ({sigResult.Detail})");
                        break;
                    case RacSignatureStatus.Invalid:
                        ok = false;
                        errors.Add($"--strict-signature: signature verification failed ({sigResult.Detail})");
                        break;
                    default:
                        ok = false;
                        errors.Add($"--strict-signature: unexpected verification status {sigResult.Status}");
                        break;
                }
                if (!ok)
                {
                    archive.Dispose();
                    return Failed(loadSw, errors, archiveOpenElapsed);
                }
            }
            else if (opts.Diagnostics && sigResult.Status == RacSignatureStatus.Invalid)
            {
                // Non-strict mode still surfaces a warning when the
                // archive carries a signature that does not check out:
                // a tampered archive should not silently execute.
                Console.WriteLine($"[Ra Language] WARNING: signature present but invalid ({sigResult.Detail}); continuing because --strict-signature was not set.");
            }

            try
            {
                var manifest = archive.Manifest;
                if (manifest.Modules.Count == 0)
                {
                    errors.Add("archive contains no modules");
                    return Failed(loadSw, errors, archiveOpenElapsed);
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
                    return Failed(loadSw, errors, archiveOpenElapsed);
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
                        return Failed(loadSw, errors, archiveOpenElapsed);
                    }
                    byte[] payload = archive.ReadSection(m.SourceSectionIndex);
                    byte[] srcHash = RacIntegrity.Hash(payload);
                    if (!RacIntegrity.Equal(srcHash, m.SourceHash))
                    {
                        errors.Add(
                            $"module #{i} ('{m.LogicalPath}') hash mismatch between section payload and manifest record");
                        return Failed(loadSw, errors, archiveOpenElapsed);
                    }
                    string sourceText = System.Text.Encoding.UTF8.GetString(payload);
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
                // Locate the std root inside the overlay. MapToHost
                // places std-classified modules under `<virtualRoot>/std/`
                // (when their LogicalPath already has the `std/` prefix
                // we strip the duplicate; otherwise we mount under
                // `<virtualRoot>/src/`). The std root for ModuleResolver
                // must point at the *same* directory we mounted into.
                // VirtualFs.Exists is the authoritative check, not the
                // on-disk Directory.Exists — the overlay never touches
                // the real filesystem.
                string virtualStd = Path.Combine(virtualRoot, "std");
                bool overlayHasStd = false;
                foreach (var mountedPath in contents.Keys)
                {
                    if (mountedPath.StartsWith(virtualStd, StringComparison.OrdinalIgnoreCase))
                    {
                        overlayHasStd = true;
                        break;
                    }
                }
                if (!overlayHasStd)
                {
                    // No std modules in the overlay — point the resolver
                    // at the virtual root so it still has a valid path.
                    virtualStd = virtualRoot;
                }
                ImportNodeVisitor.InitializeModuleManager(
                    virtualProject,
                    virtualStd,
                    () => Program.BuiltinSymbolTable);
                ImportNodeVisitor.ResetCache();

                string entrySource = contents[entryHostPath];

                // v1.2 (#1): pre-deserialise every module's
                // ModuleBytecode payload up-front and register the
                // resulting RaFunction trees with ModuleManager. Any
                // subsequent `import` lookup hits the precompiled fast
                // path inside ModuleManager.Load — no lex / parse /
                // Resolver work at runtime for the imports either.
                // Source sections continue to back diagnostics and the
                // overlay-mounted VirtualFs (so file:line:col still
                // resolves to real text when an error surfaces) but the
                // loader never touches them.
                RaFunction? entryBytecodeFn = null;
                for (int mi = 0; mi < manifest.Modules.Count; mi++)
                {
                    var m = manifest.Modules[mi];
                    if (m.BytecodeSectionIndex < 0) continue;
                    RaFunction fn;
                    try
                    {
                        byte[] bcPayload = archive.ReadSection(m.BytecodeSectionIndex);
                        fn = ModuleBytecodeIo.Deserialize(bcPayload, archive.SharedConstPool);
                    }
                    catch (Exception ex)
                    {
                        if (opts.Diagnostics)
                            Console.WriteLine(
                                $"[Ra Language] module '{m.LogicalPath}' bytecode payload unusable ({ex.Message}); falling back to source for that module.");
                        continue;
                    }
                    if (opts.VerifyBytecode)
                    {
                        var vres = RacBytecodeVerifier.Verify(fn);
                        if (!vres.Ok)
                        {
                            errors.Add(
                                $"module '{m.LogicalPath}' bytecode verifier failed:\n{vres.FormatReport()}");
                            return Failed(loadSw, errors, archiveOpenElapsed);
                        }
                    }
                    string hostPath = remap[m.AbsoluteVirtualPath];
                    if (mi == manifest.EntryModuleIndex) entryBytecodeFn = fn;
                    else ImportNodeVisitor.ModuleManager.RegisterPrecompiled(hostPath, fn);
                }

                var execSw = Stopwatch.StartNew();
                ValueResult run;
                try
                {
                    if (entryBytecodeFn != null)
                    {
                        run = RunBytecode(entryBytecodeFn, entryHostPath);
                    }
                    else
                    {
                        run = Program.Run(entryHostPath, entrySource);
                    }
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
                        ArchiveOpenTime = openSw.Elapsed,
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
                    ArchiveOpenTime = openSw.Elapsed,
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

        // Drive a deserialised RaFunction through the VM without
        // re-running lex/parse/IR-compile. Mirrors the tail of
        // Program.Run() so semantics match exactly.
        private static ValueResult RunBytecode(RaFunction script, string entryPath)
        {
            var interpreter = new Interpreter();
            var context = new Context(entryPath);
            context.SymbolTable = Program.GlobalSymbolTable;
            var vm = new VmExecutor(interpreter);
            var task = vm.RunScript(script, context);
            var result = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            return (result.Value, result.Error);
        }

        private static RacRunResult Failed(Stopwatch loadSw, List<string> errors, TimeSpan openElapsed = default)
        {
            if (loadSw.IsRunning) loadSw.Stop();
            return new RacRunResult
            {
                Loaded = false,
                Executed = false,
                LoadTime = loadSw.Elapsed,
                ArchiveOpenTime = openElapsed,
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

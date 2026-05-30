using System;
using System.Collections.Concurrent;
using System.IO;

namespace RaLanguage.Interpreter.Archive
{
    // Process-wide virtual filesystem overlay consulted by ModuleResolver
    // and ModuleManager. The default behaviour is pass-through to
    // System.IO, so the regular disk-based code path is unaffected. The
    // archive runner pre-populates this overlay with the bundled module
    // sources before invoking the standard interpreter pipeline.
    //
    // Lookups are case-insensitive on Windows (matching ModuleManager's
    // OrdinalIgnoreCase cache key) and case-sensitive elsewhere.
    //
    // Entries are by absolute path, normalised via Path.GetFullPath
    // upstream. The overlay holds raw UTF-8 source text.
    public static class VirtualFs
    {
        private static readonly StringComparer Comparer =
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private static readonly StringComparison PathComparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static ConcurrentDictionary<string, string> _entries
            = new(Comparer);

        // Bumped each time the overlay is mutated so callers that
        // captured a snapshot can detect staleness cheaply.
        public static int Generation { get; private set; }

        public static bool HasAnyOverlay => !_entries.IsEmpty;

        public static void Clear()
        {
            _entries = new ConcurrentDictionary<string, string>(Comparer);
            Generation++;
        }

        public static void Mount(string absolutePath, string content)
        {
            if (string.IsNullOrEmpty(absolutePath))
                throw new ArgumentException("absolutePath", nameof(absolutePath));
            _entries[absolutePath] = content;
            Generation++;
        }

        public static bool Exists(string absolutePath)
        {
            if (_entries.ContainsKey(absolutePath)) return true;
            return File.Exists(absolutePath);
        }

        public static string ReadAllText(string absolutePath)
        {
            if (_entries.TryGetValue(absolutePath, out var content))
                return content;
            return File.ReadAllText(absolutePath);
        }

        public static bool TryGetOverlay(string absolutePath, out string? content)
        {
            if (_entries.TryGetValue(absolutePath, out var v))
            {
                content = v;
                return true;
            }
            content = null;
            return false;
        }

        // A directory "exists" if it is a real disk directory, or — in
        // archive (overlay) mode — if any mounted file path lives under it.
        // Used by the module resolver to recognise std sub-packages.
        public static bool DirectoryExists(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return false;
            if (Directory.Exists(absolutePath)) return true;
            string prefix = EnsureTrailingSeparator(absolutePath);
            foreach (var key in _entries.Keys)
                if (key.StartsWith(prefix, PathComparison))
                    return true;
            return false;
        }

        // Enumerates *.ra files directly under (recursive == false) or
        // anywhere beneath (recursive == true) `absoluteDir`, unioning the
        // real disk with the archive overlay. Returned paths are absolute.
        public static IEnumerable<string> EnumerateRaFiles(string absoluteDir, bool recursive)
        {
            if (string.IsNullOrEmpty(absoluteDir)) yield break;
            var seen = new HashSet<string>(Comparer);

            if (Directory.Exists(absoluteDir))
            {
                var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(absoluteDir, "*.ra", opt))
                {
                    string full = Path.GetFullPath(f);
                    if (seen.Add(full)) yield return full;
                }
            }

            if (!_entries.IsEmpty)
            {
                string prefix = EnsureTrailingSeparator(absoluteDir);
                foreach (var key in _entries.Keys)
                {
                    if (!key.StartsWith(prefix, PathComparison)) continue;
                    if (!key.EndsWith(".ra", PathComparison)) continue;
                    if (!recursive)
                    {
                        string rest = key.Substring(prefix.Length);
                        if (rest.IndexOf('/') >= 0 || rest.IndexOf('\\') >= 0) continue;
                    }
                    if (seen.Add(key)) yield return key;
                }
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (path.Length == 0) return path;
            char last = path[path.Length - 1];
            if (last == '/' || last == '\\') return path;
            return path + Path.DirectorySeparatorChar;
        }
    }
}

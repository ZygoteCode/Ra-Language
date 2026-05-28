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
    }
}

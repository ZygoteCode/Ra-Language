using System.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RaLanguage.Interpreter.Runtime.Interop
{
    public static class NativeLibraryResolver
    {
        private static readonly ConcurrentDictionary<string, IntPtr> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<IntPtr, string> _handleToCacheKey = new();
        private static readonly object _lock = new();

        public static IntPtr Load(string libraryName, IReadOnlyList<string>? searchPaths, out string? resolvedName, out string? error)
        {
            resolvedName = null;
            error = null;

            if (string.IsNullOrWhiteSpace(libraryName))
            {
                error = "Library name is empty";
                return IntPtr.Zero;
            }

            libraryName = ExpandPlaceholders(libraryName);
            var expandedSearch = ExpandSearchPaths(searchPaths);
            var cacheKey = libraryName + "|" + string.Join(":", expandedSearch ?? Array.Empty<string>());

            if (_cache.TryGetValue(cacheKey, out var cached) && cached != IntPtr.Zero)
            {
                resolvedName = libraryName;
                return cached;
            }

            lock (_lock)
            {
                if (_cache.TryGetValue(cacheKey, out cached) && cached != IntPtr.Zero)
                {
                    resolvedName = libraryName;
                    return cached;
                }

                var attempts = BuildCandidateNames(libraryName);
                var errors = new List<string>();

                foreach (var candidate in attempts)
                {
                    if (expandedSearch != null && expandedSearch.Count > 0)
                    {
                        foreach (var dir in expandedSearch)
                        {
                            if (string.IsNullOrEmpty(dir)) continue;
                            var full = Path.Combine(dir, candidate);
                            if (TryLoad(full, out var h))
                            {
                                resolvedName = full;
                                _cache[cacheKey] = h;
                                _handleToCacheKey[h] = cacheKey;
                                return h;
                            }
                        }
                    }

                    if (TryLoad(candidate, out var handle))
                    {
                        resolvedName = candidate;
                        _cache[cacheKey] = handle;
                        _handleToCacheKey[handle] = cacheKey;
                        return handle;
                    }
                    errors.Add(candidate);
                }

                error = $"Failed to load native library '{libraryName}'. Tried: {string.Join(", ", errors)}";
                return IntPtr.Zero;
            }
        }

        public static bool TryGetExport(IntPtr libraryHandle, string symbolName, bool exactSpelling, out IntPtr address)
        {
            if (libraryHandle == IntPtr.Zero)
            {
                address = IntPtr.Zero;
                return false;
            }

            if (NativeLibrary.TryGetExport(libraryHandle, symbolName, out address)) return true;

            // macOS / dyld historically prefixes C symbols with '_'. Some platforms
            // expose stripped names; many do not. Try '_'-prefixed variant on macOS
            // when the user-given symbol fails. Harmless on other OSes (just one extra lookup).
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (!symbolName.StartsWith("_") &&
                    NativeLibrary.TryGetExport(libraryHandle, "_" + symbolName, out address))
                    return true;
                if (symbolName.StartsWith("_") &&
                    NativeLibrary.TryGetExport(libraryHandle, symbolName.Substring(1), out address))
                    return true;
            }

            if (!exactSpelling)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    foreach (var suffix in new[] { "W", "A", "Ex" })
                    {
                        if (NativeLibrary.TryGetExport(libraryHandle, symbolName + suffix, out address)) return true;
                    }
                }
            }

            address = IntPtr.Zero;
            return false;
        }

        private static bool TryLoad(string path, out IntPtr handle)
        {
            try
            {
                handle = NativeLibrary.Load(path);
                return handle != IntPtr.Zero;
            }
            catch
            {
                handle = IntPtr.Zero;
                return false;
            }
        }

        public static IEnumerable<string> BuildCandidateNames(string name)
        {
            yield return name;

            bool hasSep = name.Contains('/') || name.Contains('\\');
            bool hasExt = HasKnownExtension(name);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!hasExt) yield return name + ".dll";
                if (!hasSep)
                {
                    yield return name.StartsWith("lib", StringComparison.OrdinalIgnoreCase) ? name : "lib" + name + ".dll";
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (!hasExt) yield return name + ".dylib";
                if (!hasSep)
                {
                    yield return name.StartsWith("lib", StringComparison.OrdinalIgnoreCase) ? name : "lib" + name + ".dylib";
                }
            }
            else
            {
                if (!hasExt) yield return name + ".so";
                if (!hasSep)
                {
                    var prefix = name.StartsWith("lib", StringComparison.Ordinal) ? name : "lib" + name;
                    yield return prefix + ".so";
                    yield return prefix + ".so.6";
                    yield return prefix + ".so.5";
                    yield return prefix + ".so.1";
                }
            }
        }

        private static bool HasKnownExtension(string name)
        {
            string lower = name.ToLowerInvariant();
            return lower.EndsWith(".dll") || lower.EndsWith(".so") || lower.EndsWith(".dylib")
                   || lower.Contains(".so.");
        }

        /// <summary>
        /// Cross-platform path placeholders, modelled on dyld/ld.so conventions.
        ///   @executable_path  → directory of the current process executable
        ///   @loader_path      → directory of the current interpreter assembly
        ///   $ORIGIN           → same as @loader_path (ld.so convention)
        ///   %cwd%             → current working directory
        ///   %tmp%             → system temp directory
        /// </summary>
        public static string ExpandPlaceholders(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            string result = input;

            string? exePath = null;
            try { exePath = Environment.ProcessPath; } catch { }
            string exeDir = string.IsNullOrEmpty(exePath) ? AppContext.BaseDirectory : Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            string loaderDir = AppContext.BaseDirectory;

            result = result.Replace("@executable_path", exeDir, StringComparison.Ordinal);
            result = result.Replace("@loader_path", loaderDir, StringComparison.Ordinal);
            result = result.Replace("$ORIGIN", loaderDir, StringComparison.Ordinal);
            result = result.Replace("%cwd%", Environment.CurrentDirectory, StringComparison.Ordinal);
            result = result.Replace("%tmp%", Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
            return result;
        }

        public static IReadOnlyList<string>? ExpandSearchPaths(IReadOnlyList<string>? paths)
        {
            if (paths == null || paths.Count == 0) return paths;
            var result = new List<string>(paths.Count);
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                result.Add(ExpandPlaceholders(p));
            }
            return result;
        }

        public static void ClearCache()
        {
            lock (_lock)
            {
                _cache.Clear();
                _handleToCacheKey.Clear();
            }
        }

        /// <summary>
        /// Evict a single library from the cache by name. The OS handle is freed via NativeLibrary.Free,
        /// allowing the next Load() call to pick up a recompiled/refreshed .dll/.so/.dylib.
        /// Returns true if at least one cache entry was evicted.
        /// </summary>
        public static bool Reload(string libraryName)
        {
            if (string.IsNullOrWhiteSpace(libraryName)) return false;
            libraryName = ExpandPlaceholders(libraryName);

            bool evicted = false;
            lock (_lock)
            {
                var toRemove = _cache.Where(kv => kv.Key.StartsWith(libraryName + "|", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var kv in toRemove)
                {
                    try { NativeLibrary.Free(kv.Value); } catch { }
                    _cache.TryRemove(kv.Key, out _);
                    _handleToCacheKey.TryRemove(kv.Value, out _);
                    evicted = true;
                }
            }
            return evicted;
        }

        public static IReadOnlyDictionary<string, IntPtr> Snapshot()
        {
            return _cache;
        }
    }
}

using System.IO;

namespace RaLanguage.Interpreter.Modules
{
    public sealed class ModuleResolutionResult
    {
        public string? AbsolutePath { get; }
        public string? ErrorMessage { get; }
        public bool Ok => AbsolutePath != null;

        private ModuleResolutionResult(string? absolutePath, string? errorMessage)
        {
            AbsolutePath = absolutePath;
            ErrorMessage = errorMessage;
        }

        public static ModuleResolutionResult Success(string absolutePath)
            => new ModuleResolutionResult(absolutePath, null);

        public static ModuleResolutionResult Failure(string message)
            => new ModuleResolutionResult(null, message);
    }

    public sealed class ModuleResolver
    {
        private const string RaExtension = ".ra";
        private const string StdRootSegment = "std";

        public string ProjectRoot { get; }
        public string StdRoot { get; }

        public ModuleResolver(string projectRoot, string stdRoot)
        {
            ProjectRoot = Path.GetFullPath(projectRoot);
            StdRoot = Path.GetFullPath(stdRoot);
        }

        public ModuleResolutionResult Resolve(ModuleSpecifier spec, string currentFile)
        {
            return spec.Kind switch
            {
                ModuleSpecifierKind.StringLiteral => ResolveStringLiteral(spec.RawPath!, currentFile),
                ModuleSpecifierKind.Dotted => ResolveDotted(spec.Segments!),
                _ => ModuleResolutionResult.Failure($"Unsupported module specifier kind: {spec.Kind}")
            };
        }

        private ModuleResolutionResult ResolveStringLiteral(string rawPath, string currentFile)
        {
            string normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar)
                                       .Replace('/', Path.DirectorySeparatorChar);

            string withExt = HasRaExtension(normalized) ? normalized : normalized + RaExtension;

            string absolute;
            if (Path.IsPathRooted(withExt))
            {
                absolute = Path.GetFullPath(withExt);
            }
            else
            {
                string anchorDir = ResolveAnchorDirectory(currentFile);
                absolute = Path.GetFullPath(Path.Combine(anchorDir, withExt));
            }

            if (!File.Exists(absolute))
            {
                if (!Path.IsPathRooted(withExt))
                {
                    string projectFallback = Path.GetFullPath(Path.Combine(ProjectRoot, withExt));
                    if (File.Exists(projectFallback))
                        return ModuleResolutionResult.Success(projectFallback);
                }

                return ModuleResolutionResult.Failure(
                    $"Module file not found: '{rawPath}' (resolved to '{absolute}')");
            }

            return ModuleResolutionResult.Success(absolute);
        }

        private ModuleResolutionResult ResolveDotted(IReadOnlyList<string> segments)
        {
            if (segments.Count == 0)
                return ModuleResolutionResult.Failure("Empty module path");

            if (!string.Equals(segments[0], StdRootSegment, System.StringComparison.Ordinal))
            {
                return ModuleResolutionResult.Failure(
                    $"Unknown module root '{segments[0]}'. Only '{StdRootSegment}' is supported as a dotted root.");
            }

            if (segments.Count == 1)
            {
                return ModuleResolutionResult.Failure(
                    "'std' must be followed by at least one module segment (e.g. 'std.io').");
            }

            for (int i = 1; i < segments.Count; i++)
            {
                if (!IsValidSegment(segments[i]))
                {
                    return ModuleResolutionResult.Failure(
                        $"Invalid module path segment: '{segments[i]}'");
                }
            }

            var subSegments = new string[segments.Count - 1];
            for (int i = 1; i < segments.Count; i++) subSegments[i - 1] = segments[i];
            string relative = string.Join(Path.DirectorySeparatorChar.ToString(), subSegments) + RaExtension;
            string absolute = Path.GetFullPath(Path.Combine(StdRoot, relative));

            if (!File.Exists(absolute))
            {
                return ModuleResolutionResult.Failure(
                    $"Standard library module not found: '{string.Join('.', segments)}' (looked at '{absolute}')");
            }

            return ModuleResolutionResult.Success(absolute);
        }

        private string ResolveAnchorDirectory(string currentFile)
        {
            if (string.IsNullOrEmpty(currentFile))
                return ProjectRoot;

            try
            {
                string fullCurrent = Path.IsPathRooted(currentFile)
                    ? currentFile
                    : Path.GetFullPath(Path.Combine(ProjectRoot, currentFile));

                if (File.Exists(fullCurrent))
                {
                    return Path.GetDirectoryName(fullCurrent) ?? ProjectRoot;
                }
            }
            catch
            {
            }

            return ProjectRoot;
        }

        private static bool HasRaExtension(string path)
        {
            return path.EndsWith(RaExtension, System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsValidSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            foreach (char c in segment)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                    return false;
            }
            return true;
        }
    }
}

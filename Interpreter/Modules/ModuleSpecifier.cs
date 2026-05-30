namespace RaLanguage.Interpreter.Modules
{
    public enum ModuleSpecifierKind
    {
        StringLiteral,
        Dotted
    }

    public sealed class ModuleSpecifier
    {
        public ModuleSpecifierKind Kind { get; }
        public string? RawPath { get; }
        public IReadOnlyList<string>? Segments { get; }
        public string Display { get; }

        // Trailing-`.*` glob on a dotted path (e.g. `import std.prelude.*`).
        // Selects every symbol under the addressed module/package rather than
        // a single module. Only ever set on Dotted specifiers.
        public bool IsWildcard { get; }

        private ModuleSpecifier(ModuleSpecifierKind kind, string? rawPath, IReadOnlyList<string>? segments, string display, bool isWildcard)
        {
            Kind = kind;
            RawPath = rawPath;
            Segments = segments;
            Display = display;
            IsWildcard = isWildcard;
        }

        public static ModuleSpecifier FromStringLiteral(string rawPath)
        {
            return new ModuleSpecifier(
                ModuleSpecifierKind.StringLiteral,
                rawPath,
                null,
                $"\"{rawPath}\"",
                isWildcard: false);
        }

        public static ModuleSpecifier FromDotted(IReadOnlyList<string> segments, bool isWildcard = false)
        {
            return new ModuleSpecifier(
                ModuleSpecifierKind.Dotted,
                null,
                segments,
                isWildcard ? string.Join(".", segments) + ".*" : string.Join(".", segments),
                isWildcard);
        }
    }
}

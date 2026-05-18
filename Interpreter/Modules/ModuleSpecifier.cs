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

        private ModuleSpecifier(ModuleSpecifierKind kind, string? rawPath, IReadOnlyList<string>? segments, string display)
        {
            Kind = kind;
            RawPath = rawPath;
            Segments = segments;
            Display = display;
        }

        public static ModuleSpecifier FromStringLiteral(string rawPath)
        {
            return new ModuleSpecifier(
                ModuleSpecifierKind.StringLiteral,
                rawPath,
                null,
                $"\"{rawPath}\"");
        }

        public static ModuleSpecifier FromDotted(IReadOnlyList<string> segments)
        {
            return new ModuleSpecifier(
                ModuleSpecifierKind.Dotted,
                null,
                segments,
                string.Join(".", segments));
        }
    }
}

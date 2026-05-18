using RaLanguage.Interpreter.Values.Namespaces;

namespace RaLanguage.Interpreter.Runtime.Namespaces
{
    public sealed class NamespaceRegistry
    {
        public static readonly NamespaceRegistry Global = new();

        private NamespaceValue _root;

        public NamespaceRegistry()
        {
            _root = new NamespaceValue("", null);
        }

        public NamespaceValue Root => _root;

        public void Clear()
        {
            _root = new NamespaceValue("", null);
        }

        public NamespaceLookupResult GetOrCreate(IReadOnlyList<string> segments)
        {
            if (segments == null || segments.Count == 0)
                return NamespaceLookupResult.Fail("Namespace path is empty");

            var current = _root;
            for (int i = 0; i < segments.Count; i++)
            {
                string seg = segments[i];
                if (string.IsNullOrEmpty(seg))
                    return NamespaceLookupResult.Fail("Namespace path contains an empty segment");

                var existingEntry = current.Members.GetLocalEntry(seg);
                if (existingEntry == null)
                {
                    current = current.GetOrCreateChild(seg);
                    continue;
                }

                if (existingEntry.Value is NamespaceValue ns)
                {
                    current = ns;
                    continue;
                }

                string qual = string.Join(".", segments.Take(i + 1));
                return NamespaceLookupResult.Fail(
                    $"Cannot open namespace '{qual}': name conflicts with an existing non-namespace symbol");
            }

            return NamespaceLookupResult.Ok(current);
        }

        public NamespaceValue? Resolve(IReadOnlyList<string> segments)
        {
            if (segments == null || segments.Count == 0) return null;

            var current = _root;
            for (int i = 0; i < segments.Count; i++)
            {
                var child = current.GetChildNamespace(segments[i]);
                if (child == null) return null;
                current = child;
            }
            return current;
        }
    }

    public readonly struct NamespaceLookupResult
    {
        public NamespaceValue? Namespace { get; }
        public string? ErrorMessage { get; }
        public bool IsOk => Namespace != null && ErrorMessage == null;

        private NamespaceLookupResult(NamespaceValue? ns, string? error)
        {
            Namespace = ns;
            ErrorMessage = error;
        }

        public static NamespaceLookupResult Ok(NamespaceValue ns) => new(ns, null);
        public static NamespaceLookupResult Fail(string message) => new(null, message);
    }
}

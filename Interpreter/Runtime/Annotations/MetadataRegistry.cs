using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Interpreter.Values.Annotations;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public sealed class MetadataRegistry
    {
        private readonly Dictionary<string, List<AnnotationInstanceValue>> _byTarget = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, MetadataTarget> _targetByKey = new(System.StringComparer.Ordinal);

        private static readonly MetadataRegistry _global = new();
        public static MetadataRegistry Global => _global;

        public void Register(MetadataTarget target, AnnotationInstanceValue instance)
        {
            if (!_byTarget.TryGetValue(target.Key, out var list))
            {
                list = new List<AnnotationInstanceValue>();
                _byTarget[target.Key] = list;
            }
            _targetByKey[target.Key] = target;
            list.Add(instance);
        }

        public IReadOnlyList<AnnotationInstanceValue> Get(MetadataTarget target)
            => _byTarget.TryGetValue(target.Key, out var list)
                ? list
                : System.Array.Empty<AnnotationInstanceValue>();

        public IReadOnlyList<AnnotationInstanceValue> GetByKey(string key)
            => _byTarget.TryGetValue(key, out var list)
                ? list
                : System.Array.Empty<AnnotationInstanceValue>();

        public bool HasAnnotation(string targetKey, string annotationName)
        {
            if (!_byTarget.TryGetValue(targetKey, out var list)) return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i].DefinitionName == annotationName) return true;
            return false;
        }

        public List<AnnotationInstanceValue> GetEffective(string key, System.Func<string, string?> resolveParentKey)
        {
            var result = new List<AnnotationInstanceValue>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            string? current = key;
            bool isOwn = true;
            var visitedKeys = new HashSet<string>(System.StringComparer.Ordinal);
            while (current != null && visitedKeys.Add(current))
            {
                if (_byTarget.TryGetValue(current, out var list))
                {
                    foreach (var a in list)
                    {
                        if (!isOwn && !a.Definition.IsInherited) continue;
                        if (!a.Definition.IsRepeatable && seen.Contains(a.DefinitionName)) continue;
                        result.Add(a);
                        if (!a.Definition.IsRepeatable) seen.Add(a.DefinitionName);
                    }
                }
                current = resolveParentKey(current);
                isOwn = false;
            }
            return result;
        }

        public bool HasAnnotationEffective(string key, string annotationName, System.Func<string, string?> resolveParentKey)
        {
            string? current = key;
            bool isOwn = true;
            var visitedKeys = new HashSet<string>(System.StringComparer.Ordinal);
            while (current != null && visitedKeys.Add(current))
            {
                if (_byTarget.TryGetValue(current, out var list))
                {
                    foreach (var a in list)
                    {
                        if (!isOwn && !a.Definition.IsInherited) continue;
                        if (a.DefinitionName == annotationName) return true;
                    }
                }
                current = resolveParentKey(current);
                isOwn = false;
            }
            return false;
        }

        public AnnotationInstanceValue? FindEffective(string key, string annotationName, System.Func<string, string?> resolveParentKey)
        {
            string? current = key;
            bool isOwn = true;
            var visitedKeys = new HashSet<string>(System.StringComparer.Ordinal);
            while (current != null && visitedKeys.Add(current))
            {
                if (_byTarget.TryGetValue(current, out var list))
                {
                    foreach (var a in list)
                    {
                        if (!isOwn && !a.Definition.IsInherited) continue;
                        if (a.DefinitionName == annotationName) return a;
                    }
                }
                current = resolveParentKey(current);
                isOwn = false;
            }
            return null;
        }

        public IEnumerable<AnnotationInstanceValue> GetWithMeta(string targetKey, string metaAnnotationName)
        {
            if (!_byTarget.TryGetValue(targetKey, out var list)) yield break;
            foreach (var a in list)
            {
                if (a.HasMeta(metaAnnotationName)) yield return a;
            }
        }

        public void Clear()
        {
            _byTarget.Clear();
            _targetByKey.Clear();
        }

        public IEnumerable<string> Keys => _byTarget.Keys;
    }
}

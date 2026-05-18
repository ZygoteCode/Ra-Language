using System.Collections.Generic;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Values.Annotations
{
    public sealed class AnnotationInstanceValue : RuntimeValue
    {
        public AnnotationTypeValue Definition { get; }
        public string DefinitionName => Definition.AnnotationName;
        public IReadOnlyList<RuntimeValue> PositionalArgs { get; }
        public IReadOnlyDictionary<string, RuntimeValue> NamedArgs { get; }
        public Position ApplicationStart { get; }
        public Position ApplicationEnd { get; }
        public MetadataTarget Target { get; set; }
        public int Priority { get; set; }

        public override RuntimeValueType Type => RuntimeValueType.AnnotationInstance;
        public override bool IsCopy => false;

        public AnnotationInstanceValue(
            AnnotationTypeValue definition,
            IReadOnlyList<RuntimeValue> positional,
            IReadOnlyDictionary<string, RuntimeValue> named,
            Position appStart,
            Position appEnd)
        {
            Definition = definition;
            PositionalArgs = positional;
            NamedArgs = named;
            ApplicationStart = appStart;
            ApplicationEnd = appEnd;
            Priority = definition.Priority;
        }

        public RuntimeValue? Get(string parameterName)
        {
            if (NamedArgs.TryGetValue(parameterName, out var v)) return v;
            var idx = -1;
            for (int i = 0; i < Definition.Parameters.Count; i++)
            {
                if (Definition.Parameters[i].Name == parameterName) { idx = i; break; }
            }
            if (idx >= 0 && idx < PositionalArgs.Count) return PositionalArgs[idx];
            return null;
        }

        public bool HasMeta(string metaAnnotationName)
        {
            for (int i = 0; i < Definition.MetaAnnotations.Count; i++)
            {
                if (Definition.MetaAnnotations[i].DefinitionName == metaAnnotationName) return true;
            }
            return false;
        }

        public override RuntimeValue Copy() => this;

        public override string ToString()
        {
            if (PositionalArgs.Count == 0 && NamedArgs.Count == 0) return $"@{DefinitionName}";
            var parts = new List<string>();
            foreach (var p in PositionalArgs) parts.Add(p.ToString());
            foreach (var kv in NamedArgs) parts.Add($"{kv.Key}={kv.Value}");
            return $"@{DefinitionName}({string.Join(", ", parts)})";
        }
    }
}

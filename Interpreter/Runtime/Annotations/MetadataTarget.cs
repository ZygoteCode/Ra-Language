using System;
using System.Collections.Generic;

namespace RaLanguage.Interpreter.Runtime.Annotations
{
    public readonly struct MetadataTarget : IEquatable<MetadataTarget>
    {
        public AnnotationTargetKind Kind { get; }
        public string Key { get; }
        public string? Owner { get; }
        public string Name { get; }

        public MetadataTarget(AnnotationTargetKind kind, string? owner, string name)
        {
            Kind = kind;
            Owner = owner;
            Name = name;
            Key = BuildKey(kind, owner, name);
        }

        public static string BuildKey(AnnotationTargetKind kind, string? owner, string name)
        {
            string prefix = kind switch
            {
                AnnotationTargetKind.Class => "class",
                AnnotationTargetKind.Struct => "struct",
                AnnotationTargetKind.Interface => "interface",
                AnnotationTargetKind.Trait => "trait",
                AnnotationTargetKind.Enum => "enum",
                AnnotationTargetKind.EnumMember => "enum_member",
                AnnotationTargetKind.Function => "fn",
                AnnotationTargetKind.Method => "method",
                AnnotationTargetKind.Constructor => "ctor",
                AnnotationTargetKind.Operator => "op",
                AnnotationTargetKind.Field => "field",
                AnnotationTargetKind.StaticField => "static_field",
                AnnotationTargetKind.Parameter => "param",
                AnnotationTargetKind.Variable => "var",
                AnnotationTargetKind.Annotation => "annotation",
                AnnotationTargetKind.Return => "return",
                _ => "unknown"
            };
            return owner != null ? $"{prefix}:{owner}.{name}" : $"{prefix}:{name}";
        }

        public bool Equals(MetadataTarget other) => Key == other.Key;
        public override bool Equals(object? obj) => obj is MetadataTarget mt && Equals(mt);
        public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
        public override string ToString() => Key;

        public static IEnumerable<string> AllKindNames()
        {
            yield return "class";
            yield return "struct";
            yield return "interface";
            yield return "trait";
            yield return "enum";
            yield return "enum_member";
            yield return "fn";
            yield return "method";
            yield return "constructor";
            yield return "operator";
            yield return "field";
            yield return "static_field";
            yield return "parameter";
            yield return "variable";
            yield return "annotation";
            yield return "return";
        }

        public static AnnotationTargetKind? FromName(string name) => name switch
        {
            "class" => AnnotationTargetKind.Class,
            "struct" => AnnotationTargetKind.Struct,
            "interface" => AnnotationTargetKind.Interface,
            "trait" => AnnotationTargetKind.Trait,
            "enum" => AnnotationTargetKind.Enum,
            "enum_member" => AnnotationTargetKind.EnumMember,
            "fn" => AnnotationTargetKind.Function,
            "function" => AnnotationTargetKind.Function,
            "method" => AnnotationTargetKind.Method,
            "constructor" => AnnotationTargetKind.Constructor,
            "ctor" => AnnotationTargetKind.Constructor,
            "operator" => AnnotationTargetKind.Operator,
            "op" => AnnotationTargetKind.Operator,
            "field" => AnnotationTargetKind.Field,
            "static_field" => AnnotationTargetKind.StaticField,
            "parameter" => AnnotationTargetKind.Parameter,
            "param" => AnnotationTargetKind.Parameter,
            "variable" => AnnotationTargetKind.Variable,
            "var" => AnnotationTargetKind.Variable,
            "annotation" => AnnotationTargetKind.Annotation,
            "return" => AnnotationTargetKind.Return,
            _ => null
        };
    }
}

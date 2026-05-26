using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Runtime.Properties
{
    // Runtime-side image of a PropertyDefinitionNode. Built once per
    // declaring type at definition time (in the
    // Class/Struct/Record/Interface/Trait visitor) and consulted on
    // every member-access dispatch.
    //
    // The kind is derived from the accessor set at build time and never
    // changes:
    //   - HasBacking == true  : the type's hidden class shape allocates
    //                           a slot for this property name. Storage
    //                           is in ClassInstance.FieldSlots /
    //                           StructInstance.FieldSlots, keyed by Name
    //                           (matching field shape allocation).
    //   - HasBacking == false : pure computed, no storage.
    //
    // IsLazy implies HasBacking. The corresponding slot is initialised
    // by the DefaultValueNode the *first* time the getter runs, not at
    // construction time.
    //
    // Abstract properties are flagged but allocate no slot.
    public sealed class PropertyDescriptor
    {
        public string Name { get; }
        public TypeDescriptor? PropertyType { get; }
        public bool IsPublic { get; }
        public bool IsStatic { get; }
        public bool IsAbstract { get; }
        public bool IsOverride { get; }
        public bool IsLazy { get; }
        public AstNode? DefaultValueNode { get; }

        public PropertyAccessorRuntime? Getter { get; }
        public PropertyAccessorRuntime? Setter { get; }
        public PropertyAccessorRuntime? Initter { get; }
        public PropertyAccessorRuntime? Observer { get; }

        // Owning type name; used to build metadata keys (`prop:Type.name`).
        public string DeclaringTypeName { get; }

        // The original AST node — kept so future tooling can re-derive
        // anything from it without round-tripping through the descriptor.
        public PropertyDefinitionNode SourceNode { get; }

        public bool HasBacking { get; }
        public bool IsComputed => Getter != null && Getter.Body != null && !HasBacking;
        public bool HasGetter => Getter != null;
        public bool HasSetter => Setter != null;
        public bool HasInitter => Initter != null;
        public bool HasObserver => Observer != null;

        public PropertyDescriptor(
            PropertyDefinitionNode source,
            string declaringTypeName,
            PropertyAccessorRuntime? getter,
            PropertyAccessorRuntime? setter,
            PropertyAccessorRuntime? initter,
            PropertyAccessorRuntime? observer,
            bool hasBacking)
        {
            SourceNode = source;
            Name = source.NameTok.Value?.ToString() ?? "";
            PropertyType = source.PropertyType;
            IsPublic = source.IsPublic;
            IsStatic = source.IsStatic;
            IsAbstract = source.IsAbstract;
            IsOverride = source.IsOverride;
            IsLazy = source.IsLazy;
            DefaultValueNode = source.DefaultValueNode;
            DeclaringTypeName = declaringTypeName;
            Getter = getter;
            Setter = setter;
            Initter = initter;
            Observer = observer;
            HasBacking = hasBacking;
        }

        // Resolves the effective visibility of an accessor: explicit
        // per-accessor `pub`/`priv` first; falls back to the property's
        // overall `IsPublic`.
        public bool IsAccessorPublic(PropertyAccessorRuntime accessor)
        {
            return accessor.Visibility switch
            {
                PropertyAccessorVisibility.Public => true,
                PropertyAccessorVisibility.Private => false,
                _ => IsPublic
            };
        }
    }

    public sealed class PropertyAccessorRuntime
    {
        public PropertyAccessorKind Kind { get; }
        public PropertyAccessorVisibility Visibility { get; }
        public AstNode? Body { get; }                    // null when IsAuto
        public bool IsAuto => Body == null;

        public PropertyAccessorRuntime(PropertyAccessorNode source)
        {
            Kind = source.Kind;
            Visibility = source.Visibility;
            Body = source.BodyNode;
        }
    }
}

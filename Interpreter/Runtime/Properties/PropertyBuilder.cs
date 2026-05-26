using RaLanguage.Parser.Nodes.Properties;

namespace RaLanguage.Interpreter.Runtime.Properties
{
    // Builds a PropertyDescriptor from a PropertyDefinitionNode. Pure
    // function — single point where the descriptor's classification
    // logic lives so the various type visitors (class/struct/record/
    // interface/trait) can call it uniformly.
    public static class PropertyBuilder
    {
        public static PropertyDescriptor Build(PropertyDefinitionNode source, string declaringTypeName)
        {
            PropertyAccessorRuntime? getter = null;
            PropertyAccessorRuntime? setter = null;
            PropertyAccessorRuntime? initter = null;
            PropertyAccessorRuntime? observer = null;

            foreach (var acc in source.Accessors)
            {
                var runtime = new PropertyAccessorRuntime(acc);
                switch (acc.Kind)
                {
                    case PropertyAccessorKind.Get:     getter   = runtime; break;
                    case PropertyAccessorKind.Set:     setter   = runtime; break;
                    case PropertyAccessorKind.Init:    initter  = runtime; break;
                    case PropertyAccessorKind.Observe: observer = runtime; break;
                }
            }

            // Backing-slot allocation rule:
            //
            //   - abstract  → no backing (the override decides)
            //   - lazy      → backing (slot stores the memoised value)
            //   - auto get/set/init or default value present → backing
            //   - otherwise (custom-bodied get/set with no auto) → no backing
            //
            // The user can mix a custom setter with an auto getter to
            // request both a backing slot AND custom validation logic
            // — that is the common pattern and the heuristic accepts
            // it: any auto-shaped accessor counts as backing-requesting.
            bool hasBacking = false;
            if (!source.IsAbstract)
            {
                if (source.IsLazy)
                {
                    hasBacking = true;
                }
                else if (source.DefaultValueNode != null)
                {
                    hasBacking = true;
                }
                else if ((getter != null && getter.IsAuto)
                      || (setter != null && setter.IsAuto)
                      || (initter != null && initter.IsAuto))
                {
                    hasBacking = true;
                }
                else if (observer != null)
                {
                    // Observer without other auto signals — still
                    // implies backing because the observer reads the
                    // pre-/post-set value.
                    hasBacking = true;
                }
            }

            return new PropertyDescriptor(
                source,
                declaringTypeName,
                getter, setter, initter, observer,
                hasBacking);
        }
    }
}

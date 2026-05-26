using RaLanguage.Parser.Nodes.Events;

namespace RaLanguage.Interpreter.Runtime.Events
{
    // Builds an EventDescriptor from an EventDefinitionNode. Single
    // source of truth for the subscribe/raise visibility resolution
    // (the accessor block can override the two defaults independently).
    public static class EventBuilder
    {
        public static EventDescriptor Build(EventDefinitionNode source, string declaringTypeName)
        {
            // Defaults: subscribe follows the overall property visibility;
            // raise is private even when the event is `pub`. C# convention.
            bool subscribeIsPublic = source.IsPublic;
            bool raiseIsPublic = false;

            foreach (var acc in source.Accessors)
            {
                bool pub = acc.Visibility == EventAccessorVisibility.Public;
                switch (acc.Kind)
                {
                    case EventAccessorKind.Subscribe:
                        subscribeIsPublic = pub;
                        break;
                    case EventAccessorKind.Raise:
                        raiseIsPublic = pub;
                        break;
                }
            }

            return new EventDescriptor(source, declaringTypeName, subscribeIsPublic, raiseIsPublic);
        }
    }
}

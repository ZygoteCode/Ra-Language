using RaLanguage.Parser.Nodes.Events;

namespace RaLanguage.Interpreter.Runtime.Events
{
    // Runtime-side image of an EventDefinitionNode. Built once per
    // declaring type at definition time and consulted on every event
    // member access and emission.
    //
    // Two visibility axes:
    //   - SubscribeIsPublic: who may call obj.E.on(...) / off / clear / count
    //   - RaiseIsPublic:     who may call obj.E(args)
    //
    // Default for subscribe = property's overall IsPublic.
    // Default for raise     = private (regardless of overall) — matches
    // the C# `event` convention where only the declaring type can fire.
    // A `pub raise;` accessor inside the event body overrides that.
    public sealed class EventDescriptor
    {
        public string Name { get; }
        public List<EventPayloadParam> Parameters { get; }
        public bool IsPublic { get; }
        public bool IsStatic { get; }
        public bool IsAbstract { get; }
        public bool IsOverride { get; }
        public bool IsCancellable { get; }
        public bool IsTolerant { get; }
        public bool IsAsync { get; }
        public bool SubscribeIsPublic { get; }
        public bool RaiseIsPublic { get; }
        public string DeclaringTypeName { get; }
        public EventDefinitionNode SourceNode { get; }

        public EventDescriptor(
            EventDefinitionNode source,
            string declaringTypeName,
            bool subscribeIsPublic,
            bool raiseIsPublic)
        {
            SourceNode = source;
            Name = source.NameTok.Value?.ToString() ?? "";
            Parameters = source.PayloadParams;
            IsPublic = source.IsPublic;
            IsStatic = source.IsStatic;
            IsAbstract = source.IsAbstract;
            IsOverride = source.IsOverride;
            IsCancellable = source.IsCancellable;
            IsTolerant = source.IsTolerant;
            IsAsync = source.IsAsync;
            SubscribeIsPublic = subscribeIsPublic;
            RaiseIsPublic = raiseIsPublic;
            DeclaringTypeName = declaringTypeName;
        }

        public int Arity => Parameters.Count;

        // Pairwise structural signature comparison: name, arity,
        // cancellable flag, AND each payload parameter type must
        // match by TypeDescriptor.Name. Used by override / interface
        // / trait conformance checks. A null PayloadParam.Type
        // matches any other null (untyped payload positions are
        // permissive — they unify). Strict equality is required when
        // both sides are typed.
        public bool SignatureMatches(EventDescriptor other)
        {
            if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) return false;
            if (Arity != other.Arity) return false;
            if (IsCancellable != other.IsCancellable) return false;
            for (int i = 0; i < Parameters.Count; i++)
            {
                var a = Parameters[i].Type;
                var b = other.Parameters[i].Type;
                if (a == null && b == null) continue;
                if (a == null || b == null) return false;
                if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }
}

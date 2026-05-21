namespace RaLanguage.Interpreter.Pipeline
{
    // One entry of the static capture set the Resolver attaches to every
    // FunctionDefinitionNode whose body references an outer binding. The closure
    // builder uses this list to materialise the closure environment at definition
    // time without inspecting the implicit lexical chain at every call.
    //
    //   Name      — outer binding name as it appears in source. Useful for
    //               diagnostics and tooling (LSP "show captures").
    //   SourceId  — BindingId of the binding in the enclosing frame that owns
    //               the storage. The closure indexes the upvalue table by the
    //               position of this entry in ResolvedCaptures, NOT by SourceId
    //               — keep the two distinct so the upvalue slots stay dense
    //               while the source frame can be reused for unrelated names.
    //   IsExplicit — `true` when the function carries an explicit [capture]
    //               clause (CaptureSpec) and this entry mirrors one of those
    //               specs. `false` when the resolver inferred the capture from
    //               a free-variable reference in the body.
    public readonly struct ResolvedCapture
    {
        public readonly string Name;
        public readonly BindingId SourceId;
        public readonly bool IsExplicit;

        public ResolvedCapture(string name, BindingId sourceId, bool isExplicit)
        {
            Name = name;
            SourceId = sourceId;
            IsExplicit = isExplicit;
        }
    }
}

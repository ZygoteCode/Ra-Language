namespace RaLanguage.Interpreter.IR
{
    // Describes one slot in a closure's capture array. Mirrors the resolver's
    // ResolvedCapture but in VM-native form: an upvalue is either copied from
    // the parent frame's locals (IsLocal=true) or aliased from the parent
    // closure's own upvalues (IsLocal=false). See RA_VM_MIGRATION.md §3.9.
    public readonly struct UpvalueSpec
    {
        public readonly bool IsLocal;
        public readonly ushort Index;

        public UpvalueSpec(bool isLocal, ushort index)
        {
            IsLocal = isLocal;
            Index = index;
        }
    }
}

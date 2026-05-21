namespace RaLanguage.Interpreter.Runtime
{
    // Inline-cache record for a single AST identifier lookup site (VariableAccessNode,
    // VariableAssignmentNode). One AST node = one cache slot. The cache is replaced
    // atomically by reference assignment, so reads in concurrent contexts (Spawn /
    // async) always see a consistent {Table, Generation, Entry} triple — never a
    // torn snapshot of two different resolutions.
    //
    // Validation policy (see VariableAccessNodeVisitor): a cache hit requires that
    //   * Table is the same SymbolTable instance currently in scope, AND
    //   * Table.LocalGeneration matches Generation.
    // Generation only ticks on add / remove (not on TryAssign value mutation), so
    // hot loops that only reassign existing locals keep the cache valid for the
    // entire run.
    //
    // We deliberately only memoise hits found in the LOCAL dict of the current
    // table. Parent-walk hits stay uncached: tracking shadow invalidation across
    // a parent chain would require per-ancestor generation reads, which costs as
    // much as just walking the chain. Local hits — function parameters, loop
    // bodies, branch locals — dominate the hot path, and that's where the cache
    // pays off.
    public sealed class SymbolLookupCache
    {
        public readonly SymbolTable Table;
        public readonly int Generation;
        public readonly SymbolEntry Entry;

        public SymbolLookupCache(SymbolTable table, int generation, SymbolEntry entry)
        {
            Table = table;
            Generation = generation;
            Entry = entry;
        }
    }
}

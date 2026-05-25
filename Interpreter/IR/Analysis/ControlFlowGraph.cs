using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M54: container for the basic-block decomposition of a RaFunction.
    // Blocks are stored in `Blocks[0..N)` in PC-ascending order; the
    // entry block is always at index 0 (sourced from PC=0). PC-to-block
    // lookups go through `PcToBlock[pc]` (length `Code.Length`) so the
    // dispatch-loop integration can map a runtime PC back to a block id
    // in O(1) without scanning.
    //
    // The CFG is **derived state**. It is rebuilt on demand by
    // `CfgBuilder.Build(fn)`; the runtime never consults it directly.
    // Optimisation passes (dominators, SSA, JIT codegen) operate on the
    // CFG; the VM keeps interpreting the flat opcode stream until a
    // tier-up succeeds.
    public sealed class ControlFlowGraph
    {
        public readonly RaFunction Function;
        public readonly List<BasicBlock> Blocks = new();
        // pc -> id of the block that owns this pc; -1 for unreachable
        // padding (none in current emit shape, kept for forward compat).
        public readonly int[] PcToBlock;

        public ControlFlowGraph(RaFunction fn)
        {
            Function = fn;
            PcToBlock = new int[fn.Code.Length];
            for (int i = 0; i < PcToBlock.Length; i++) PcToBlock[i] = -1;
        }

        public BasicBlock Entry => Blocks.Count > 0 ? Blocks[0] : throw new System.InvalidOperationException("empty CFG");

        // Post-order traversal — useful for iterative dataflow + dominator
        // analysis (reverse-post-order = forward dataflow order).
        public int[] PostOrder()
        {
            int n = Blocks.Count;
            var order = new List<int>(n);
            var visited = new bool[n];
            void Dfs(int id)
            {
                if (visited[id]) return;
                visited[id] = true;
                foreach (var s in Blocks[id].Successors) Dfs(s);
                order.Add(id);
            }
            Dfs(0);
            return order.ToArray();
        }

        public int[] ReversePostOrder()
        {
            var po = PostOrder();
            System.Array.Reverse(po);
            return po;
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# CFG of {Function.Name} ({Blocks.Count} blocks, {Function.Code.Length} insns)");
            foreach (var b in Blocks)
            {
                sb.Append(b.ToString());
                sb.Append("  preds=[").Append(string.Join(",", b.Predecessors)).AppendLine("]");
            }
            return sb.ToString();
        }
    }
}

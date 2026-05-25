using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.IR.Analysis
{
    // M55: dominator analysis on top of `ControlFlowGraph`.
    //
    // Implements the Cooper-Harvey-Kennedy iterative algorithm (CHK 2001
    // "A Simple, Fast Dominance Algorithm"). Empirically converges in
    // ~3-4 reverse-post-order sweeps for typical functions and avoids
    // the per-node fastest-path overhead of Lengauer-Tarjan that pays
    // off only on huge CFGs. Ra functions cap at a few hundred blocks;
    // CHK is the right complexity/code-size trade-off.
    //
    // Produces:
    //   * IDom[b] — immediate dominator of block `b` (`-1` for entry).
    //   * DominatorTree[b] — children of `b` in the dominator tree
    //     (lazy build via `BuildDominatorTree`).
    //   * DominanceFrontier[b] — the dominance frontier set, computed
    //     directly from the IDom map by the CHK paper's DF algorithm.
    //     Required for SSA phi placement (M56).
    public sealed class Dominators
    {
        public readonly ControlFlowGraph Cfg;
        public readonly int[] IDom;
        public readonly int[] PostOrderIndex; // block id -> rpo position
        public readonly int[] Rpo;            // rpo position -> block id
        private List<int>[]? _domTree;
        private HashSet<int>[]? _df;

        private Dominators(ControlFlowGraph cfg, int[] idom, int[] rpo, int[] poIdx)
        {
            Cfg = cfg;
            IDom = idom;
            Rpo = rpo;
            PostOrderIndex = poIdx;
        }

        public static Dominators Compute(ControlFlowGraph cfg)
        {
            int n = cfg.Blocks.Count;
            var rpo = cfg.ReversePostOrder();
            // poIdx[blockId] = position in RPO (entry = 0).
            var poIdx = new int[n];
            for (int i = 0; i < poIdx.Length; i++) poIdx[i] = -1;
            for (int i = 0; i < rpo.Length; i++) poIdx[rpo[i]] = i;

            var idom = new int[n];
            for (int i = 0; i < n; i++) idom[i] = -1;
            idom[rpo[0]] = rpo[0]; // entry self-dominates per CHK

            bool changed = true;
            while (changed)
            {
                changed = false;
                // Skip entry (rpo[0]).
                for (int k = 1; k < rpo.Length; k++)
                {
                    int b = rpo[k];
                    var preds = cfg.Blocks[b].Predecessors;
                    // Find first processed predecessor.
                    int newIdom = -1;
                    foreach (var p in preds)
                    {
                        if (idom[p] != -1) { newIdom = p; break; }
                    }
                    if (newIdom == -1) continue; // unreachable
                    foreach (var p in preds)
                    {
                        if (p == newIdom) continue;
                        if (idom[p] != -1)
                            newIdom = Intersect(p, newIdom, idom, poIdx);
                    }
                    if (idom[b] != newIdom)
                    {
                        idom[b] = newIdom;
                        changed = true;
                    }
                }
            }
            // Normalise entry's idom from self-pointer to -1 so consumers
            // can root the dominator tree cleanly.
            idom[rpo[0]] = -1;
            return new Dominators(cfg, idom, rpo, poIdx);
        }

        // CHK Intersect: walks two finger pointers up the dominator
        // chain via RPO position comparison until they meet.
        private static int Intersect(int b1, int b2, int[] idom, int[] poIdx)
        {
            while (b1 != b2)
            {
                while (poIdx[b1] > poIdx[b2]) b1 = idom[b1];
                while (poIdx[b2] > poIdx[b1]) b2 = idom[b2];
            }
            return b1;
        }

        // Lazily build child-list view of the dominator tree.
        public List<int>[] BuildDominatorTree()
        {
            if (_domTree != null) return _domTree;
            var tree = new List<int>[Cfg.Blocks.Count];
            for (int i = 0; i < tree.Length; i++) tree[i] = new List<int>();
            for (int b = 0; b < Cfg.Blocks.Count; b++)
            {
                int p = IDom[b];
                if (p >= 0 && p != b) tree[p].Add(b);
            }
            _domTree = tree;
            return tree;
        }

        // Dominance frontier per CHK 2001. DF[b] = set of join points
        // where b's dominance ends. Used by SSA phi placement: a phi for
        // a definition in block b must be inserted at every block in
        // DF+(b) (iterated dominance frontier).
        public HashSet<int>[] DominanceFrontiers()
        {
            if (_df != null) return _df;
            int n = Cfg.Blocks.Count;
            var df = new HashSet<int>[n];
            for (int i = 0; i < df.Length; i++) df[i] = new HashSet<int>();
            for (int b = 0; b < n; b++)
            {
                var preds = Cfg.Blocks[b].Predecessors;
                if (preds.Count < 2) continue;
                int bIdom = IDom[b];
                foreach (var p in preds)
                {
                    int runner = p;
                    while (runner != -1 && runner != bIdom)
                    {
                        df[runner].Add(b);
                        runner = IDom[runner];
                    }
                }
            }
            _df = df;
            return df;
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Dominators of {Cfg.Function.Name}");
            for (int b = 0; b < Cfg.Blocks.Count; b++)
            {
                sb.AppendLine($"  BB{b}: idom={IDom[b]}");
            }
            sb.AppendLine("# Dominance frontiers");
            var dfs = DominanceFrontiers();
            for (int b = 0; b < dfs.Length; b++)
            {
                if (dfs[b].Count == 0) continue;
                sb.AppendLine($"  DF(BB{b}) = {{{string.Join(",", dfs[b])}}}");
            }
            return sb.ToString();
        }
    }
}

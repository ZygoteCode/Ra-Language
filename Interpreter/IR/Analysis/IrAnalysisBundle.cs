namespace RaLanguage.Interpreter.IR.Analysis
{
    // M64: container for the M54-M62 analysis pipeline applied to a
    // RaFunction at IR-finalize time. Held on the function so the
    // post-finalize `IrRewriter` can consume the dataflow facts and
    // rewrite the bytecode in place.
    //
    // Replaces the earlier `TierUpAnalysisBundle` / `TierUpCompiler`
    // scaffold. The JIT codegen path is OUT of charter for the
    // current milestone — see RA_VM_MIGRATION.md §37 for the
    // feasibility verdict and §38 for the in-place rewrite landing.
    // When a future milestone resumes runtime native codegen this
    // bundle is exactly the input it needs.
    public sealed class IrAnalysisBundle
    {
        public readonly ControlFlowGraph Cfg;
        public readonly Dominators Dom;
        public readonly SsaForm Ssa;
        public readonly SsaOptimizer Opt;
        public readonly GlobalValueNumbering Gvn;
        public readonly LoopAnalysis Loops;
        public readonly Sccp Sccp;

        public IrAnalysisBundle(ControlFlowGraph cfg, Dominators dom, SsaForm ssa)
        {
            Cfg = cfg; Dom = dom; Ssa = ssa;
            Opt = SsaOptimizer.Run(ssa);
            Gvn = GlobalValueNumbering.Run(ssa);
            Loops = LoopAnalysis.Run(ssa);
            Sccp = Sccp.Run(ssa);
        }

        public static IrAnalysisBundle Build(RaFunction fn)
        {
            var cfg = CfgBuilder.Build(fn);
            var dom = Dominators.Compute(cfg);
            var ssa = SsaForm.Build(cfg, dom);
            return new IrAnalysisBundle(cfg, dom, ssa);
        }
    }
}

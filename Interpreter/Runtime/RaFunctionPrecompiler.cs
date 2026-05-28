using System.Collections.Generic;
using RaLanguage.Interpreter.IR;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;

namespace RaLanguage.Interpreter.Runtime
{
    // v4 (#pre-compiled children): walk a freshly-compiled script's
    // AST-ref pools and force every nested function / method body to
    // its IR form at build time. Without this pass, the AST-side
    // `.CompiledBody` fields stay null until the first runtime
    // OP_DEFINE_FUNCTION / OP_NATIVE_DEFINE call — which means the
    // serialiser writes them as "no inline body" and the runtime
    // pays the lazy compile on first invocation.
    //
    // After this pass:
    //   - every FunctionDefinitionNode reachable through the script
    //     (FuncDefRefs, ClassDefinitionNode.Methods, ExtensionDefinitionNode.Methods,
    //      InterfaceDefinitionNode.* / TraitDefinitionNode.* method shapes)
    //     has `.CompiledBody` set when IR compile succeeded
    //   - every StructMethodDefinitionNode + TraitMethodDefinitionNode +
    //     OperatorDefinitionNode reached the same fixpoint
    //   - the serialiser observes `.CompiledBody != null` on each, emits
    //     the inline RaFunction, and the runtime loader cascades the cached
    //     body into RuntimeValue construction with zero further IR work.
    public static class RaFunctionPrecompiler
    {
        public static void PrecompileChildren(RaFunction script)
        {
            if (script == null) return;
            var visited = new HashSet<RaFunction>();
            WalkFunction(script, visited);
        }

        private static void WalkFunction(RaFunction fn, HashSet<RaFunction> visited)
        {
            if (fn == null || !visited.Add(fn)) return;

            // The IR compiler maintains six AST-ref pools that can
            // contain function-shaped nodes. FuncDefRefs is the
            // primary one (every OP_DefineFunction lands here);
            // DefineRefs covers OP_NATIVE_DEFINE, where structs,
            // traits, classes, interfaces, enums and extensions
            // live — each of those can recursively own functions
            // through their Methods / Operators / Properties /
            // Events lists.
            if (fn.FuncDefRefs != null)
                foreach (var node in fn.FuncDefRefs) PrecompileFunctionNode(node, visited);
            if (fn.DefineRefs != null)
                foreach (var node in fn.DefineRefs) PrecompileDefineNode(node, visited);

            // Recurse through the script's own nested function bodies.
            if (fn.Children != null)
                foreach (var child in fn.Children) WalkFunction(child, visited);
        }

        private static void PrecompileFunctionNode(FunctionDefinitionNode? node, HashSet<RaFunction> visited)
        {
            if (node == null) return;
            bool dbg = System.Environment.GetEnvironmentVariable("RA_PRECOMPILE_DEBUG") == "1";
            if (dbg)
                System.Console.WriteLine(
                    $"[precompile] {node.VarNameTok?.Value ?? "<anon>"} frameId={node.FrameId} body={(node.BodyNode != null)} tried={node.IrCompileTried} compiled={(node.CompiledBody != null)}");
            var compiled = FunctionDefinitionHelper.GetOrCompileBody(node);
            if (compiled != null) WalkFunction(compiled, visited);
            else if (dbg)
                System.Console.WriteLine(
                    $"[precompile]   -> null (frameId={node.FrameId} body={(node.BodyNode != null)} tried={node.IrCompileTried})");
        }

        private static void PrecompileStructMethod(StructMethodDefinitionNode? m, HashSet<RaFunction> visited)
        {
            if (m == null) return;
            var compiled = FunctionDefinitionHelper.GetOrCompileStructMethod(m);
            if (compiled != null) WalkFunction(compiled, visited);
        }

        private static void PrecompileTraitMethod(TraitMethodDefinitionNode? m, HashSet<RaFunction> visited)
        {
            if (m == null) return;
            var compiled = FunctionDefinitionHelper.GetOrCompileTraitMethod(m);
            if (compiled != null) WalkFunction(compiled, visited);
        }

        private static void PrecompileOperator(OperatorDefinitionNode? op, HashSet<RaFunction> visited)
        {
            if (op == null) return;
            var compiled = FunctionDefinitionHelper.GetOrCompileOperator(op);
            if (compiled != null) WalkFunction(compiled, visited);
        }

        private static void PrecompileDefineNode(RaLanguage.Parser.Nodes.AstNode? node, HashSet<RaFunction> visited)
        {
            switch (node)
            {
                case ClassDefinitionNode c:
                    foreach (var m in c.Methods) PrecompileFunctionNode(m, visited);
                    foreach (var op in c.Operators) PrecompileOperator(op, visited);
                    foreach (var p in c.Properties)
                        foreach (var a in p.Accessors)
                            if (a.BodyNode is FunctionDefinitionNode innerFn)
                                PrecompileFunctionNode(innerFn, visited);
                    break;
                case StructDefinitionNode s:
                    foreach (var m in s.Methods) PrecompileStructMethod(m, visited);
                    foreach (var op in s.Operators) PrecompileOperator(op, visited);
                    foreach (var p in s.Properties)
                        foreach (var a in p.Accessors)
                            if (a.BodyNode is FunctionDefinitionNode innerFn)
                                PrecompileFunctionNode(innerFn, visited);
                    break;
                case RaLanguage.Parser.Nodes.Records.RecordDefinitionNode rec:
                    foreach (var m in rec.Methods) PrecompileStructMethod(m, visited);
                    foreach (var op in rec.Operators) PrecompileOperator(op, visited);
                    break;
                case ExtensionDefinitionNode e:
                    foreach (var m in e.Methods) PrecompileFunctionNode(m, visited);
                    foreach (var op in e.Operators) PrecompileOperator(op, visited);
                    foreach (var (m, _) in e.Indexers) PrecompileFunctionNode(m, visited);
                    break;
                case TraitDefinitionNode t:
                    foreach (var m in t.Methods) PrecompileTraitMethod(m, visited);
                    break;
                case RaLanguage.Parser.Nodes.Interfaces.InterfaceDefinitionNode _:
                    // Interface methods are bare signatures — no bodies to compile.
                    break;
                case FunctionDefinitionNode fdef:
                    // DefineRefs may carry standalone fn definitions
                    // (e.g. lowered patterns / lambdas surfacing as
                    // NativeDefine entries).
                    PrecompileFunctionNode(fdef, visited);
                    break;
                default:
                    break;
            }
        }
    }
}

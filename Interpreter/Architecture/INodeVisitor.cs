using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter.Architecture
{
    // Async-capable visitor entry point. The signature returns ValueTask so
    // sync-completing visits pay no allocation (ValueTask wraps the result
    // directly), while any visitor that genuinely needs to suspend — most
    // notably AwaitNodeVisitor — can return a non-completed ValueTask and
    // unwind the calling chain without blocking the host worker.
    //
    // Background: the previous sync-only INodeVisitor forced every `await x`
    // in user code to translate to `_tcs.Task.GetAwaiter().GetResult()` deep
    // inside the visitor stack, pinning the calling thread-pool worker for
    // the duration of the wait. The audit (item 5.7) called this out as
    // sync-over-async: the pre-warm to 128+ workers masked the symptom but
    // did not address the cause. Making the visitor pipeline itself async
    // lets `await` truly suspend without holding a worker.
    public interface INodeVisitor
    {
        ValueTask<RuntimeResult> Visit(AstNode node, Context context, IInterpreter interpreter);
    }
}

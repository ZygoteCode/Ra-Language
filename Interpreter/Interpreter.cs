using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Visitors.Annotations;
using RaLanguage.Interpreter.Visitors.Classes;
using RaLanguage.Interpreter.Visitors.Enums;
using RaLanguage.Interpreter.Visitors.Extensions;
using RaLanguage.Interpreter.Visitors.Functions;
using RaLanguage.Interpreter.Visitors.Interfaces;
using RaLanguage.Interpreter.Visitors.Iterations;
using RaLanguage.Interpreter.Visitors.Members;
using RaLanguage.Interpreter.Visitors.Operations;
using RaLanguage.Interpreter.Visitors.Primitives;
using RaLanguage.Interpreter.Visitors.Special;
using RaLanguage.Interpreter.Visitors.Statements;
using RaLanguage.Interpreter.Visitors.Structs;
using RaLanguage.Interpreter.Visitors.Traits;
using RaLanguage.Interpreter.Visitors.Variables;
using RaLanguage.Interpreter.Visitors.Imports;
using RaLanguage.Interpreter.Visitors.Async;
using RaLanguage.Interpreter.Visitors.Namespaces;
using RaLanguage.Interpreter.Visitors.Asm;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter
{
    public class Interpreter : IInterpreter
    {
        public List<(string, AstNode)> Labels { get; } = new List<(string, AstNode)>();

        // Closed-instance delegates pointing directly at each visitor's Visit method.
        // Replaces the previous INodeVisitor[] + interface dispatch: delegate invocation
        // is a single indirect call the JIT/AOT can inline and devirtualize, while
        // interface dispatch always pays the IVT lookup per node.
        //
        // Async-capable since the v5.7 pipeline change: each delegate returns a
        // ValueTask<RuntimeResult>. Sync-completing visitors (the overwhelming
        // majority) return a synchronously-completed ValueTask so dispatch
        // pays no allocation. Only visitors that genuinely suspend (the await
        // path) yield to their caller, propagating the suspension up.
        private readonly Func<AstNode, Context, IInterpreter, ValueTask<RuntimeResult>>[] _visitors;

        public Interpreter()
        {
            var typesCount = Enum.GetValues<AstNodeType>().Length;
            _visitors = new Func<AstNode, Context, IInterpreter, ValueTask<RuntimeResult>>[typesCount];
            RegisterVisitors();
        }

        public void RegisterVisitors()
        {
            var typesCount = Enum.GetValues<AstNodeType>().Length;

            _visitors[(int)AstNodeType.Number] = new NumberNodeVisitor().Visit;
            _visitors[(int)AstNodeType.String] = new StringNodeVisitor().Visit;
            _visitors[(int)AstNodeType.List] = new ListNodeVisitor().Visit;
            _visitors[(int)AstNodeType.VariableAccess] = new VariableAccessNodeVisitor().Visit;
            _visitors[(int)AstNodeType.VariableDeclaration] = new VariableDeclarationNodeVisitor().Visit;
            _visitors[(int)AstNodeType.VariableAssignment] = new VariableAssignmentNodeVisitor().Visit;
            _visitors[(int)AstNodeType.VariableDelete] = new VariableDeleteNodeVisitor().Visit;
            _visitors[(int)AstNodeType.BinaryOperation] = new BinaryOperationNodeVisitor().Visit;
            _visitors[(int)AstNodeType.UnaryOperation] = new UnaryOperationNodeVisitor().Visit;
            _visitors[(int)AstNodeType.If] = new IfNodeVisitor().Visit;
            _visitors[(int)AstNodeType.For] = new ForNodeVisitor().Visit;
            _visitors[(int)AstNodeType.While] = new WhileNodeVisitor().Visit;
            _visitors[(int)AstNodeType.FunctionDefinition] = new FunctionDefinitionNodeVisitor().Visit;
            _visitors[(int)AstNodeType.FunctionCall] = new FunctionCallNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Return] = new ReturnNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Continue] = new ContinueNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Break] = new BreakNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Pass] = new PassNodeVisitor().Visit;
            _visitors[(int)AstNodeType.DoWhile] = new DoWhileNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Typeof] = new TypeofNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Nameof] = new NameofNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Null] = new NullNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Boolean] = new BooleanNodeVisitor().Visit;
            _visitors[(int)AstNodeType.ListAccess] = new ListAccessNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Set] = new SetNodeVisitor().Visit;
            _visitors[(int)AstNodeType.ListAssignment] = new ListAssignmentNodeVisitor().Visit;
            _visitors[(int)AstNodeType.ForEach] = new ForEachNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Range] = new RangeNodeVisitor().Visit;
            _visitors[(int)AstNodeType.NullCoalescing] = new NullCoalescingNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Ternary] = new TernaryNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Map] = new MapNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Yield] = new YieldNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Switch] = new SwitchNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Tuple] = new TupleNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Cast] = new CastNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Try] = new TryNodeVisitor().Visit;
            _visitors[(int)AstNodeType.SuperFor] = new SuperForNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Label] = new LabelNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Goto] = new GotoNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Retry] = new RetryNodeVisitor().Visit;
            _visitors[(int)AstNodeType.EnumAccess] = new EnumAccessNodeVisitor().Visit;
            _visitors[(int)AstNodeType.EnumDefinition] = new EnumDefinitionNodeVisitor().Visit;
            _visitors[(int)AstNodeType.StructDefinition] = new StructDefinitionNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Self] = new SelfNodeVisitor().Visit;
            _visitors[(int)AstNodeType.MemberAccess] = new MemberAccessNodeVisitor().Visit;
            _visitors[(int)AstNodeType.MemberAssignment] = new MemberAssignmentNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Scope] = new ScopeNodeVisitor().Visit;
            _visitors[(int)AstNodeType.ClassDefinition] = new ClassDefinitionNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Super] = new SuperNodeVisitor().Visit;
            _visitors[(int)AstNodeType.InterfaceDefinition] = new InterfaceDefinitionNodeVisitor().Visit;
            _visitors[(int)AstNodeType.TraitDefinition] = new TraitDefinitionNodeVisitor().Visit;
            _visitors[(int)AstNodeType.ExtensionDefinition] = new ExtensionDefinitionNodeVisitor().Visit;
            _visitors[(int)AstNodeType.ImportAll] = new ImportNodeVisitor().Visit;
            _visitors[(int)AstNodeType.ImportSelective] = new ImportNodeVisitor().Visit;
            _visitors[(int)AstNodeType.ImportAlias] = new ImportNodeVisitor().Visit;
            _visitors[(int)AstNodeType.AnnotationDefinition] = new AnnotationDefinitionNodeVisitor().Visit;
            _visitors[(int)AstNodeType.AnnotationApplication] = new AnnotationApplicationNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Await] = new AwaitNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Spawn] = new SpawnNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Emit] = new EmitNodeVisitor().Visit;
            _visitors[(int)AstNodeType.ForAwait] = new ForAwaitNodeVisitor().Visit;
            _visitors[(int)AstNodeType.NamespaceDeclaration] = new NamespaceDeclarationNodeVisitor().Visit;
            _visitors[(int)AstNodeType.UsingNamespace] = new UsingNamespaceNodeVisitor().Visit;
            _visitors[(int)AstNodeType.AsmBlock] = new AsmBlockNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Borrow] = new BorrowNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Dereference] = new DereferenceNodeVisitor().Visit;
            _visitors[(int)AstNodeType.DereferenceAssignment] = new DereferenceAssignmentNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Pipeline] = new PipelineNodeVisitor().Visit;
            _visitors[(int)AstNodeType.FormattedInterpolation] = new FormattedInterpolationNodeVisitor().Visit;
            _visitors[(int)AstNodeType.RegexLiteral] = new RegexLiteralNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Match] = new RaLanguage.Interpreter.Visitors.Patterns.MatchNodeVisitor().Visit;
            _visitors[(int)AstNodeType.TryUnwrap] = new RaLanguage.Interpreter.Visitors.Patterns.TryUnwrapNodeVisitor().Visit;
            _visitors[(int)AstNodeType.Throw] = new ThrowNodeVisitor().Visit;
        }

        public ValueTask<RuntimeResult> Visit(AstNode node, Context context)
        {
            var index = (int)node.NodeType;
            if (index < 0 || index >= _visitors.Length || _visitors[index] == null)
                throw new Exception($"No visitor module registered for the node: {node.NodeType}");
            return _visitors[index](node, context, this);
        }

        // Sync entry-point for hosts that cannot await (Program.Run, REPL
        // top-level, microbenchmark loop). Blocks the calling thread exactly
        // once — at the outermost frame — instead of paying the
        // sync-over-async tax inside every Ra `await` expression. ValueTask
        // sync-completion fast-path is preserved by GetAwaiter().GetResult().
        public RuntimeResult VisitBlocking(AstNode node, Context context)
        {
            var task = Visit(node, context);
            if (task.IsCompletedSuccessfully) return task.Result;
            return task.AsTask().GetAwaiter().GetResult();
        }

        public (RuntimeValue? value, Error? error) ExtractVariableValueByName(string name, Position posStart, Position posEnd, Context context)
        {
            var entry = context.SymbolTable.GetEntry(name);
            if (entry == null)
                return (null, new RuntimeError(posStart, posEnd,
                    $"'{name}' is not defined",
                    context,
                    code: DiagnosticCode.RuntimeUndefinedSymbol,
                    primaryLabel: "no such symbol in scope",
                    help: $"declare '{name}' with 'var', 'let', 'const' or 'final' before using it, or check the spelling"));

            if (entry.IsMoved)
                return (null, new RuntimeError(posStart, posEnd,
                    $"value of '{name}' was already moved",
                    context,
                    code: DiagnosticCode.RuntimeMovedValue,
                    primaryLabel: "used here after move",
                    help: "non-copy 'let' bindings transfer ownership on use; rebind the value or take a copy"));

            // `let const` bindings are compile-time-stable constants: the binding
            // itself may not be reseated and the value may not be moved out. For
            // sharable types (containers, instances) the read aliases the underlying
            // value — mutations through a shared list are still possible, matching
            // the documented memory model. For IsCopy primitives Aliased() yields a
            // fresh-identity copy (which is a no-op identity for immutable scalars).
            if (entry.IsConstBinding)
                return (entry.Value.Aliased().SetContext(context).SetPos(posStart, posEnd), null);

            if (entry.IsLet && !entry.Value.IsCopy)
            {
                if (entry.IsBorrowed)
                    return (null, new RuntimeError(posStart, posEnd,
                        $"cannot move out of '{name}': it is currently borrowed",
                        context,
                        code: DiagnosticCode.RuntimeBorrowViolation,
                        primaryLabel: entry.HasMutableBorrow
                            ? "binding is exclusively borrowed (&mut)"
                            : $"binding has {entry.SharedBorrowCount} shared borrow(s) alive",
                        help: "the value cannot be moved while borrows are alive; let the borrows drop first or clone the value"));

                entry.IsMoved = true;
                return (entry.Value.SetContext(context).SetPos(posStart, posEnd), null);
            }

            // Default read path. Aliased() shares containers/instances and keeps
            // primitives at the same observable cost (Copy() returns `this`).
            return (entry.Value.Aliased().SetContext(context).SetPos(posStart, posEnd), null);
        }
    }
}
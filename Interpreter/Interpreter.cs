using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Visitors.Functions;
using RaLanguage.Interpreter.Visitors.Iterations;
using RaLanguage.Interpreter.Visitors.Operations;
using RaLanguage.Interpreter.Visitors.Primitives;
using RaLanguage.Interpreter.Visitors.Special;
using RaLanguage.Interpreter.Visitors.Statements;
using RaLanguage.Interpreter.Visitors.Variables;
using RaLanguage.Lexer;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Interpreter
{
    public class Interpreter : IInterpreter
    {
        public bool AreCallsBlocked { get; set; } = false;
        public List<(string, AstNode)> Labels { get; } = new List<(string, AstNode)>();
        private readonly INodeVisitor[] _visitors;

        public Interpreter()
        {
            var typesCount = Enum.GetValues<AstNodeType>().Length;
            _visitors = new INodeVisitor[typesCount];
            RegisterVisitors();
        }

        public void RegisterVisitors()
        {
            var typesCount = Enum.GetValues<AstNodeType>().Length;

            _visitors[(int)AstNodeType.Number] = new NumberNodeVisitor();
            _visitors[(int)AstNodeType.String] = new StringNodeVisitor();
            _visitors[(int)AstNodeType.List] = new ListNodeVisitor();
            _visitors[(int)AstNodeType.VariableAccess] = new VariableAccessNodeVisitor();
            _visitors[(int)AstNodeType.VariableDeclaration] = new VariableDeclarationNodeVisitor();
            _visitors[(int)AstNodeType.VariableAssignment] = new VariableAssignmentNodeVisitor();
            _visitors[(int)AstNodeType.VariableDelete] = new VariableDeleteNodeVisitor();
            _visitors[(int)AstNodeType.BinaryOperation] = new BinaryOperationNodeVisitor();
            _visitors[(int)AstNodeType.UnaryOperation] = new UnaryOperationNodeVisitor();
            _visitors[(int)AstNodeType.If] = new IfNodeVisitor();
            _visitors[(int)AstNodeType.For] = new ForNodeVisitor();
            _visitors[(int)AstNodeType.While] = new WhileNodeVisitor();
            _visitors[(int)AstNodeType.FunctionDefinition] = new FunctionDefinitionNodeVisitor();
            _visitors[(int)AstNodeType.FunctionCall] = new FunctionCallNodeVisitor();
            _visitors[(int)AstNodeType.Return] = new ReturnNodeVisitor();
            _visitors[(int)AstNodeType.Continue] = new ContinueNodeVisitor();
            _visitors[(int)AstNodeType.Break] = new BreakNodeVisitor();
            _visitors[(int)AstNodeType.Pass] = new PassNodeVisitor();
            _visitors[(int)AstNodeType.DoWhile] = new DoWhileNodeVisitor();
            _visitors[(int)AstNodeType.Typeof] = new TypeofNodeVisitor();
            _visitors[(int)AstNodeType.Nameof] = new NameofNodeVisitor();
            _visitors[(int)AstNodeType.Null] = new NullNodeVisitor();
            _visitors[(int)AstNodeType.Boolean] = new BooleanNodeVisitor();
            _visitors[(int)AstNodeType.ListAccess] = new ListAccessNodeVisitor();
            _visitors[(int)AstNodeType.Set] = new SetNodeVisitor();
            _visitors[(int)AstNodeType.ListAssignment] = new ListAssignmentNodeVisitor();
            _visitors[(int)AstNodeType.ForEach] = new ForEachNodeVisitor();
            _visitors[(int)AstNodeType.Range] = new RangeNodeVisitor();
            _visitors[(int)AstNodeType.NullCoalescing] = new NullCoalescingNodeVisitor();
            _visitors[(int)AstNodeType.Ternary] = new TernaryNodeVisitor();
            _visitors[(int)AstNodeType.Map] = new MapNodeVisitor();
            _visitors[(int)AstNodeType.Yield] = new YieldNodeVisitor();
            _visitors[(int)AstNodeType.Switch] = new SwitchNodeVisitor();
            _visitors[(int)AstNodeType.Tuple] = new TupleNodeVisitor();
            _visitors[(int)AstNodeType.Cast] = new CastNodeVisitor();
            _visitors[(int)AstNodeType.Try] = new TryNodeVisitor();
            _visitors[(int)AstNodeType.SuperFor] = new SuperForNodeVisitor();
            _visitors[(int)AstNodeType.Label] = new LabelNodeVisitor();
            _visitors[(int)AstNodeType.Goto] = new GotoNodeVisitor();
        }

        public RuntimeResult Visit(AstNode node, Context context)
        {
            var index = (int)node.NodeType;
            if (index < 0 || index >= _visitors.Length || _visitors[index] == null)
                throw new Exception($"No visitor module registered for the node: {node.NodeType}");
            return _visitors[index].Visit(node, context, this);
        }

        public (RuntimeValue? value, Error? error) ExtractVariableValueByName(string name, Position posStart, Position posEnd, Context context)
        {
            var entry = context.SymbolTable.GetEntry(name);
            if (entry == null)
                return (null, new RuntimeError(posStart, posEnd, $"'{name}' is not defined", context));

            if (entry.IsMoved)
                return (null, new RuntimeError(posStart, posEnd, $"Variable '{name}' was moved", context));

            if (entry.IsLet && !entry.Value.IsCopy)
            {
                entry.IsMoved = true;
                return (entry.Value.SetContext(context).SetPos(posStart, posEnd), null);
            }

            return (entry.Value.Copy().SetContext(context).SetPos(posStart, posEnd), null);
        }
    }
}
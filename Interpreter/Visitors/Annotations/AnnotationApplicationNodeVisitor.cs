using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Annotations;
using RaLanguage.Parser.Nodes.Annotations;

namespace RaLanguage.Interpreter.Visitors.Annotations
{
    public sealed class AnnotationApplicationNodeVisitor : NodeVisitor<AnnotationApplicationNode>
    {
        protected override RuntimeResult VisitNode(AnnotationApplicationNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var typeSymbol = context.SymbolTable.Get(node.Name);
            if (typeSymbol is not AnnotationTypeValue typeValue)
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Annotation '@{node.Name}' is not defined", context));

            var (positional, named, err) = AnnotationProcessor.EvaluateArgs(node, typeValue, context, interpreter);
            if (err != null) return res.Failure(err);

            var instance = new AnnotationInstanceValue(
                typeValue,
                positional,
                named,
                node.PositionStart,
                node.PositionEnd)
            {
                Priority = typeValue.Priority
            };
            instance.SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
            return res.Success(instance);
        }
    }
}

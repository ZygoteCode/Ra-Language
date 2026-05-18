using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Parser.Nodes.Async;

namespace RaLanguage.Interpreter.Visitors.Async
{
    public class EmitNodeVisitor : NodeVisitor<EmitNode>
    {
        protected sealed override RuntimeResult VisitNode(EmitNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var producer = context.AsyncCtx?.CurrentStreamProducer;
            if (producer == null)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "'emit' is only valid inside an 'async stream fn' body", context));
            }

            var inner = interpreter.Visit(node.Expression, context);
            if (inner.Error != null) return res.Failure(inner.Error);

            var value = inner.Value ?? new RaLanguage.Interpreter.Values.Primitives.NullValue().SetContext(context).SetPos(node.PositionStart, node.PositionEnd);

            var owner = producer.OwnerValue;
            if (owner != null)
            {
                if (owner.ElementType != null && !owner.ElementType.IsTypeParameter
                    && !RaLanguage.Types.TypeSystem.IsAssignable(context, owner.ElementType, value))
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, $"Stream element type mismatch: expected '{owner.ElementType}', got '{value.Type}'", context));
                }
                if (owner.ElementType == null && value.Type != RaLanguage.Interpreter.Values.RuntimeValueType.Null)
                {
                    owner.ElementType = RaLanguage.Types.TypeSystem.GetDescriptorFromRuntimeValue(value);
                }
            }

            var accepted = producer.Emit(value);
            if (!accepted)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Stream consumer has been cancelled or closed", context));
            }

            return res.Success(value);
        }
    }
}

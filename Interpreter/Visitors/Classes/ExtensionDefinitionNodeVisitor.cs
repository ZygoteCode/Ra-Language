using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Annotations;
using RaLanguage.Interpreter.Runtime.Events;
using RaLanguage.Interpreter.Runtime.Properties;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Events;
using RaLanguage.Parser.Nodes.Properties;
using RaLanguage.Parser.Nodes.Structs;

namespace RaLanguage.Interpreter.Visitors.Extensions
{
    public class ExtensionDefinitionNodeVisitor : NodeVisitor<ExtensionDefinitionNode>
    {
        protected override async ValueTask<RuntimeResult> VisitNode(ExtensionDefinitionNode node, Context context, IInterpreter interpreter)
            => Apply(node, context);

        public static RuntimeResult Apply(ExtensionDefinitionNode node, Context context)
        {
            var res = new RuntimeResult();

            var targetName = node.TargetType.Name;
            if (string.IsNullOrWhiteSpace(targetName))
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd, "Invalid extension target type", context));

            // @sealed extend T: probe the annotation list directly. The
            // annotation processor's target-kind validator does not know
            // about extensions; reading the annotation here lets us
            // honour the directive without rejecting a valid form.
            bool isSealed = node.IsSealed || HasSealedAnnotation(node);

            if (context.Extensions.IsSealed(targetName))
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"target type '{targetName}' is sealed against further extension declarations",
                    context,
                    code: Errors.DiagnosticCode.RuntimeGeneric,
                    help: "@sealed extend forbids subsequent 'extend " + targetName + " { ... }' blocks in this module and any module that imports it"));
            }

            bool blockPublic = node.IsPublic;
            string? declaringModule = context.DisplayName;
            var targetTypeDescriptor = node.TargetType;

            // Collect indexer method names so the bare method list
            // gets filtered. Indexer-marked methods are excluded from
            // the regular method bucket — they only resolve through
            // the indexer path to avoid two reachable entry points.
            var indexerMethodSet = new HashSet<Parser.Nodes.Functions.FunctionDefinitionNode>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var (m, _) in node.Indexers) indexerMethodSet.Add(m);

            foreach (var method in node.Methods)
            {
                if (indexerMethodSet.Contains(method)) continue;
                context.Extensions.RegisterMethod(
                    targetName,
                    method,
                    isBlockPublic: blockPublic,
                    isLocal: true,
                    declaringModule: declaringModule,
                    targetType: targetTypeDescriptor);
            }

            foreach (var (method, isSetter) in node.Indexers)
            {
                context.Extensions.RegisterIndexer(targetName, new ExtensionIndexerEntry(
                    method,
                    isSetter: isSetter,
                    isBlockPublic: blockPublic,
                    isLocal: true,
                    declaringModule: declaringModule,
                    targetType: targetTypeDescriptor));
            }

            foreach (var prop in node.Properties)
            {
                var validationErr = ValidateExtensionProperty(prop, targetName, context);
                if (validationErr != null) return res.Failure(validationErr);

                var descriptor = PropertyBuilder.Build(prop, targetName);

                if (descriptor.HasBacking)
                {
                    return res.Failure(new RuntimeError(prop.PositionStart, prop.PositionEnd,
                        $"extension property '{targetName}.{descriptor.Name}' cannot have a backing slot — extensions cannot extend the receiver's storage layout",
                        context,
                        code: Errors.DiagnosticCode.RuntimeGeneric,
                        help: "declare the property as a computed accessor: 'prop " + descriptor.Name + " => <expr>' or 'prop " + descriptor.Name + " { get { ... } set { ... } }'"));
                }

                if (!context.Extensions.RegisterProperty(
                        targetName,
                        descriptor,
                        isBlockPublic: blockPublic,
                        isLocal: true,
                        declaringModule: declaringModule,
                        out var dupErr,
                        targetType: targetTypeDescriptor))
                {
                    return res.Failure(new RuntimeError(prop.PositionStart, prop.PositionEnd,
                        dupErr ?? "duplicate extension property",
                        context));
                }
            }

            foreach (var op in node.Operators)
            {
                context.Extensions.RegisterOperator(
                    targetName,
                    op,
                    isBlockPublic: blockPublic,
                    isLocal: true,
                    declaringModule: declaringModule,
                    targetType: targetTypeDescriptor);
            }

            foreach (var ev in node.Events)
            {
                var validationErr = ValidateExtensionEvent(ev, targetName, context);
                if (validationErr != null) return res.Failure(validationErr);

                // Extension events default to public subscribe / private raise,
                // mirroring class-level event defaults.
                var subscribeIsPublic = ev.IsPublic || blockPublic;
                bool raiseIsPublic = false;
                foreach (var acc in ev.Accessors)
                {
                    if (string.Equals(acc.KindTok.Value?.ToString(), "raise", StringComparison.Ordinal))
                        raiseIsPublic = acc.Visibility == EventAccessorVisibility.Public;
                    else if (string.Equals(acc.KindTok.Value?.ToString(), "subscribe", StringComparison.Ordinal))
                        subscribeIsPublic = acc.Visibility != EventAccessorVisibility.Private;
                }

                var desc = new EventDescriptor(ev, targetName, subscribeIsPublic, raiseIsPublic);
                if (!context.Extensions.RegisterEvent(
                        targetName,
                        desc,
                        isBlockPublic: blockPublic,
                        isLocal: true,
                        declaringModule: declaringModule,
                        out var dupErr,
                        targetType: targetTypeDescriptor))
                {
                    return res.Failure(new RuntimeError(ev.PositionStart, ev.PositionEnd,
                        dupErr ?? "duplicate extension event",
                        context));
                }
            }

            foreach (var fieldDecl in node.Fields)
            {
                var field = fieldDecl.Field;
                string fieldName = field.NameTok.Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(fieldName))
                    return res.Failure(new RuntimeError(field.PositionStart, field.PositionEnd,
                        "extension field requires a name", context));

                if (fieldDecl.IsLazy && field.DefaultValueNode == null)
                    return res.Failure(new RuntimeError(field.PositionStart, field.PositionEnd,
                        $"lazy extension field '{targetName}.{fieldName}' must declare an initializer expression with '= expr'",
                        context));

                int slot = ExtensionFieldStorage.AllocateSlot(
                    targetName,
                    fieldDecl.IsStaticField ? null : targetTypeDescriptor,
                    fieldName);
                var descriptor = new ExtensionFieldDescriptor(
                    field,
                    targetName,
                    slot,
                    isStaticField: fieldDecl.IsStaticField,
                    isLazy: fieldDecl.IsLazy);
                ExtensionFieldStorage.RegisterDescriptor(slot, descriptor);

                if (!context.Extensions.RegisterField(
                        targetName,
                        descriptor,
                        isBlockPublic: blockPublic,
                        isLocal: true,
                        declaringModule: declaringModule,
                        out var dupErr,
                        targetType: fieldDecl.IsStaticField ? null : targetTypeDescriptor))
                {
                    return res.Failure(new RuntimeError(field.PositionStart, field.PositionEnd,
                        dupErr ?? "duplicate extension field",
                        context));
                }
            }

            if (isSealed)
                context.Extensions.MarkSealed(targetName);

            return res.Success(NullValue.Null.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }

        private static bool HasSealedAnnotation(ExtensionDefinitionNode node)
        {
            if (!node.HasAnnotations || node.Annotations == null) return false;
            foreach (var a in node.Annotations)
            {
                var name = a.NameTok.Value?.ToString();
                if (string.Equals(name, BuiltInAnnotations.Sealed, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static RuntimeError? ValidateExtensionProperty(PropertyDefinitionNode prop, string targetName, Context context)
        {
            string propName = prop.NameTok.Value?.ToString() ?? "";

            if (prop.IsLazy)
                return new RuntimeError(prop.PositionStart, prop.PositionEnd,
                    $"extension property '{targetName}.{propName}' cannot be lazy — extensions have no storage to memoise into",
                    context);
            if (prop.IsStatic)
                return new RuntimeError(prop.PositionStart, prop.PositionEnd,
                    $"extension property '{targetName}.{propName}' cannot be static — extensions extend instances, not the type itself",
                    context);
            if (prop.IsAbstract)
                return new RuntimeError(prop.PositionStart, prop.PositionEnd,
                    $"extension property '{targetName}.{propName}' cannot be abstract — there is no override chain to fulfil it",
                    context);
            if (prop.IsOverride)
                return new RuntimeError(prop.PositionStart, prop.PositionEnd,
                    $"extension property '{targetName}.{propName}' cannot use 'override' — extensions add new members, they do not replace existing ones",
                    context);

            foreach (var acc in prop.Accessors)
            {
                if (acc.Kind == PropertyAccessorKind.Init)
                    return new RuntimeError(acc.KindTok.PositionStart, acc.KindTok.PositionEnd,
                        $"extension property '{targetName}.{propName}' cannot declare an 'init' accessor — there is no constructor to bind it to",
                        context);
                if (acc.Kind == PropertyAccessorKind.Observe)
                    return new RuntimeError(acc.KindTok.PositionStart, acc.KindTok.PositionEnd,
                        $"extension property '{targetName}.{propName}' cannot declare an 'observe' accessor — without backing storage there is nothing to watch",
                        context);
                if (acc.BodyNode == null && (acc.Kind == PropertyAccessorKind.Get || acc.Kind == PropertyAccessorKind.Set))
                    return new RuntimeError(acc.KindTok.PositionStart, acc.KindTok.PositionEnd,
                        $"extension property '{targetName}.{propName}' cannot have an auto '{acc.Kind.ToString().ToLowerInvariant()}' accessor — extensions cannot store data on the receiver. Write the body explicitly",
                        context,
                        code: Errors.DiagnosticCode.RuntimeGeneric,
                        help: "use 'get => <expr>' or 'get { return <expr> }' (and similarly for set)");
            }

            if (prop.DefaultValueNode != null)
                return new RuntimeError(prop.PositionStart, prop.PositionEnd,
                    $"extension property '{targetName}.{propName}' cannot declare a default value — extensions have no backing slot to initialise",
                    context);

            return null;
        }

        private static RuntimeError? ValidateExtensionEvent(EventDefinitionNode ev, string targetName, Context context)
        {
            string name = ev.NameTok.Value?.ToString() ?? "";
            if (ev.IsStatic)
                return new RuntimeError(ev.PositionStart, ev.PositionEnd,
                    $"extension event '{targetName}.{name}' cannot be static — extensions extend instances, not the type itself",
                    context);
            if (ev.IsAbstract)
                return new RuntimeError(ev.PositionStart, ev.PositionEnd,
                    $"extension event '{targetName}.{name}' cannot be abstract — there is no override chain to fulfil it",
                    context);
            if (ev.IsOverride)
                return new RuntimeError(ev.PositionStart, ev.PositionEnd,
                    $"extension event '{targetName}.{name}' cannot use 'override' — extensions add new members, they do not replace existing ones",
                    context);
            return null;
        }
    }
}

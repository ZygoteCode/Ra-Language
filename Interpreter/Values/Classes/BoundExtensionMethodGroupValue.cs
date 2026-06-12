using RaLanguage.Errors.Types;
using System.Threading.Tasks;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Functions;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Interpreter.Values.Traits;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace RaLanguage.Interpreter.Values.Classes
{
    public class BoundExtensionMethodGroupValue : BaseFunctionValue
    {
        public RuntimeValue Receiver { get; }
        public List<FunctionDefinitionNode> Candidates { get; }

        // Optional entry metadata — present when the resolver supplied
        // it. Used to render cross-module ambiguity diagnostics; the
        // dispatch logic itself only consults Candidates.
        public List<ExtensionMethodEntry>? Entries { get; }

        public override RuntimeValueType Type => RuntimeValueType.BaseFunction;

        public BoundExtensionMethodGroupValue(RuntimeValue receiver, List<FunctionDefinitionNode> candidates)
            : base("<extension>")
        {
            Receiver = receiver;
            Candidates = candidates;
        }

        public BoundExtensionMethodGroupValue(RuntimeValue receiver, List<ExtensionMethodEntry> entries)
            : base("<extension>")
        {
            Receiver = receiver;
            Entries = entries;
            Candidates = new List<FunctionDefinitionNode>(entries.Count);
            foreach (var e in entries) Candidates.Add(e.Method);
        }

        public override async ValueTask<RuntimeResult> Execute(List<RuntimeValue> args)
            => await ExecuteWithNamedArgs(args, new Dictionary<string, RuntimeValue>(StringComparer.Ordinal));

        public override async ValueTask<RuntimeResult> ExecuteWithNamedArgs(List<RuntimeValue> positionalArgs, Dictionary<string, RuntimeValue> namedArgs)
        {
            var res = new RuntimeResult();
            var interpreter = new Interpreter();

            // Cross-module ambiguity: collect every candidate that
            // would bind to the current args. If 2+ live in the same
            // tier (both local OR both imported) AND originate from
            // different declaring modules, the dispatch is ambiguous
            // — refuse rather than picking arbitrarily.
            FunctionDefinitionNode? selected = null;
            int firstSelectedIdx = -1;
            for (int i = 0; i < Candidates.Count; i++)
            {
                if (!MethodCallBinder.CanBind(Candidates[i], positionalArgs, namedArgs, Context))
                    continue;
                if (selected == null) { selected = Candidates[i]; firstSelectedIdx = i; continue; }

                if (Entries != null && firstSelectedIdx >= 0 && i < Entries.Count && firstSelectedIdx < Entries.Count)
                {
                    var a = Entries[firstSelectedIdx];
                    var b = Entries[i];
                    if (a.IsLocal == b.IsLocal
                        && !string.Equals(a.DeclaringModule, b.DeclaringModule, StringComparison.OrdinalIgnoreCase))
                    {
                        var details = new StringBuilder();
                        details.Append($"ambiguous extension method '{Name}' — multiple imported overloads bind to this call:");
                        details.Append("\n  - ").Append(DescribeEntry(a));
                        details.Append("\n  - ").Append(DescribeEntry(b));
                        return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                            details.ToString(),
                            Context,
                            code: Errors.DiagnosticCode.RuntimeGeneric,
                            help: "shadow the ambiguous import with a local 'extend' declaration, or disambiguate by calling the method via the module wrapper"));
                    }
                }
            }

            if (selected == null)
            {
                var fmErr = MethodCallBinder.DescribeCallableArgMismatch(Candidates, positionalArgs, Context, PositionStart, PositionEnd);
                if (fmErr != null) return res.Failure(fmErr);
                return res.Failure(new RuntimeError(PositionStart, PositionEnd, $"No matching extension overload found for '{Name}'", Context));
            }

            var execCtx = GenerateNewContext();

            var selfValue = Receiver.IsCopy ? Receiver.Copy() : Receiver;
            var selfTypeName = TypeSystem.GetExtensionTargetName(Receiver);

            execCtx.SymbolTable.Set(
                "self",
                selfValue,
                isLet: true,
                declaredType: new TypeDescriptor(selfTypeName),
                isStaticallyTyped: true,
                isPublic: false);

            var bind = await MethodCallBinder.BindIntoContext(
                selected,
                execCtx,
                positionalArgs,
                namedArgs,
                selfTypeName);

            if (bind.error != null)
                return res.Failure(bind.error);

            RuntimeResult bodyRes;
            var compiled = selected is RaLanguage.Parser.Nodes.Functions.FunctionDefinitionNode fdn
                ? Runtime.FunctionDefinitionHelper.GetOrCompileBody(fdn)
                : null;
            if (compiled == null)
                return res.Failure(new RuntimeError(PositionStart, PositionEnd,
                    $"extension method '{Name}' has no IR-compiled body", Context));
            {
                var vm = new Vm.VmExecutor(interpreter);
                var frame = Vm.VmFrame.Rent(compiled);
                bodyRes = await vm.Execute(frame, bind.execCtx!);
                if (bodyRes.Error == null) Vm.VmFrame.Return(frame);
            }
            if (bodyRes.Error != null) return res.Failure(bodyRes.Error);

            if (bodyRes.FuncReturnValue != null)
                return res.Success(bodyRes.FuncReturnValue);

            var retValue = selected.ShouldAutoReturn
                ? (bodyRes.Value ?? NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd))
                : NullValue.Null.SetContext(Context).SetPos(PositionStart, PositionEnd);

            return res.Success(retValue);
        }

        private static string DescribeEntry(ExtensionMethodEntry e)
        {
            var mod = string.IsNullOrEmpty(e.DeclaringModule) ? "<this module>" : e.DeclaringModule;
            var argsStr = string.Join(", ", e.Method.ArgNameToks.Select(t => t.Value?.ToString() ?? "_"));
            return $"{e.Method.VarNameTok?.Value}({argsStr}) from {mod}";
        }

        public override RuntimeValue Copy()
        {
            var copy = Entries != null
                ? new BoundExtensionMethodGroupValue(Receiver, Entries)
                : new BoundExtensionMethodGroupValue(Receiver, Candidates);
            return copy.SetContext(Context).SetPos(PositionStart, PositionEnd);
        }

        public override string ToString() => $"<extension {Name}>";
    }
}

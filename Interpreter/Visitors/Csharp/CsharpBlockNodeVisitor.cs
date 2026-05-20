using System;
using System.Text;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Csharp;
using RaLanguage.Interpreter.Values;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Csharp;

namespace RaLanguage.Interpreter.Visitors.Csharp
{
    /// <summary>
    /// Visits inline <c>csharp { ... }</c> and <c>csharp -&gt; T { ... }</c> blocks.
    ///
    /// Interpolation modes:
    ///   %{expr}            — automatic literal substitution (int/long/double/bool/string/null/list)
    ///   %{expr:raw}        — emit the value's string form unquoted (templating)
    ///   %{expr:str}        — force a quoted C# string literal
    ///   %{expr:int|long|uint|ulong|float|double|decimal|bool|char} — coerce to a typed literal
    ///
    /// The resulting C# source is compiled by Roslyn, cached, and executed. The return value
    /// is marshalled back into a Ra <see cref="RuntimeValue"/>; if the block declared a return
    /// type (<c>csharp -&gt; int { ... }</c>) the marshaller coerces the value to that shape,
    /// otherwise it converts freely based on the CLR type of the script return value.
    /// </summary>
    public sealed class CsharpBlockNodeVisitor : NodeVisitor<CsharpBlockNode>
    {
        protected sealed override RuntimeResult VisitNode(CsharpBlockNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            var sb = new StringBuilder();
            for (int i = 0; i < node.Parts.Count; i++)
            {
                var part = node.Parts[i];

                if (part is CsharpTextPartNode text)
                {
                    sb.Append(text.Text);
                    continue;
                }

                string? typeHint = null;
                AstNode evalPart = part;
                if (part is CsharpInterpPartNode ip)
                {
                    typeHint = ip.TypeHint;
                    evalPart = ip.Expr;
                }

                var val = res.Register(interpreter.Visit(evalPart, context));
                if (res.ShouldReturn()) return res;
                if (val == null)
                {
                    return res.Failure(new RuntimeError(part.PositionStart, part.PositionEnd,
                        "csharp %{...} interpolation produced a null value",
                        context,
                        code: DiagnosticCode.RuntimeGeneric,
                        primaryLabel: "no value produced by the interpolated expression"));
                }

                if (!CsharpInteropMarshaller.TryFormatLiteral(val, typeHint, out string formatted, out string? interpErr))
                {
                    return res.Failure(new RuntimeError(part.PositionStart, part.PositionEnd,
                        $"csharp %{{...}}: {interpErr}",
                        context,
                        code: DiagnosticCode.RuntimeTypeMismatch,
                        primaryLabel: "value not usable in this interpolation slot",
                        help: "consider adding a type hint like %{x:str}, %{x:int}, or %{x:raw}"));
                }
                sb.Append(formatted);
            }

            string source = sb.ToString();
            var options = new CsharpExecutionOptions(source, node.Usings, node.References, node.ReturnType);

            var (value, error, diagnostics) = CsharpExecutor.Execute(options);

            if (error != null)
            {
                if (error is CsharpCompileException cce)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"csharp compile error: {string.Join(" | ", cce.Diagnostics)}",
                        context,
                        code: DiagnosticCode.RuntimeGeneric,
                        primaryLabel: "compilation of the csharp block failed",
                        help: "fix the C# diagnostic(s) above; types from extra namespaces need 'using <ns>' on the csharp header"));
                }
                if (error is CsharpRuntimeException cre)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        $"csharp runtime error: {cre.Message}",
                        context,
                        code: DiagnosticCode.RuntimeNativeException,
                        primaryLabel: "the inline csharp block threw an exception"));
                }
                if (error is CsharpUnsupportedException cue)
                {
                    return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                        cue.Message,
                        context,
                        code: DiagnosticCode.RuntimeGeneric,
                        primaryLabel: "dynamic code unsupported here"));
                }
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"csharp error: {error.Message}",
                    context,
                    code: DiagnosticCode.RuntimeGeneric));
            }

            var runtimeValue = CsharpInteropMarshaller.ToRuntimeValue(value, node.ReturnType);
            return res.Success(runtimeValue.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

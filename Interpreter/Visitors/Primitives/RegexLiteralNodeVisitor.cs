using System.Text.RegularExpressions;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Architecture;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes.Primitives;

namespace RaLanguage.Interpreter.Visitors.Primitives
{
    // Compiles a regex literal at most once per AST node. The CachedValue
    // field on RegexLiteralNode means a `re"..."` sitting inside a hot loop
    // pays the (pattern, options) compile cost on the first iteration only;
    // every subsequent visit hits the cache and returns the same instance.
    public class RegexLiteralNodeVisitor : NodeVisitor<RegexLiteralNode>
    {
        protected sealed override RuntimeResult VisitNode(RegexLiteralNode node, Context context, IInterpreter interpreter)
        {
            var res = new RuntimeResult();

            if (node.CachedValue != null)
            {
                return res.Success(node.CachedValue.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
            }

            RegexOptions options;
            try
            {
                options = RegexValue.ParseFlags(node.Flags);
            }
            catch (System.ArgumentException ex)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    ex.Message,
                    context,
                    code: DiagnosticCode.RuntimeRegexCompile,
                    primaryLabel: "invalid flag suffix",
                    help: "regex literal flags must be a subset of i, m, s, x, n"));
            }

            Regex regex;
            try
            {
                regex = RegexValue.Compile(node.Pattern, options);
            }
            catch (System.ArgumentException ex)
            {
                return res.Failure(new RuntimeError(node.PositionStart, node.PositionEnd,
                    $"invalid regex pattern: {ex.Message}",
                    context,
                    code: DiagnosticCode.RuntimeRegexCompile,
                    primaryLabel: "pattern rejected by the regex engine",
                    help: "check escaping, group syntax, and quantifier placement"));
            }

            var value = new RegexValue(node.Pattern, node.Flags, options, regex);
            node.CachedValue = value;
            return res.Success(value.SetContext(context).SetPos(node.PositionStart, node.PositionEnd));
        }
    }
}

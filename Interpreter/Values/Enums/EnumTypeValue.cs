using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Parser.Nodes.Special;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class EnumTypeValue : RuntimeValue
    {
        public string EnumName { get; }
        public IReadOnlyList<EnumVariantInfo> Variants { get; }
        public IReadOnlyDictionary<string, EnumVariantInfo> VariantsByName { get; }
        public List<string> GenericTypeParams { get; }
        public List<WhereConstraintNode> WhereConstraints { get; }

        public sealed override RuntimeValueType Type => RuntimeValueType.EnumType;

        public EnumTypeValue(
            string enumName,
            IReadOnlyList<EnumVariantInfo> variants,
            List<string>? genericTypeParams = null,
            List<WhereConstraintNode>? whereConstraints = null)
        {
            EnumName = enumName;
            Variants = variants;
            GenericTypeParams = genericTypeParams ?? new List<string>();
            WhereConstraints = whereConstraints ?? new List<WhereConstraintNode>();

            var byName = new Dictionary<string, EnumVariantInfo>(variants.Count, StringComparer.Ordinal);
            foreach (var v in variants) byName[v.Name] = v;
            VariantsByName = byName;
        }

        public bool HasMember(string name) => VariantsByName.ContainsKey(name);

        public bool TryGetVariant(string name, out EnumVariantInfo info)
        {
            if (VariantsByName.TryGetValue(name, out var v)) { info = v; return true; }
            info = null!;
            return false;
        }

        // Resolves `EnumType.Member`:
        //   * zero-arity variant → fully-constructed EnumValue
        //   * payload variant    → EnumVariantConstructor (callable)
        public RuntimeValue GetMember(string memberName)
        {
            if (!VariantsByName.TryGetValue(memberName, out var info))
            {
                throw new InvalidOperationException($"Enum '{EnumName}' has no variant '{memberName}'");
            }

            if (!info.HasPayload)
            {
                return new EnumValue(EnumName, memberName, info.Index, info.UnderlyingValue)
                    .SetContext(Context)
                    .SetPos(PositionStart, PositionEnd);
            }

            return new Functions.EnumVariantConstructor(this, info)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public sealed override RuntimeValue Copy()
        {
            return new EnumTypeValue(EnumName, Variants, GenericTypeParams, WhereConstraints)
                .SetContext(Context)
                .SetPos(PositionStart, PositionEnd);
        }

        public sealed override string ToString() => $"<enum {EnumName}>";
    }
}

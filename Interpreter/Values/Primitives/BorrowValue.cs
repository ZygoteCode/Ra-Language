using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    // Runtime representation of an active borrow produced by `&x` or `&mut x`.
    //
    // Holds a back-pointer to the SymbolEntry of the borrowed binding so it can:
    //   * read / write through the borrow (the latter only when IsMutableBorrow=true);
    //   * decrement the shared / mutable borrow counter on Release();
    //   * report use-after-free if the source entry has been dropped from its table.
    //
    // BorrowValue is intentionally NOT IsCopy (it must be moved by the existing
    // let-move machinery when assigned/passed). Release() is idempotent.
    public sealed class BorrowValue : RuntimeValue, IReferenceValue
    {
        public SymbolEntry SourceEntry { get; }
        public SymbolTable SourceTable { get; }
        public string SourceName { get; }
        public bool IsMutableBorrow { get; }
        public string? Lifetime { get; }
        public bool Released { get; private set; }

        public override RuntimeValueType Type => RuntimeValueType.Reference;
        public override bool IsCopy => false;

        public BorrowValue(SymbolEntry source, SymbolTable sourceTable, string sourceName, bool isMutable, string? lifetime = null)
        {
            SourceEntry = source;
            SourceTable = sourceTable;
            SourceName = sourceName;
            IsMutableBorrow = isMutable;
            Lifetime = lifetime;
        }

        public RuntimeValue Value
        {
            get
            {
                if (Released)
                    throw new InvalidOperationException($"borrow of '{SourceName}' has been released (use-after-free)");
                if (SourceEntry.IsMoved)
                    throw new InvalidOperationException($"borrow of '{SourceName}' is invalid: source was moved");
                return SourceEntry.Value;
            }
            set
            {
                if (Released)
                    throw new InvalidOperationException($"borrow of '{SourceName}' has been released (use-after-free)");
                if (!IsMutableBorrow)
                    throw new InvalidOperationException($"cannot assign through shared borrow of '{SourceName}': &T is read-only, use &mut to mutate");
                if (SourceEntry.IsConstBinding)
                    throw new InvalidOperationException($"cannot assign through borrow of '{SourceName}': source binding is const");
                SourceEntry.Value = value;
            }
        }

        // Decrements the source entry's borrow counter. Safe to call twice.
        public void Release()
        {
            if (Released) return;
            Released = true;
            if (IsMutableBorrow)
            {
                SourceEntry.HasMutableBorrow = false;
            }
            else if (SourceEntry.SharedBorrowCount > 0)
            {
                SourceEntry.SharedBorrowCount--;
            }
        }

        public override RuntimeValue Copy()
        {
            // Borrows are NOT freely duplicated; existing assignment / argument-passing
            // semantics will treat this as a non-copy move. We return `this` rather than
            // a clone so counters do not desync.
            return this;
        }

        public override string ToString()
        {
            try
            {
                string prefix = IsMutableBorrow ? "&mut " : "&";
                return $"{prefix}{SourceName}={Value}";
            }
            catch
            {
                return $"{(IsMutableBorrow ? "&mut " : "&")}{SourceName}=<invalid>";
            }
        }

        public override ValueResult AddedTo(RuntimeValue other) => SafeValue()?.AddedTo(other) ?? Err();
        public override ValueResult SubbedBy(RuntimeValue other) => SafeValue()?.SubbedBy(other) ?? Err();
        public override ValueResult MultedBy(RuntimeValue other) => SafeValue()?.MultedBy(other) ?? Err();
        public override ValueResult DivedBy(RuntimeValue other) => SafeValue()?.DivedBy(other) ?? Err();
        public override ValueResult PowedBy(RuntimeValue other) => SafeValue()?.PowedBy(other) ?? Err();
        public override ValueResult ModuledBy(RuntimeValue other) => SafeValue()?.ModuledBy(other) ?? Err();
        public override ValueResult BitwiseLeftShiftedBy(RuntimeValue other) => SafeValue()?.BitwiseLeftShiftedBy(other) ?? Err();
        public override ValueResult BitwiseRightShiftedBy(RuntimeValue other) => SafeValue()?.BitwiseRightShiftedBy(other) ?? Err();
        public override ValueResult BitwiseAndedBy(RuntimeValue other) => SafeValue()?.BitwiseAndedBy(other) ?? Err();
        public override ValueResult BitwiseOredBy(RuntimeValue other) => SafeValue()?.BitwiseOredBy(other) ?? Err();
        public override ValueResult ListAccess(RuntimeValue other) => SafeValue()?.ListAccess(other) ?? Err();
        public override ValueResult GetComparisonEq(RuntimeValue other) => SafeValue()?.GetComparisonEq(other) ?? Err();
        public override ValueResult GetComparisonNe(RuntimeValue other) => SafeValue()?.GetComparisonNe(other) ?? Err();
        public override ValueResult GetComparisonStrictEq(RuntimeValue other) => SafeValue()?.GetComparisonStrictEq(other) ?? Err();
        public override ValueResult GetComparisonStrictNe(RuntimeValue other) => SafeValue()?.GetComparisonStrictNe(other) ?? Err();
        public override ValueResult GetComparisonLt(RuntimeValue other) => SafeValue()?.GetComparisonLt(other) ?? Err();
        public override ValueResult GetComparisonGt(RuntimeValue other) => SafeValue()?.GetComparisonGt(other) ?? Err();
        public override ValueResult GetComparisonLte(RuntimeValue other) => SafeValue()?.GetComparisonLte(other) ?? Err();
        public override ValueResult GetComparisonGte(RuntimeValue other) => SafeValue()?.GetComparisonGte(other) ?? Err();
        public override ValueResult Notted() => SafeValue()?.Notted() ?? Err();
        public override ValueResult BitwiseNotted() => SafeValue()?.BitwiseNotted() ?? Err();
        public override ValueResult Factorial() => SafeValue()?.Factorial() ?? Err();
        public override ValueResult AndedBy(RuntimeValue other) => SafeValue()?.AndedBy(other) ?? Err();
        public override ValueResult OredBy(RuntimeValue other) => SafeValue()?.OredBy(other) ?? Err();
        public override ValueResult InCollection(RuntimeValue other) => SafeValue()?.InCollection(other) ?? Err();
        public override bool IsTrue()
        {
            var v = SafeValue();
            return v != null && v.IsTrue();
        }

        private RuntimeValue? SafeValue()
        {
            if (Released || SourceEntry.IsMoved) return null;
            return SourceEntry.Value;
        }

        private ValueResult Err()
        {
            string reason = Released ? "released borrow" : "borrow of moved value";
            return (null, new RuntimeError(PositionStart, PositionEnd,
                $"invalid borrow access on '{SourceName}' ({reason})",
                Context,
                code: DiagnosticCode.RuntimeMovedValue,
                primaryLabel: "borrow no longer points to a live value",
                help: "the borrowed binding was moved or its scope exited; create a fresh borrow within a valid lifetime"));
        }
    }
}

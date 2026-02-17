using RaLanguage.Errors;
using RaLanguage.Errors.Types;

namespace RaLanguage.Interpreter.Values.Primitives
{
    public class ListVal : RuntimeValue
    {
        public List<RuntimeValue> Elements { get; }
        public ListVal(List<RuntimeValue> elements) { Elements = elements; }

        public override (RuntimeValue?, Error?) AddedTo(RuntimeValue other)
        {
            var newList = (ListVal)Copy();
            newList.Elements.Add(other);
            return (newList, null);
        }

        public override (RuntimeValue?, Error?) SubbedBy(RuntimeValue other)
        {
            if (other is Number n)
            {
                var newList = (ListVal)Copy();
                try
                {
                    int idx = (int)n.Value;
                    if (idx < 0 || idx >= newList.Elements.Count) throw new IndexOutOfRangeException();
                    newList.Elements.RemoveAt(idx);
                    return (newList, null);
                }
                catch
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Element at this index could not be removed from list because index is out of bounds", Context));
                }
            }
            return base.SubbedBy(other);
        }

        public override (RuntimeValue?, Error?) MultedBy(RuntimeValue other)
        {
            if (other is ListVal l)
            {
                var newList = (ListVal)Copy();
                newList.Elements.AddRange(l.Elements);
                return (newList, null);
            }
            return base.MultedBy(other);
        }

        public override (RuntimeValue?, Error?) DivedBy(RuntimeValue other)
        {
            if (other is Number n)
            {
                try
                {
                    int idx = (int)n.Value;
                    if (idx < 0 || idx >= Elements.Count) throw new IndexOutOfRangeException();
                    return (Elements[idx], null);
                }
                catch
                {
                    return (null, new RuntimeError(other.PositionStart, other.PositionEnd, "Element at this index could not be retrieved from list because index is out of bounds", Context));
                }
            }
            return base.DivedBy(other);
        }

        public override RuntimeValue Copy()
        {
            return new ListVal(new List<RuntimeValue>(Elements)).SetPos(PositionStart, PositionEnd).SetContext(Context);
        }

        public override string ToString() => "[" + string.Join(", ", Elements.Select(e =>
            e is StringVal s ? s.ToRepr() : e.ToString())) + "]";
    }
}
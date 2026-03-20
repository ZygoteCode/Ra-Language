using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Parser.Nodes;

namespace RaLanguage.Types
{
    public class TypeChecker
    {
        public static RuntimeValue? GetNewType(TypeDescriptor? declaredType, RuntimeValue value, Context context, AstNode node)
        {
            if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "long", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "i64", StringComparison.Ordinal)) && value.Type == RuntimeValueType.Integer)
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new LongValue((long)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new LongValue((long)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new LongValue((long)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new LongValue((long)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new LongValue((long)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new LongValue((long)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new LongValue((long)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new LongValue((long)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new LongValue((long)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new LongValue((long)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new LongValue((long)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new LongValue((long)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!long.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new LongValue((long)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "float", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "f32", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new FloatValue((float)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new FloatValue((float)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new FloatValue((float)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new FloatValue((float)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new FloatValue((float)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new FloatValue((float)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new FloatValue((float)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new FloatValue((uint)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new FloatValue((float)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new FloatValue((float)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new FloatValue((float)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new FloatValue((float)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!float.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new FloatValue((float)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "double", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "f64", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new DoubleValue((double)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new DoubleValue((double)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new DoubleValue((double)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new DoubleValue((double)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new DoubleValue((double)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new DoubleValue((double)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new DoubleValue((double)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new DoubleValue((uint)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new DoubleValue((double)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new DoubleValue((double)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new DoubleValue((double)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new DoubleValue((double)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!double.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new DoubleValue((double)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "uint", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "ui32", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "unsignedinteger", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new UnsignedIntegerValue((uint)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new UnsignedIntegerValue((uint)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new UnsignedIntegerValue((uint)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new UnsignedIntegerValue((uint)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new UnsignedIntegerValue((uint)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new UnsignedIntegerValue((uint)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new UnsignedIntegerValue((uint)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new UnsignedIntegerValue((uint)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new UnsignedIntegerValue((uint)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new UnsignedIntegerValue((uint)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new UnsignedIntegerValue((uint)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new UnsignedIntegerValue((uint)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!uint.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new UnsignedIntegerValue((uint)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "ulong", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "ui64", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "unsignedlong", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new UnsignedLongValue((ulong)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new UnsignedLongValue((ulong)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new UnsignedLongValue((ulong)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new UnsignedLongValue((ulong)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new UnsignedLongValue((ulong)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new UnsignedLongValue((ulong)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new UnsignedLongValue((ulong)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new UnsignedLongValue((ulong)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new UnsignedLongValue((ulong)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new UnsignedLongValue((ulong)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new UnsignedLongValue((ulong)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new UnsignedLongValue((ulong)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!ulong.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new UnsignedLongValue((ulong)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "i16", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "short", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "int16", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new ShortValue((short)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new ShortValue((short)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new ShortValue((short)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new ShortValue((short)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new ShortValue((short)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new ShortValue((short)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new ShortValue((short)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new ShortValue((short)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new ShortValue((short)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new ShortValue((short)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new ShortValue((short)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new ShortValue((short)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!short.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new ShortValue((short)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "ushort", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "ui16", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "uint16", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new UnsignedShortValue((ushort)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new UnsignedShortValue((ushort)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new UnsignedShortValue((ushort)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new UnsignedShortValue((ushort)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new UnsignedShortValue((ushort)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new UnsignedShortValue((ushort)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new UnsignedShortValue((ushort)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new UnsignedShortValue((ushort)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new UnsignedShortValue((ushort)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new UnsignedShortValue((ushort)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new UnsignedShortValue((ushort)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new UnsignedShortValue((ushort)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!ushort.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new UnsignedShortValue((ushort)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "int128", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "i128", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "integer128", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new Int128Value((Int128)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new Int128Value((Int128)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new Int128Value((Int128)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new Int128Value((Int128)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new Int128Value((Int128)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new Int128Value((Int128)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new Int128Value((Int128)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new Int128Value((Int128)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new Int128Value((Int128)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new Int128Value((Int128)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new Int128Value((Int128)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new Int128Value((Int128)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!Int128.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new Int128Value((Int128)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "uint128", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "ui128", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "unsignedinteger128", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new UnsignedInt128Value((UInt128)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new UnsignedInt128Value((UInt128)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new UnsignedInt128Value((UInt128)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new UnsignedInt128Value((UInt128)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new UnsignedInt128Value((UInt128)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new UnsignedInt128Value((UInt128)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new UnsignedInt128Value((UInt128)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new UnsignedInt128Value((UInt128)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new UnsignedInt128Value((UInt128)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new UnsignedInt128Value((UInt128)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new UnsignedInt128Value((UInt128)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new UnsignedInt128Value((UInt128)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!UInt128.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new UnsignedInt128Value((UInt128)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "decimal", StringComparison.Ordinal) || string.Equals(declaredType.Name?.ToString(), "f128", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new DecimalValue((decimal)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new DecimalValue((decimal)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new DecimalValue((decimal)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new DecimalValue((decimal)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new DecimalValue((decimal)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new DecimalValue((decimal)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new DecimalValue((decimal)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new DecimalValue((decimal)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new DecimalValue((decimal)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new DecimalValue((decimal)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new DecimalValue((decimal)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new DecimalValue((decimal)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!decimal.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new DecimalValue((decimal)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }
            else if (declaredType != null && (string.Equals(declaredType.Name?.ToString(), "byte", StringComparison.Ordinal)))
            {
                if (value.Type == RuntimeValueType.Integer)
                {
                    value = new ByteValue((byte)((IntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Long)
                {
                    value = new ByteValue((byte)((LongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Float)
                {
                    value = new ByteValue((byte)((FloatValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Double)
                {
                    value = new ByteValue((byte)((DoubleValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInteger)
                {
                    value = new ByteValue((byte)((UnsignedIntegerValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedLong)
                {
                    value = new ByteValue((byte)((UnsignedLongValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedShort)
                {
                    value = new ByteValue((byte)((UnsignedShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Short)
                {
                    value = new ByteValue((byte)((ShortValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Int128)
                {
                    value = new ByteValue((byte)((Int128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.UnsignedInt128)
                {
                    value = new ByteValue((byte)((UnsignedInt128Value)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Decimal)
                {
                    value = new ByteValue((byte)((DecimalValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Byte)
                {
                    value = new ByteValue((byte)((ByteValue)value).Value).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
                else if (value.Type == RuntimeValueType.Number)
                {
                    var n = (NumberValue)value;

                    if (!byte.TryParse(n.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        return null;
                    }

                    value = new ByteValue((byte)d).SetContext(context).SetPos(node.PositionStart, node.PositionEnd);
                }
            }

            return value;
        }
    }
}
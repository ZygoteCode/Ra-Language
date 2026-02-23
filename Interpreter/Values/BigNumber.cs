using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace RaLanguage.Interpreter.Values
{
    public readonly struct BigNumber : IComparable<BigNumber>
    {
        public BigInteger Unscaled { get; }
        public BigInteger Scale { get; }

        static readonly ConcurrentDictionary<BigInteger, BigInteger> TenPowCache = new();

        static BigNumber()
        {
            TenPowCache[BigInteger.Zero] = BigInteger.One;
            TenPowCache[BigInteger.One] = new BigInteger(10);
        }

        public BigNumber(BigInteger unscaled, BigInteger scale)
        {
            if (scale < 0) throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be >= 0");
            Unscaled = unscaled;
            Scale = scale;
        }

        public static BigNumber Zero => new BigNumber(BigInteger.Zero, BigInteger.Zero);
        public static BigNumber One => new BigNumber(BigInteger.One, BigInteger.Zero);

        public static BigNumber Parse(string s)
        {
            if (s is null) throw new ArgumentNullException(nameof(s));
            s = s.Trim();
            if (s.Length == 0) throw new FormatException("Empty string");

            int idx = 0;
            int len = s.Length;

            int sign = 1;
            if (s[idx] == '+' || s[idx] == '-')
            {
                if (s[idx] == '-') sign = -1;
                idx++;
                if (idx >= len) throw new FormatException("Invalid number");
            }

            int expPos = -1;
            for (int i = idx; i < len; i++)
            {
                char c = s[i];
                if (c == 'e' || c == 'E')
                {
                    expPos = i;
                    break;
                }
            }

            string mantissaStr;
            string exponentStr = null;
            if (expPos >= 0)
            {
                mantissaStr = s.Substring(idx, expPos - idx);
                exponentStr = s.Substring(expPos + 1);
            }
            else
            {
                mantissaStr = s.Substring(idx);
            }

            if (string.IsNullOrWhiteSpace(mantissaStr)) throw new FormatException("Missing mantissa");

            BigInteger exponent = BigInteger.Zero;
            if (!string.IsNullOrEmpty(exponentStr))
            {
                exponentStr = exponentStr.Trim();
                if (exponentStr.Length == 0) throw new FormatException("Invalid exponent");
                int eidx = 0;
                int esign = 1;
                if (exponentStr[0] == '+' || exponentStr[0] == '-')
                {
                    if (exponentStr[0] == '-') esign = -1;
                    eidx++;
                    if (eidx >= exponentStr.Length) throw new FormatException("Invalid exponent");
                }

                var sbExp = new StringBuilder();
                for (; eidx < exponentStr.Length; eidx++)
                {
                    char c = exponentStr[eidx];
                    if (c == '_') continue;
                    if (!char.IsDigit(c)) throw new FormatException($"Invalid character in exponent: {c}");
                    sbExp.Append(c);
                }

                if (sbExp.Length == 0) throw new FormatException("Invalid exponent digits");
                exponent = BigInteger.Parse(sbExp.ToString(), CultureInfo.InvariantCulture) * esign;
            }

            var sbDigits = new StringBuilder();
            int digitsAfterDot = 0;
            bool seenDot = false;
            foreach (char ch in mantissaStr)
            {
                if (ch == '_') continue;
                if (ch == '.')
                {
                    if (seenDot) throw new FormatException("Multiple decimal points");
                    seenDot = true;
                    continue;
                }
                if (!char.IsDigit(ch)) throw new FormatException($"Invalid character in number: {ch}");
                sbDigits.Append(ch);
                if (seenDot) digitsAfterDot++;
            }

            if (sbDigits.Length == 0) throw new FormatException("No digits in number");

            int firstNonZeroIdx = 0;
            while (firstNonZeroIdx < sbDigits.Length && sbDigits[firstNonZeroIdx] == '0') firstNonZeroIdx++;
            string digitStr;
            int trimmedLeadingZeros = firstNonZeroIdx;
            if (firstNonZeroIdx == sbDigits.Length)
            {
                digitStr = "0";
            }
            else
            {
                digitStr = sbDigits.ToString(firstNonZeroIdx, sbDigits.Length - firstNonZeroIdx);
            }

            BigInteger unscaled = BigInteger.Parse(digitStr, CultureInfo.InvariantCulture) * sign;
            BigInteger scale = new BigInteger(digitsAfterDot);

            BigInteger scaleAdjusted = scale - exponent;
            if (scaleAdjusted < 0)
            {
                BigInteger mul = TenPow(BigInteger.Negate(scaleAdjusted));
                unscaled *= mul;
                scale = BigInteger.Zero;
            }
            else
            {
                scale = scaleAdjusted;
            }

            return new BigNumber(unscaled, scale).Normalize();
        }

        // ---------------------- ToString ----------------------
        public override string ToString()
        {
            if (Unscaled.IsZero) return "0";
            BigInteger absUnscaled = BigInteger.Abs(Unscaled);
            string s = absUnscaled.ToString(CultureInfo.InvariantCulture);

            if (Scale.IsZero) return (Unscaled.Sign < 0 ? "-" : "") + s;

            if (Scale <= (BigInteger)int.MaxValue)
            {
                int scaleInt = (int)Scale;
                if (s.Length <= scaleInt)
                {
                    var left = "0";
                    var frac = s.PadLeft(scaleInt, '0');
                    int fracLen = frac.Length;
                    while (fracLen > 0 && frac[fracLen - 1] == '0') fracLen--;
                    frac = frac.Substring(0, fracLen);
                    if (fracLen == 0) return (Unscaled.Sign < 0 ? "-" : "") + left;
                    return (Unscaled.Sign < 0 ? "-" : "") + left + "." + frac;
                }
                else
                {
                    int intLen = s.Length - scaleInt;
                    string intPart = s.Substring(0, intLen);
                    string fracPart = s.Substring(intLen);
                    int fracLen = fracPart.Length;
                    while (fracLen > 0 && fracPart[fracLen - 1] == '0') fracLen--;
                    fracPart = fracPart.Substring(0, fracLen);
                    if (fracPart.Length == 0) return (Unscaled.Sign < 0 ? "-" : "") + intPart;
                    return (Unscaled.Sign < 0 ? "-" : "") + intPart + "." + fracPart;
                }
            }
            else
            {
                return $"{(Unscaled.Sign < 0 ? "-" : "")}{absUnscaled}e-{Scale}";
            }
        }

        public BigNumber Normalize()
        {
            if (Unscaled.IsZero) return Zero;

            BigInteger u = Unscaled;
            BigInteger s = Scale;

            while (s > BigInteger.Zero)
            {
                BigInteger rem = u % 10;
                if (rem != 0) break;
                u /= 10;
                s -= 1;
            }
            return new BigNumber(u, s);
        }

        private static BigInteger TenPow(BigInteger exp)
        {
            if (exp < 0) throw new ArgumentOutOfRangeException(nameof(exp));
            if (TenPowCache.TryGetValue(exp, out var cached)) return cached;

            BigInteger result = BigInteger.One;
            BigInteger baseVal = new BigInteger(10);
            BigInteger e = exp;

            BigInteger bestKey = BigInteger.MinusOne;
            foreach (var key in TenPowCache.Keys)
            {
                if (key <= exp && key > bestKey) bestKey = key;
            }
            if (bestKey >= BigInteger.Zero && TenPowCache.TryGetValue(bestKey, out var bestVal))
            {
                result = bestVal;
                e = exp - bestKey;
            }

            BigInteger curr = baseVal;
            while (e > 0)
            {
                if (!e.IsEven) result *= curr;
                e /= 2;
                if (e > 0) curr *= curr;
            }

            TenPowCache.TryAdd(exp, result);
            return result;
        }

        private static (BigInteger aU, BigInteger bU, BigInteger scale) AlignScales(in BigNumber a, in BigNumber b)
        {
            if (a.Scale == b.Scale) return (a.Unscaled, b.Unscaled, a.Scale);
            if (a.Scale > b.Scale)
            {
                BigInteger diff = a.Scale - b.Scale;
                BigInteger mul = TenPow(diff);
                return (a.Unscaled, b.Unscaled * mul, a.Scale);
            }
            else
            {
                BigInteger diff = b.Scale - a.Scale;
                BigInteger mul = TenPow(diff);
                return (a.Unscaled * mul, b.Unscaled, b.Scale);
            }
        }

        public static BigNumber operator +(in BigNumber a, in BigNumber b)
        {
            var (au, bu, s) = AlignScales(a, b);
            var res = au + bu;
            return new BigNumber(res, s).Normalize();
        }

        public static BigNumber operator -(in BigNumber a, in BigNumber b)
        {
            var (au, bu, s) = AlignScales(a, b);
            var res = au - bu;
            return new BigNumber(res, s).Normalize();
        }

        public static BigNumber operator *(in BigNumber a, in BigNumber b)
        {
            var res = a.Unscaled * b.Unscaled;
            var s = a.Scale + b.Scale;
            return new BigNumber(res, s).Normalize();
        }

        public BigNumber DivideWithPrecision(in BigNumber other, int precision = 50)
        {
            if (other.Unscaled.IsZero) throw new DivideByZeroException();
            BigInteger precisionPow = TenPow(precision);
            BigInteger numerator = this.Unscaled * precisionPow;
            BigInteger quotient = BigInteger.DivRem(numerator, other.Unscaled, out var remainder);
            BigInteger resultScale = this.Scale - other.Scale + precision;
            var result = new BigNumber(quotient, resultScale).Normalize();
            if (remainder.IsZero) return result.Normalize();
            return result;
        }

        public static BigNumber operator /(in BigNumber a, in BigNumber b)
        {
            if (b.Unscaled.IsZero) throw new DivideByZeroException();
            var (au, bu, s) = AlignScales(a, b);
            var q = BigInteger.DivRem(au, bu, out var r);
            if (r.IsZero)
            {
                return new BigNumber(q, s).Normalize();
            }
            return a.DivideWithPrecision(b, precision: 50);
        }

        public BigNumber Pow(in BigNumber exponent, int precision = 50)
        {
            if (exponent.Scale.IsZero)
            {
                BigInteger exp = exponent.Unscaled;
                bool negativeExp = exp.Sign < 0;
                if (negativeExp) exp = BigInteger.Abs(exp);

                BigInteger resultUnscaled = BigInteger.One;
                BigInteger baseUnscaled = this.Unscaled;
                BigInteger currentExp = exp;

                while (currentExp > BigInteger.Zero)
                {
                    if (!currentExp.IsEven) resultUnscaled *= baseUnscaled;
                    baseUnscaled *= baseUnscaled;
                    currentExp >>= 1;
                }

                BigInteger finalScale = this.Scale * exp;
                var result = new BigNumber(resultUnscaled, finalScale).Normalize();

                if (negativeExp)
                {
                    return One.DivideWithPrecision(result, precision);
                }

                return result;
            }

            double x = ToDouble();
            double y = exponent.ToDouble();
            return FromDouble(Math.Pow(x, y));
        }

        public int CompareTo(BigNumber other)
        {
            var (au, bu, s) = AlignScales(this, other);
            return au.CompareTo(bu);
        }

        public static bool operator ==(in BigNumber a, in BigNumber b) => a.CompareTo(b) == 0;
        public static bool operator !=(in BigNumber a, in BigNumber b) => a.CompareTo(b) != 0;
        public static bool operator <(in BigNumber a, in BigNumber b) => a.CompareTo(b) < 0;
        public static bool operator >(in BigNumber a, in BigNumber b) => a.CompareTo(b) > 0;
        public static bool operator <=(in BigNumber a, in BigNumber b) => a.CompareTo(b) <= 0;
        public static bool operator >=(in BigNumber a, in BigNumber b) => a.CompareTo(b) >= 0;

        public override bool Equals(object? obj) => obj is BigNumber bn && this == bn;
        public override int GetHashCode() => HashCode.Combine(Unscaled.GetHashCode(), Scale.GetHashCode());

        public BigInteger ToBigInteger()
        {
            if (Scale.IsZero) return Unscaled;
            BigInteger pow = TenPow(Scale);
            if (Unscaled.Sign >= 0)
            {
                return Unscaled / pow;
            }
            else
            {
                BigInteger abs = BigInteger.Abs(Unscaled);
                BigInteger ip = abs / pow;
                return -ip;
            }
        }

        public bool IsZero() => Unscaled.IsZero;

        public double ToDouble()
        {
            var s = ToString();
            return double.Parse(s, CultureInfo.InvariantCulture);
        }

        public static BigNumber FromDouble(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new OverflowException("Cannot convert NaN/Infinity to BigNumber");
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            return Parse(s);
        }

        public static BigNumber BitwiseNot(in BigNumber a)
        {
            var i = a.ToBigInteger();
            return new BigNumber(~i, BigInteger.Zero);
        }

        public static BigNumber BitwiseAnd(in BigNumber a, in BigNumber b)
        {
            var ai = a.ToBigInteger();
            var bi = b.ToBigInteger();
            return new BigNumber(ai & bi, BigInteger.Zero);
        }

        public static BigNumber BitwiseOr(in BigNumber a, in BigNumber b)
        {
            var ai = a.ToBigInteger();
            var bi = b.ToBigInteger();
            return new BigNumber(ai | bi, BigInteger.Zero);
        }

        public static BigNumber BitwiseXor(in BigNumber a, in BigNumber b)
        {
            var ai = a.ToBigInteger();
            var bi = b.ToBigInteger();
            return new BigNumber(ai ^ bi, BigInteger.Zero);
        }

        public static BigNumber LeftShift(in BigNumber a, in BigNumber b)
        {
            var ai = a.ToBigInteger();
            BigInteger shift = b.ToBigInteger();
            if (shift < 0) throw new ArgumentOutOfRangeException(nameof(b), "Negative shift");
            if (shift > int.MaxValue)
                shift = int.MaxValue;
            return new BigNumber(ai << (int)shift, BigInteger.Zero);
        }

        public static BigNumber RightShift(in BigNumber a, in BigNumber b)
        {
            var ai = a.ToBigInteger();
            BigInteger shift = b.ToBigInteger();
            if (shift < 0) throw new ArgumentOutOfRangeException(nameof(b), "Negative shift");
            if (shift > int.MaxValue)
                shift = int.MaxValue;
            return new BigNumber(ai >> (int)shift, BigInteger.Zero);
        }

        public static BigNumber Mod(in BigNumber a, in BigNumber b)
        {
            var ai = a.ToBigInteger();
            var bi = b.ToBigInteger();
            if (bi.IsZero) throw new DivideByZeroException();
            return new BigNumber(ai % bi, BigInteger.Zero);
        }

        public static implicit operator BigNumber(int v) => new BigNumber(new BigInteger(v), BigInteger.Zero);
        public static implicit operator BigNumber(long v) => new BigNumber(new BigInteger(v), BigInteger.Zero);
        public static implicit operator BigNumber(short v) => new BigNumber(new BigInteger(v), BigInteger.Zero);
        public static implicit operator BigNumber(byte v) => new BigNumber(new BigInteger(v), BigInteger.Zero);
        public static implicit operator BigNumber(ulong v) => new BigNumber(new BigInteger(v), BigInteger.Zero);
        public static implicit operator BigNumber(BigInteger v) => new BigNumber(v, BigInteger.Zero);

        public static implicit operator BigNumber(decimal d)
        {
            int[] bits = decimal.GetBits(d);
            int lo = bits[0];
            int mid = bits[1];
            int hi = bits[2];
            int flags = bits[3];
            bool negative = (flags & unchecked((int)0x80000000)) != 0;
            int scale = (flags >> 16) & 0xFF;

            BigInteger unscaled = ((BigInteger)(uint)hi << 64) | ((BigInteger)(uint)mid << 32) | (uint)lo;
            if (negative) unscaled = BigInteger.Negate(unscaled);
            return new BigNumber(unscaled, scale).Normalize();
        }

        public static explicit operator BigNumber(double d) => FromDouble(d);

        public static explicit operator double(BigNumber bn) => bn.ToDouble();

        public static explicit operator BigInteger(BigNumber bn) => bn.ToBigInteger();

        public static explicit operator int(BigNumber bn)
        {
            BigInteger i = bn.ToBigInteger();
            if (i < int.MinValue || i > int.MaxValue) throw new OverflowException("BigNumber to int overflow");
            return (int)i;
        }

        public static explicit operator long(BigNumber bn)
        {
            BigInteger i = bn.ToBigInteger();
            if (i < long.MinValue || i > long.MaxValue) throw new OverflowException("BigNumber to long overflow");
            return (long)i;
        }

        public static explicit operator decimal(BigNumber bn)
        {
            if (bn.Unscaled.IsZero) return decimal.Zero;

            BigInteger mant = bn.Unscaled;
            BigInteger scale = bn.Scale;
            if (scale < 0) throw new OverflowException("Negative scale for decimal conversion");

            if (scale > 28)
            {
                BigInteger diff = scale - 28;
                mant /= TenPow(diff);
                scale = 28;
            }

            BigInteger absMant = BigInteger.Abs(mant);
            BigInteger max96 = (BigInteger.One << 96) - 1;
            if (absMant > max96) throw new OverflowException("BigNumber magnitude too large for decimal");

            uint lo = (uint)(absMant & 0xFFFFFFFF);
            uint mid = (uint)((absMant >> 32) & 0xFFFFFFFF);
            uint hi = (uint)((absMant >> 64) & 0xFFFFFFFF);
            bool isNegative = mant.Sign < 0;
            byte scaleByte = (byte)(int)scale;

            return new decimal((int)lo, (int)mid, (int)hi, isNegative, scaleByte);
        }
    }
}
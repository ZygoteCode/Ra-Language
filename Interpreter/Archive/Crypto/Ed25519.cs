using System;
using System.Numerics;
using System.Security.Cryptography;

namespace RaLanguage.Interpreter.Archive.Crypto
{
    // Pure managed Ed25519 (RFC 8032) implementation. Vendored to avoid
    // a native libsodium dependency on NativeAOT builds. Uses BigInteger
    // for field / scalar arithmetic — clearer than a 10-limb fixed-width
    // port and trivially trimmer-safe. Signing speed is a few hundred
    // ops/sec on a desktop CPU; that is plenty for archive signing,
    // which signs at most once per build.
    //
    // Public surface (all sizes in bytes):
    //   PrivateKeySize = 32 (random seed)
    //   PublicKeySize  = 32 (compressed Edwards Y || sign(X))
    //   SignatureSize  = 64 (compressed R || encoded S)
    //
    //   GeneratePrivateKey(span)
    //   GetPublicKey(privateKey, span)
    //   Sign(message, privateKey, publicKey, signatureSpan)
    //   Verify(message, signature, publicKey) -> bool
    //
    // Verified against the RFC 8032 §7.1 test vectors at test time.
    //
    // SECURITY NOTE: scalar multiplication uses a double-and-add ladder,
    // which leaks the scalar's Hamming weight via timing. That is
    // acceptable for *archive signing* — a one-shot build-time
    // operation under the operator's control — but is NOT suitable for
    // signing in a multi-tenant server context. If the use-case grows
    // into long-lived online signing, swap this for a constant-time
    // ladder or move to a native library.
    public static class Ed25519
    {
        public const int PrivateKeySize = 32;
        public const int PublicKeySize = 32;
        public const int SignatureSize = 64;

        // p = 2^255 - 19
        private static readonly BigInteger P = (BigInteger.One << 255) - 19;
        // L = 2^252 + 27742317777372353535851937790883648493
        private static readonly BigInteger L =
            (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        // d = -121665 / 121666 mod p
        private static readonly BigInteger D = ModP(BigInteger.Parse("-121665") * InvP(121666));
        // i = sqrt(-1) mod p   used by point decoding
        private static readonly BigInteger I = PowP(2, (P - 1) / 4);
        // Base point B
        private static readonly Point B;

        static Ed25519()
        {
            BigInteger by = ModP(4 * InvP(5));
            BigInteger bx = RecoverX(by, 0);
            B = new Point(bx, by);
        }

        // === Public API ===

        public static void GeneratePrivateKey(Span<byte> destination)
        {
            if (destination.Length != PrivateKeySize)
                throw new ArgumentException($"private key buffer must be {PrivateKeySize} bytes", nameof(destination));
            RandomNumberGenerator.Fill(destination);
        }

        public static byte[] GeneratePrivateKey()
        {
            var key = new byte[PrivateKeySize];
            GeneratePrivateKey(key);
            return key;
        }

        public static void GetPublicKey(ReadOnlySpan<byte> privateKey, Span<byte> destination)
        {
            if (privateKey.Length != PrivateKeySize)
                throw new ArgumentException($"private key must be {PrivateKeySize} bytes", nameof(privateKey));
            if (destination.Length != PublicKeySize)
                throw new ArgumentException($"public key buffer must be {PublicKeySize} bytes", nameof(destination));

            Span<byte> h = stackalloc byte[64];
            SHA512.HashData(privateKey, h);
            ClampScalar(h.Slice(0, 32));
            BigInteger a = LeBytesToBigInt(h.Slice(0, 32));
            Point A = ScalarMult(B, a);
            EncodePoint(A, destination);
        }

        public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
        {
            var pub = new byte[PublicKeySize];
            GetPublicKey(privateKey, pub);
            return pub;
        }

        public static void Sign(ReadOnlySpan<byte> message, ReadOnlySpan<byte> privateKey,
            ReadOnlySpan<byte> publicKey, Span<byte> destination)
        {
            if (privateKey.Length != PrivateKeySize)
                throw new ArgumentException($"private key must be {PrivateKeySize} bytes", nameof(privateKey));
            if (publicKey.Length != PublicKeySize)
                throw new ArgumentException($"public key must be {PublicKeySize} bytes", nameof(publicKey));
            if (destination.Length != SignatureSize)
                throw new ArgumentException($"signature buffer must be {SignatureSize} bytes", nameof(destination));

            Span<byte> h = stackalloc byte[64];
            SHA512.HashData(privateKey, h);
            ClampScalar(h.Slice(0, 32));
            BigInteger a = LeBytesToBigInt(h.Slice(0, 32));
            var prefix = h.Slice(32, 32);

            byte[] rBuf;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
            {
                sha.AppendData(prefix);
                sha.AppendData(message);
                rBuf = sha.GetHashAndReset();
            }
            BigInteger r = ModL(LeBytesToBigInt(rBuf));
            Point R = ScalarMult(B, r);
            Span<byte> rEnc = stackalloc byte[32];
            EncodePoint(R, rEnc);

            byte[] kBuf;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
            {
                sha.AppendData(rEnc);
                sha.AppendData(publicKey);
                sha.AppendData(message);
                kBuf = sha.GetHashAndReset();
            }
            BigInteger k = ModL(LeBytesToBigInt(kBuf));
            BigInteger s = ModL(r + k * a);

            rEnc.CopyTo(destination.Slice(0, 32));
            BigIntToLeBytes(s, destination.Slice(32, 32));
        }

        public static byte[] Sign(ReadOnlySpan<byte> message, ReadOnlySpan<byte> privateKey,
            ReadOnlySpan<byte> publicKey)
        {
            var sig = new byte[SignatureSize];
            Sign(message, privateKey, publicKey, sig);
            return sig;
        }

        public static bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature,
            ReadOnlySpan<byte> publicKey)
        {
            if (signature.Length != SignatureSize) return false;
            if (publicKey.Length != PublicKeySize) return false;

            Point? A = DecodePoint(publicKey);
            if (A == null) return false;

            Point? R = DecodePoint(signature.Slice(0, 32));
            if (R == null) return false;

            BigInteger s = LeBytesToBigInt(signature.Slice(32, 32));
            if (s >= L) return false; // RFC 8032 strict bound

            byte[] kBuf;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
            {
                sha.AppendData(signature.Slice(0, 32));
                sha.AppendData(publicKey);
                sha.AppendData(message);
                kBuf = sha.GetHashAndReset();
            }
            BigInteger k = ModL(LeBytesToBigInt(kBuf));

            Point lhs = ScalarMult(B, s);
            Point rhs = PointAdd(R.Value, ScalarMult(A.Value, k));
            return PointsEqual(lhs, rhs);
        }

        // === Helpers ===

        private static void ClampScalar(Span<byte> h32)
        {
            // RFC 8032 §5.1.5
            h32[0] &= 248;
            h32[31] &= 127;
            h32[31] |= 64;
        }

        private static BigInteger LeBytesToBigInt(ReadOnlySpan<byte> b)
        {
            // Append a zero high byte so BigInteger always parses as positive.
            Span<byte> buf = stackalloc byte[b.Length + 1];
            b.CopyTo(buf);
            buf[b.Length] = 0;
            return new BigInteger(buf, isUnsigned: true, isBigEndian: false);
        }

        private static void BigIntToLeBytes(BigInteger v, Span<byte> destination)
        {
            destination.Clear();
            byte[] tmp = v.ToByteArray(isUnsigned: true, isBigEndian: false);
            int copy = Math.Min(tmp.Length, destination.Length);
            tmp.AsSpan(0, copy).CopyTo(destination);
        }

        private static BigInteger ModP(BigInteger v)
        {
            BigInteger r = v % P;
            if (r.Sign < 0) r += P;
            return r;
        }

        private static BigInteger ModL(BigInteger v)
        {
            BigInteger r = v % L;
            if (r.Sign < 0) r += L;
            return r;
        }

        private static BigInteger InvP(BigInteger v) => PowP(v, P - 2);

        private static BigInteger PowP(BigInteger b, BigInteger e)
            => BigInteger.ModPow(ModP(b), e, P);

        private static BigInteger RecoverX(BigInteger y, int signBit)
        {
            // x^2 = (y^2 - 1) / (d*y^2 + 1) mod p
            BigInteger num = ModP(y * y - 1);
            BigInteger den = ModP(D * y * y + 1);
            BigInteger xx = ModP(num * InvP(den));
            BigInteger x = PowP(xx, (P + 3) / 8);
            if (ModP(x * x - xx) != 0)
            {
                x = ModP(x * I);
            }
            if (ModP(x * x - xx) != 0)
                throw new InvalidOperationException("ed25519: no square root");
            if (((int)(x & 1)) != signBit) x = P - x;
            return x;
        }

        private readonly struct Point
        {
            public readonly BigInteger X;
            public readonly BigInteger Y;
            public Point(BigInteger x, BigInteger y) { X = x; Y = y; }
        }

        // Edwards addition in affine coordinates. Slow but correct and
        // straightforward to audit. The performance ceiling is fine for
        // archive-time signing where we sign once per build.
        private static Point PointAdd(Point a, Point b)
        {
            BigInteger xxyy = ModP(D * a.X * b.X * a.Y * b.Y);
            BigInteger x3num = ModP(a.X * b.Y + b.X * a.Y);
            BigInteger x3den = ModP(1 + xxyy);
            BigInteger y3num = ModP(a.Y * b.Y + a.X * b.X);
            BigInteger y3den = ModP(1 - xxyy);
            return new Point(ModP(x3num * InvP(x3den)), ModP(y3num * InvP(y3den)));
        }

        private static Point ScalarMult(Point p, BigInteger scalar)
        {
            // Double-and-add. Variable-time — acceptable for a build-time
            // tool (see file-level security note).
            Point result = new Point(0, 1); // neutral element on Edwards curve
            BigInteger e = scalar;
            Point acc = p;
            while (e > 0)
            {
                if ((e & 1) == 1) result = PointAdd(result, acc);
                acc = PointAdd(acc, acc);
                e >>= 1;
            }
            return result;
        }

        private static bool PointsEqual(Point a, Point b)
            => a.X == b.X && a.Y == b.Y;

        private static void EncodePoint(Point p, Span<byte> destination)
        {
            // y in 255 little-endian bits; sign(x) in bit 255.
            BigIntToLeBytes(p.Y, destination);
            byte signBit = (byte)((int)(p.X & 1) << 7);
            destination[31] = (byte)((destination[31] & 0x7F) | signBit);
        }

        private static Point? DecodePoint(ReadOnlySpan<byte> encoded)
        {
            if (encoded.Length != 32) return null;
            Span<byte> y32 = stackalloc byte[32];
            encoded.CopyTo(y32);
            int signBit = (y32[31] >> 7) & 1;
            y32[31] &= 0x7F;
            BigInteger y = LeBytesToBigInt(y32);
            if (y >= P) return null;
            BigInteger x;
            try { x = RecoverX(y, signBit); }
            catch { return null; }
            return new Point(x, y);
        }
    }
}

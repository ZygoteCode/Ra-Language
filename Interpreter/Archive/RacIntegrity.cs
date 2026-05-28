using System;
using System.IO;
using System.Security.Cryptography;

namespace RaLanguage.Interpreter.Archive
{
    // SHA-256 helpers used by both writer and reader. Centralised so the
    // exact algorithm (and any future migration) lives in one place.
    //
    // We deliberately avoid `HashAlgorithm`'s instance API in favour of
    // the static `SHA256.HashData(...)` overloads — they are zero-alloc,
    // trimmer-safe, and the AOT compiler can specialise them. The
    // streaming `IncrementalHash` flavour is used for inputs we don't
    // already have in a contiguous buffer.
    public static class RacIntegrity
    {
        // 32-byte SHA-256 digest of a contiguous byte slice.
        public static byte[] Hash(ReadOnlySpan<byte> data)
        {
            byte[] result = new byte[RacFormat.HashSize];
            SHA256.HashData(data, result);
            return result;
        }

        // Streaming hash of a Stream — used over already-written
        // archive bytes so the writer can compute the directory hash
        // without holding the whole archive in memory.
        public static byte[] HashStream(Stream stream)
        {
            using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buf = new byte[RacFormat.IoBufferSize];
            int read;
            while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            {
                ih.AppendData(buf, 0, read);
            }
            byte[] result = new byte[RacFormat.HashSize];
            ih.GetHashAndReset(result);
            return result;
        }

        // Hash the concatenation of `parts`. Used when a digest spans
        // multiple in-memory regions (e.g. directory hash = H(entry_0
        // || entry_1 || ...)).
        public static byte[] HashParts(params byte[][] parts)
        {
            using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var p in parts)
            {
                if (p == null || p.Length == 0) continue;
                ih.AppendData(p);
            }
            byte[] result = new byte[RacFormat.HashSize];
            ih.GetHashAndReset(result);
            return result;
        }

        // Constant-time comparison of two byte arrays. Used when
        // validating archive integrity to avoid early-exit side
        // channels — they are not security-critical at v1 but the
        // habit is cheap.
        public static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string FormatHex(ReadOnlySpan<byte> data)
        {
            const string Hex = "0123456789abcdef";
            Span<char> buf = stackalloc char[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                buf[2 * i]     = Hex[(data[i] >> 4) & 0x0F];
                buf[2 * i + 1] = Hex[data[i] & 0x0F];
            }
            return new string(buf);
        }
    }
}

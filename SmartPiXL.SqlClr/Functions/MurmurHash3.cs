// ─────────────────────────────────────────────────────────────────────────────
// CLR Function: MurmurHash3 (128-bit, x64 variant)
// Non-cryptographic hash for DeviceHash, fingerprint bucketing, and
// consistent partitioning. ~200ns vs ~2μs for SHA-256.
//
// Returns 16 bytes (BINARY(16)) — use for equality checks and bucketing.
// NOT for security — no collision resistance guarantees.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Data.SqlTypes;
using System.Text;
using Microsoft.SqlServer.Server;

namespace SmartPiXL.SqlClr.Functions;

public static class MurmurHash3Function
{
    private const uint Seed = 0x42F0E1EB; // Arbitrary seed — consistent across all calls

    /// <summary>
    /// Computes MurmurHash3 128-bit (x64) and returns the lower 128 bits as BINARY(16).
    /// <example>
    /// <c>SELECT dbo.MurmurHash3('some fingerprint string')</c> → <c>0x...</c> (16 bytes)
    /// </example>
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = true,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "MurmurHash3")]
    public static SqlBinary Execute(SqlString input)
    {
        if (input.IsNull)
            return SqlBinary.Null;

        var data = Encoding.UTF8.GetBytes(input.Value);
        var hash = ComputeHash128(data, Seed);

        var result = new byte[16];
        BitConverter.GetBytes(hash.H1).CopyTo(result, 0);
        BitConverter.GetBytes(hash.H2).CopyTo(result, 8);
        return new SqlBinary(result);
    }

    // ════════════════════════════════════════════════════════════════════════
    // MurmurHash3 128-bit x64 implementation
    // Reference: https://github.com/aappleby/smhasher/blob/master/src/MurmurHash3.cpp
    // ════════════════════════════════════════════════════════════════════════

    private struct Hash128
    {
        public ulong H1;
        public ulong H2;
    }

    private const ulong C1 = 0x87C37B91114253D5UL;
    private const ulong C2 = 0x4CF5AD432745937FUL;

    private static Hash128 ComputeHash128(byte[] data, uint seed)
    {
        var length = data.Length;
        var nblocks = length / 16;

        var h1 = (ulong)seed;
        var h2 = (ulong)seed;

        // ── Body: process 16-byte blocks ─────────────────────────────────
        for (var i = 0; i < nblocks; i++)
        {
            var k1 = BitConverter.ToUInt64(data, i * 16);
            var k2 = BitConverter.ToUInt64(data, i * 16 + 8);

            k1 *= C1;
            k1 = RotateLeft64(k1, 31);
            k1 *= C2;
            h1 ^= k1;

            h1 = RotateLeft64(h1, 27);
            h1 += h2;
            h1 = h1 * 5 + 0x52DCE729;

            k2 *= C2;
            k2 = RotateLeft64(k2, 33);
            k2 *= C1;
            h2 ^= k2;

            h2 = RotateLeft64(h2, 31);
            h2 += h1;
            h2 = h2 * 5 + 0x38495AB5;
        }

        // ── Tail: remaining bytes ────────────────────────────────────────
        var tail = nblocks * 16;
        ulong tk1 = 0;
        ulong tk2 = 0;

        switch (length & 15)
        {
            case 15: tk2 ^= (ulong)data[tail + 14] << 48; goto case 14;
            case 14: tk2 ^= (ulong)data[tail + 13] << 40; goto case 13;
            case 13: tk2 ^= (ulong)data[tail + 12] << 32; goto case 12;
            case 12: tk2 ^= (ulong)data[tail + 11] << 24; goto case 11;
            case 11: tk2 ^= (ulong)data[tail + 10] << 16; goto case 10;
            case 10: tk2 ^= (ulong)data[tail + 9] << 8; goto case 9;
            case 9:
                tk2 ^= (ulong)data[tail + 8];
                tk2 *= C2;
                tk2 = RotateLeft64(tk2, 33);
                tk2 *= C1;
                h2 ^= tk2;
                goto case 8;
            case 8: tk1 ^= (ulong)data[tail + 7] << 56; goto case 7;
            case 7: tk1 ^= (ulong)data[tail + 6] << 48; goto case 6;
            case 6: tk1 ^= (ulong)data[tail + 5] << 40; goto case 5;
            case 5: tk1 ^= (ulong)data[tail + 4] << 32; goto case 4;
            case 4: tk1 ^= (ulong)data[tail + 3] << 24; goto case 3;
            case 3: tk1 ^= (ulong)data[tail + 2] << 16; goto case 2;
            case 2: tk1 ^= (ulong)data[tail + 1] << 8; goto case 1;
            case 1:
                tk1 ^= (ulong)data[tail];
                tk1 *= C1;
                tk1 = RotateLeft64(tk1, 31);
                tk1 *= C2;
                h1 ^= tk1;
                break;
        }

        // ── Finalization ─────────────────────────────────────────────────
        h1 ^= (ulong)length;
        h2 ^= (ulong)length;

        h1 += h2;
        h2 += h1;

        h1 = FMix64(h1);
        h2 = FMix64(h2);

        h1 += h2;
        h2 += h1;

        return new Hash128 { H1 = h1, H2 = h2 };
    }

    private static ulong RotateLeft64(ulong x, int r) => (x << r) | (x >> (64 - r));

    private static ulong FMix64(ulong k)
    {
        k ^= k >> 33;
        k *= 0xFF51AFD7ED558CCDL;
        k ^= k >> 33;
        k *= 0xC4CEB9FE1A85EC53L;
        k ^= k >> 33;
        return k;
    }
}

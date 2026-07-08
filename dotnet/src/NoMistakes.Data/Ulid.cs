using System.Security.Cryptography;

namespace NoMistakes.Data;

/// <summary>
/// Generates monotonically increasing ULID strings, mirroring Go's
/// ulid.Monotonic(rand.Reader, 0) usage. IDs sort lexicographically in creation
/// order, which is what the run/step queries rely on for their "id DESC"
/// tie-break within the same one-second created_at bucket.
/// </summary>
internal static class Ulid
{
    // Crockford's base32 alphabet (excludes I, L, O, U).
    private const string Base32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly object Gate = new();
    private static long lastMs = -1;
    private static readonly byte[] LastRandom = new byte[10];

    /// <summary>Returns a new 26-character ULID, strictly greater than the previous one.</summary>
    public static string New()
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        byte[] random = new byte[10];

        lock (Gate)
        {
            if (ms <= lastMs)
            {
                // Same millisecond (or a backwards clock): keep the timestamp and
                // increment the random component so the ULID still increases.
                ms = lastMs;
                Increment(LastRandom);
            }
            else
            {
                RandomNumberGenerator.Fill(LastRandom);
                lastMs = ms;
            }

            Array.Copy(LastRandom, random, random.Length);
        }

        return Encode(ms, random);
    }

    private static string Encode(long ms, byte[] random)
    {
        Span<char> chars = stackalloc char[26];

        // First 10 chars hold the 48-bit millisecond timestamp (big-endian, 5
        // bits per char). Higher timestamps produce lexicographically larger
        // strings.
        var t = ms;
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Base32[(int)(t & 0x1f)];
            t >>= 5;
        }

        // Last 16 chars hold the 80-bit randomness, 5 bits per char.
        for (var i = 0; i < 16; i++)
        {
            chars[10 + i] = Base32[ExtractBits(random, i * 5, 5)];
        }

        return new string(chars);
    }

    private static int ExtractBits(byte[] data, int startBit, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            var bit = startBit + i;
            var b = (data[bit >> 3] >> (7 - (bit & 7))) & 1;
            value = (value << 1) | b;
        }
        return value;
    }

    private static void Increment(byte[] r)
    {
        for (var i = r.Length - 1; i >= 0; i--)
        {
            if (++r[i] != 0)
            {
                break;
            }
        }
    }
}

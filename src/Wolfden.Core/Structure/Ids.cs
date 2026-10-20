using System.Security.Cryptography;

namespace Wolfden.Core.Structure;

/// <summary>
/// Makes new ids (format.md §4.2): ULIDs in canonical form, a 48-bit
/// millisecond timestamp and 80 random bits, written as 26 uppercase
/// Crockford base32 characters. Readers never give the timestamp any
/// meaning (F-ID-1); it only makes ids made later sort later.
/// </summary>
public static class Ids
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A new id, from the current time and a cryptographic random source.</summary>
    public static string New()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        return New(DateTimeOffset.UtcNow, random);
    }

    /// <summary>The id for <paramref name="time"/> and 10 bytes of <paramref name="random"/>, so tests can pin it down.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The time is before 1970 or doesn't fit in 48 bits, or <paramref name="random"/> isn't 10 bytes.</exception>
    public static string New(DateTimeOffset time, ReadOnlySpan<byte> random)
    {
        var ms = time.ToUnixTimeMilliseconds();
        ArgumentOutOfRangeException.ThrowIfNegative(ms, nameof(time));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ms, (1L << 48) - 1, nameof(time));
        if (random.Length != 10)
        {
            throw new ArgumentOutOfRangeException(nameof(random), "A ULID has 10 random bytes.");
        }

        // 128 bits: the timestamp in the top 48, the random bytes below.
        var high = ((ulong)ms << 16) | ((ulong)random[0] << 8) | random[1];
        ulong low = 0;
        for (var i = 2; i < 10; i++)
        {
            low = (low << 8) | random[i];
        }

        var value = new UInt128(high, low);
        Span<char> chars = stackalloc char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Crockford[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }
}

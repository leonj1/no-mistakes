namespace NoMistakes.Config;

/// <summary>
/// Parses Go's <c>time.ParseDuration</c> string format so .NET config values
/// stay wire-compatible with the Go implementation. Examples: "168h", "2h30m",
/// "90m", "0s", "-5m". Valid units are ns, us (µs), ms, s, m, h.
/// Throws <see cref="FormatException"/> on malformed input.
/// </summary>
public static class GoDuration
{
    private static readonly Dictionary<string, long> UnitMap = new(StringComparer.Ordinal)
    {
        ["ns"] = 1L,
        ["us"] = 1000L,
        ["µs"] = 1000L, // µs (micro sign)
        ["μs"] = 1000L, // μs (greek small letter mu)
        ["ms"] = 1_000_000L,
        ["s"] = 1_000_000_000L,
        ["m"] = 60_000_000_000L,
        ["h"] = 3_600_000_000_000L,
    };

    /// <summary>Parses a Go duration string into a <see cref="TimeSpan"/>.</summary>
    public static TimeSpan Parse(string value)
    {
        // A TimeSpan tick is 100ns; config durations are always whole 100ns units.
        return TimeSpan.FromTicks(ParseNanos(value) / 100);
    }

    /// <summary>Parses a Go duration string into a nanosecond count.</summary>
    public static long ParseNanos(string s)
    {
        var orig = s;
        long d = 0;
        var neg = false;

        if (s.Length > 0)
        {
            var c = s[0];
            if (c == '-' || c == '+')
            {
                neg = c == '-';
                s = s.Substring(1);
            }
        }

        // Special case: if all that is left is "0", this is zero.
        if (s == "0")
        {
            return 0;
        }
        if (s.Length == 0)
        {
            throw new FormatException($"time: invalid duration \"{orig}\"");
        }

        while (s.Length > 0)
        {
            if (!(s[0] == '.' || (s[0] >= '0' && s[0] <= '9')))
            {
                throw new FormatException($"time: invalid duration \"{orig}\"");
            }

            var pl = s.Length;
            long v;
            (v, s) = LeadingInt(s);
            var pre = pl != s.Length;

            long f = 0;
            double scale = 1;
            var post = false;
            if (s.Length > 0 && s[0] == '.')
            {
                s = s.Substring(1);
                var pl2 = s.Length;
                (f, scale, s) = LeadingFraction(s);
                post = pl2 != s.Length;
            }

            if (!pre && !post)
            {
                throw new FormatException($"time: invalid duration \"{orig}\"");
            }

            var i = 0;
            for (; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '.' || (c >= '0' && c <= '9'))
                {
                    break;
                }
            }
            if (i == 0)
            {
                throw new FormatException($"time: missing unit in duration \"{orig}\"");
            }

            var u = s.Substring(0, i);
            s = s.Substring(i);
            if (!UnitMap.TryGetValue(u, out var unit))
            {
                throw new FormatException($"time: unknown unit \"{u}\" in duration \"{orig}\"");
            }

            v *= unit;
            if (f > 0)
            {
                v += (long)((double)f * ((double)unit / scale));
            }
            d += v;
        }

        return neg ? -d : d;
    }

    private static (long Value, string Rest) LeadingInt(string s)
    {
        long x = 0;
        var i = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c < '0' || c > '9')
            {
                break;
            }
            x = (x * 10) + (c - '0');
        }
        return (x, s.Substring(i));
    }

    private static (long Value, double Scale, string Rest) LeadingFraction(string s)
    {
        var i = 0;
        long x = 0;
        double scale = 1;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c < '0' || c > '9')
            {
                break;
            }
            x = (x * 10) + (c - '0');
            scale *= 10;
        }
        return (x, scale, s.Substring(i));
    }
}

using System.Text.RegularExpressions;

namespace NoMistakes.Git;

/// <summary>
/// Hides credentials embedded in URLs before they reach logs or error text.
/// Mirrors Go's <c>internal/safeurl.RedactText</c>: it finds http(s) URLs in a
/// blob of text and replaces any userinfo component with "redacted", leaving
/// non-URL and credential-free text unchanged.
///
/// This is a minimal copy local to the git wrapper (the only slice-4 consumer);
/// slice 6 (SCM URL parsing) promotes the full safeurl surface to a shared
/// package.
/// </summary>
public static partial class Redactor
{
    [GeneratedRegex("""https?://[^\s'"<>]+""")]
    private static partial Regex HttpUrlPattern();

    /// <summary>
    /// Replaces userinfo in every http(s) URL found in <paramref name="text"/>
    /// with "redacted". Non-URL text is returned unchanged.
    /// </summary>
    public static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        return HttpUrlPattern().Replace(text, m => Redact(m.Value));
    }

    /// <summary>
    /// Redacts the userinfo of a single URL. A value that does not parse as a
    /// URL, or carries no userinfo, is returned unchanged.
    /// </summary>
    public static string Redact(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return raw;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return raw;
        }
        if (string.IsNullOrEmpty(uri.UserInfo))
        {
            return raw;
        }
        var builder = new UriBuilder(uri)
        {
            UserName = "redacted",
            Password = string.Empty,
        };
        // UriBuilder emits "redacted:@host" when the password is cleared to
        // empty; Go's url.User("redacted") yields "redacted@host". Normalize to
        // match the Go oracle.
        return builder.Uri.ToString().Replace("redacted:@", "redacted@");
    }
}

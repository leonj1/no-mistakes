namespace NoMistakes.Scm;

/// <summary>
/// PR-description length budgeting, mirroring Go's <c>internal/scm</c>
/// prbody.go. Lengths are measured in UTF-16 code units - the way Azure
/// DevOps (a .NET service) counts a description - which in C# is simply
/// <see cref="string.Length"/>.
/// </summary>
public static class PRBody
{
    /// <summary>
    /// Appended by <see cref="Clamp"/> when it has to cut a body, so a
    /// shortened description is visibly marked rather than ending mid-text.
    /// </summary>
    internal const string TruncationMarker = "\n\n…(description truncated)";

    /// <summary>
    /// Length of <paramref name="s"/> in UTF-16 code units (a non-BMP rune -
    /// an emoji, some CJK - counts as two). Mirrors Go's <c>PRBodyLen</c>;
    /// in .NET this is the native string length.
    /// </summary>
    public static int Length(string s) => s.Length;

    /// <summary>
    /// The hard limit a provider enforces on a PR description, measured in
    /// <see cref="Length"/> units, or 0 when the provider imposes no practical
    /// limit. Azure DevOps rejects descriptions over 4000 characters; GitHub,
    /// GitLab, and Bitbucket allow far larger bodies than this tool ever
    /// produces.
    /// </summary>
    public static int MaxChars(Provider p) => p == Provider.AzureDevOps ? 4000 : 0;

    /// <summary>
    /// Truncates <paramref name="body"/> to at most <paramref name="max"/>
    /// UTF-16 units, cutting on a rune boundary and appending a truncation
    /// marker (kept inside the budget) when it cuts. max &lt;= 0 means
    /// unlimited. This is the last-resort backstop: callers that can shed
    /// whole sections to fit a budget should do so before relying on a blind
    /// clamp.
    /// </summary>
    public static string Clamp(string body, int max)
    {
        if (max <= 0 || body.Length <= max)
        {
            return body;
        }
        var budget = max - TruncationMarker.Length;
        if (budget < 0)
        {
            budget = 0;
        }
        // Never split a surrogate pair: dropping the low half would leave an
        // unpaired high surrogate at the cut.
        if (budget > 0 && char.IsHighSurrogate(body[budget - 1]))
        {
            budget--;
        }
        return body[..budget] + TruncationMarker;
    }
}

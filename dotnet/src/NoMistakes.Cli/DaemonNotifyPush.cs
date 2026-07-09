using NoMistakes.Core;
using NoMistakes.Ipc;

namespace NoMistakes.Cli;

/// <summary>
/// The hidden `daemon notify-push` operation invoked by the gate's
/// post-receive hook: parses the git push options and forwards the push to
/// the daemon over IPC as a push_received request. Ported from Go
/// internal/cli/daemon_cmd.go newDaemonNotifyPushCmd and its push-option
/// parse/format helpers.
/// </summary>
public static class DaemonNotifyPush
{
    /// <summary>
    /// Carries an agent-supplied intent through a git push. The value is
    /// base64-encoded so multi-line or special-character intents survive the
    /// push-option transport (which is line-oriented).
    /// </summary>
    internal const string IntentPushOptionPrefix = "no-mistakes.intent=";

    private const string SkipPushOptionPrefix = "no-mistakes.skip=";

    /// <summary>
    /// Notifies the daemon of a received push. Failures surface as
    /// exceptions: an unreachable daemon as IOException ("connect to daemon:
    /// ..."), a daemon-side rejection as IpcRpcException.
    /// </summary>
    public static async Task<PushReceivedResult> NotifyPushAsync(
        Paths paths,
        string gate,
        string refName,
        string oldSha,
        string newSha,
        IReadOnlyList<string> pushOptions,
        CancellationToken ct = default)
    {
        var skipSteps = ParseSkipPushOptions(pushOptions);
        var intent = ParseIntentPushOptions(pushOptions);

        IpcClient client;
        try
        {
            client = await IpcClient.DialAsync(paths.Socket, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new IOException($"connect to daemon: {ex.Message}");
        }
        using (client)
        {
            var result = await client.CallAsync<PushReceivedResult>(Methods.PushReceived, new PushReceivedParams
            {
                Gate = gate,
                Ref = refName,
                Old = oldSha,
                New = newSha,
                SkipSteps = skipSteps.Count > 0 ? skipSteps : null,
                Intent = string.IsNullOrEmpty(intent) ? null : intent,
            }, ct).ConfigureAwait(false);
            return result ?? new PushReceivedResult();
        }
    }

    /// <summary>
    /// Extracts step names from every no-mistakes.skip= push option,
    /// deduplicated in first-seen order. Unknown steps throw ArgumentException
    /// with Go's message shape (unknown step "x").
    /// </summary>
    internal static List<string> ParseSkipPushOptions(IEnumerable<string> options)
    {
        var steps = new List<string>();
        foreach (var option in options)
        {
            if (!option.StartsWith(SkipPushOptionPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            steps.AddRange(ParseSkipSteps(option[SkipPushOptionPrefix.Length..]));
        }
        return DedupeSteps(steps);
    }

    internal static List<string> ParseSkipSteps(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }
        var steps = new List<string>();
        foreach (var part in value.Split(','))
        {
            var step = part.Trim();
            if (!StepName.All.Contains(step))
            {
                throw new ArgumentException($"unknown step \"{step}\"");
            }
            steps.Add(step);
        }
        return DedupeSteps(steps);
    }

    /// <summary>
    /// Extracts and decodes the intent push option, if any. The last
    /// occurrence wins; a corrupt encoding throws ArgumentException.
    /// </summary>
    internal static string ParseIntentPushOptions(IEnumerable<string> options)
    {
        var intent = string.Empty;
        foreach (var option in options)
        {
            if (!option.StartsWith(IntentPushOptionPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var encoded = option[IntentPushOptionPrefix.Length..];
            try
            {
                intent = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException ex)
            {
                throw new ArgumentException($"decode intent push option: {ex.Message}");
            }
        }
        return intent;
    }

    /// <summary>
    /// Encodes intent as a single push option, or returns "" when there is no
    /// intent to carry.
    /// </summary>
    internal static string FormatIntentPushOption(string intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            return string.Empty;
        }
        return IntentPushOptionPrefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(intent));
    }

    internal static List<string> FormatSkipPushOptions(IReadOnlyList<string> steps)
    {
        if (steps.Count == 0)
        {
            return new List<string>();
        }
        return new List<string> { SkipPushOptionPrefix + string.Join(",", DedupeSteps(steps.ToList())) };
    }

    private static List<string> DedupeSteps(List<string> steps)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(steps.Count);
        foreach (var step in steps)
        {
            if (seen.Add(step))
            {
                result.Add(step);
            }
        }
        return result;
    }
}

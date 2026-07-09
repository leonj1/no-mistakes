using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMistakes.Daemon;

/// <summary>
/// The daemon PID file: the daemon's process id plus its process start time,
/// so a later reader can tell a live owner from a reused PID. Ported from Go
/// internal/daemon (daemonPIDFile in recover_servers.go plus the read/write
/// helpers in daemon.go).
/// </summary>
public sealed class DaemonPidRecord
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Injectable so tests can force a rename failure (Go's renameDaemonPIDFile var).
    internal static Action<string, string> Rename = (from, to) => File.Move(from, to, overwrite: true);

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    public bool SameOwner(DaemonPidRecord other) =>
        Pid == other.Pid && Nullable.Equals(StartedAt, other.StartedAt);

    /// <summary>
    /// Builds the record for the current process. Falls back to the given (or
    /// wall) clock when the OS refuses to report the process start time,
    /// mirroring Go's currentDaemonPIDRecord fallback chain.
    /// </summary>
    public static DaemonPidRecord CurrentProcess(Func<DateTimeOffset>? now = null)
    {
        DateTimeOffset startedAt;
        try
        {
            startedAt = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception)
        {
            startedAt = (now ?? (() => DateTimeOffset.UtcNow))();
        }
        return new DaemonPidRecord { Pid = Environment.ProcessId, StartedAt = startedAt.ToUniversalTime() };
    }

    /// <summary>Reads and parses the PID file at path.</summary>
    public static DaemonPidRecord Read(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Parses PID-file contents: the JSON record, or a legacy plain-integer
    /// file (older daemons wrote just the PID). Throws
    /// <see cref="FormatException"/> on anything else, including a
    /// non-positive PID.
    /// </summary>
    public static DaemonPidRecord Parse(string data)
    {
        try
        {
            var record = JsonSerializer.Deserialize<DaemonPidRecord>(data, JsonOptions);
            if (record != null)
            {
                if (record.Pid <= 0)
                {
                    throw new FormatException("invalid pid file: pid must be positive");
                }
                return record;
            }
        }
        catch (JsonException)
        {
            // Fall through to the legacy plain-integer format.
        }
        if (!int.TryParse(data.Trim(), out var pid))
        {
            throw new FormatException("invalid pid file: not a valid pid");
        }
        if (pid <= 0)
        {
            throw new FormatException("invalid pid file: pid must be positive");
        }
        return new DaemonPidRecord { Pid = pid };
    }

    /// <summary>
    /// Writes the record atomically: a temp file in the same directory is
    /// renamed over the target, so a failed write never destroys an existing
    /// PID file (Go writeDaemonPIDFile).
    /// </summary>
    public static void Write(string path, DaemonPidRecord record)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            dir = ".";
        }
        var tmpPath = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(record, JsonOptions));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    tmpPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            Rename(tmpPath, path);
        }
        catch
        {
            try
            {
                File.Delete(tmpPath);
            }
            catch
            {
                // Best-effort temp cleanup; the original error matters more.
            }
            throw;
        }
    }
}

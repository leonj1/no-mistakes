using NoMistakes.Daemon;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// PID-file parsing and atomic-write behavior, ported from Go's
/// TestReadPID* and TestWriteDaemonPIDFile_* lifecycle tests. Shares the
/// "daemon" collection with the lifecycle tests because the rename-failure
/// test swaps the process-global <see cref="DaemonPidRecord.Rename"/> hook.
/// </summary>
[Collection("daemon")]
public class DaemonPidRecordTests
{
    [Fact]
    public void JsonRecordRoundTrips()
    {
        using var tmp = new TempDir();
        var path = tmp.File("daemon.pid");
        var record = new DaemonPidRecord
        {
            Pid = 4242,
            StartedAt = new DateTimeOffset(2026, 4, 20, 10, 0, 0, 123, TimeSpan.Zero),
        };
        DaemonPidRecord.Write(path, record);

        var got = DaemonPidRecord.Read(path);
        Assert.Equal(4242, got.Pid);
        Assert.Equal(record.StartedAt, got.StartedAt);
        Assert.True(got.SameOwner(record));
    }

    [Fact]
    public void LegacyPlainIntegerFileParses()
    {
        var got = DaemonPidRecord.Parse("12345\n");
        Assert.Equal(12345, got.Pid);
        Assert.Null(got.StartedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-pid")]
    [InlineData("{\"pid\":\"nope\"}")]
    public void InvalidContentThrows(string data)
    {
        Assert.Throws<FormatException>(() => DaemonPidRecord.Parse(data));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("{\"pid\":0}")]
    [InlineData("{\"pid\":-1}")]
    public void NonPositivePidIsRejected(string data)
    {
        Assert.Throws<FormatException>(() => DaemonPidRecord.Parse(data));
    }

    [Fact]
    public void ReadMissingFileThrows()
    {
        using var tmp = new TempDir();
        Assert.ThrowsAny<IOException>(() => DaemonPidRecord.Read(tmp.File("daemon.pid")));
    }

    [Fact]
    public void WriteLeavesExistingFileUntouchedOnRenameFailure()
    {
        using var tmp = new TempDir();
        var path = tmp.File("daemon.pid");
        DaemonPidRecord.Write(path, new DaemonPidRecord { Pid = 1111 });

        var originalRename = DaemonPidRecord.Rename;
        DaemonPidRecord.Rename = (_, _) => throw new IOException("rename blew up");
        try
        {
            Assert.Throws<IOException>(() =>
                DaemonPidRecord.Write(path, new DaemonPidRecord { Pid = 2222 }));
        }
        finally
        {
            DaemonPidRecord.Rename = originalRename;
        }

        Assert.Equal(1111, DaemonPidRecord.Read(path).Pid);
        // The failed attempt must not leak its temp file either.
        var leftovers = Directory.GetFiles(tmp.Path);
        var file = Assert.Single(leftovers);
        Assert.Equal(path, file);
    }

    [Fact]
    public void WriteReplacesExistingFileAndLeavesNoTempFiles()
    {
        using var tmp = new TempDir();
        var path = tmp.File("daemon.pid");
        DaemonPidRecord.Write(path, new DaemonPidRecord { Pid = 1111 });
        DaemonPidRecord.Write(path, new DaemonPidRecord { Pid = 2222 });

        Assert.Equal(2222, DaemonPidRecord.Read(path).Pid);
        var file = Assert.Single(Directory.GetFiles(tmp.Path));
        Assert.Equal(path, file);
    }

    [Fact]
    public void CurrentProcessRecordsOwnPidAndStartTime()
    {
        var record = DaemonPidRecord.CurrentProcess();
        Assert.Equal(Environment.ProcessId, record.Pid);
        Assert.NotNull(record.StartedAt);
        Assert.True(record.StartedAt!.Value <= DateTimeOffset.UtcNow);
    }
}

using System.Text;
using System.Text.Json;

namespace NoMistakes.Ipc;

/// <summary>
/// Newline-delimited JSON framing over a stream: one JSON document per line,
/// the wire format of the IPC connection. Mirrors Go's json.Encoder +
/// bufio.Scanner pair, including the 1 MiB per-message cap. Not safe for
/// concurrent readers or concurrent writers; callers serialize access like
/// Go's ipc.Client mutex.
/// </summary>
public sealed class JsonLineStream : IDisposable
{
    /// <summary>Largest accepted message, matching Go's scanner buffer cap.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    private static readonly byte[] Newline = "\n"u8.ToArray();

    private readonly Stream stream;
    private byte[] buffer = new byte[4096];
    private int start;
    private int end;

    public JsonLineStream(Stream stream)
    {
        this.stream = stream;
    }

    /// <summary>Writes one message as a single JSON line and flushes.</summary>
    public async Task WriteAsync<T>(T message, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.WriteAsync(Newline, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the next message, or null on a clean end-of-stream at a message
    /// boundary. Throws <see cref="IOException"/> on a truncated trailing
    /// message or one exceeding <see cref="MaxMessageBytes"/>, and
    /// <see cref="JsonException"/> on a malformed document.
    /// </summary>
    public async Task<T?> ReadAsync<T>(CancellationToken ct = default)
        where T : class
    {
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        if (line == null)
        {
            return null;
        }
        return JsonSerializer.Deserialize<T>(line, IpcJson.Options);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            if (newline >= 0)
            {
                var line = Encoding.UTF8.GetString(buffer, start, newline - start);
                start = newline + 1;
                return line;
            }

            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }

            // A message may be exactly MaxMessageBytes long plus its newline,
            // so allow one extra byte before declaring the message oversized.
            if (end > MaxMessageBytes)
            {
                throw new IOException($"ipc message exceeds {MaxMessageBytes} bytes");
            }
            if (end == buffer.Length)
            {
                var grown = new byte[Math.Min(buffer.Length * 2, MaxMessageBytes + 1)];
                Buffer.BlockCopy(buffer, 0, grown, 0, end);
                buffer = grown;
            }

            var n = await stream.ReadAsync(buffer.AsMemory(end, buffer.Length - end), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (end > start)
                {
                    throw new IOException("ipc connection closed mid-message");
                }
                return null;
            }
            end += n;
        }
    }

    public void Dispose() => stream.Dispose();
}

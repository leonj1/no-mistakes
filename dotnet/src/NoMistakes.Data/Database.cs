using Microsoft.Data.Sqlite;

namespace NoMistakes.Data;

/// <summary>
/// Wraps a single SQLite connection to the run database and runs migrations on
/// open. Ported from Go's internal/db.DB. A single connection is held open (Go
/// pins MaxOpenConns=1) and every operation is serialized behind a lock, so the
/// type is safe to share across threads the way the daemon does.
/// </summary>
public sealed partial class Database : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();

    private Database(SqliteConnection connection)
    {
        this.connection = connection;
    }

    /// <summary>
    /// Opens (or creates) the SQLite database at <paramref name="path"/>, applies
    /// the schema and additive migrations, and returns a ready connection.
    /// </summary>
    public static Database Open(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
        }.ToString();

        var conn = new SqliteConnection(connectionString);
        try
        {
            conn.Open();

            // busy_timeout must be set first so the schema/migration statements
            // wait for a transient writer lock instead of failing immediately.
            ExecPragma(conn, "PRAGMA busy_timeout=5000");
            ExecPragma(conn, "PRAGMA journal_mode=WAL");
            ExecPragma(conn, "PRAGMA foreign_keys=ON");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = Schema.Sql;
                cmd.ExecuteNonQuery();
            }

            foreach (var stmt in Schema.MigrationStatements)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = stmt;
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (IsDuplicateColumnError(ex))
                {
                    // Column already exists; the ALTER is a no-op.
                }
            }
        }
        catch
        {
            conn.Dispose();
            throw;
        }

        return new Database(conn);
    }

    /// <summary>Closes the underlying connection.</summary>
    public void Close() => connection.Dispose();

    public void Dispose() => connection.Dispose();

    /// <summary>
    /// Reports whether <paramref name="ex"/> is SQLite's "duplicate column name"
    /// error, which ALTER TABLE ADD COLUMN raises when the column already exists.
    /// </summary>
    private static bool IsDuplicateColumnError(SqliteException ex) =>
        ex.Message.Contains("duplicate column name", StringComparison.Ordinal);

    private static void ExecPragma(SqliteConnection conn, string pragma)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Generates a new monotonic ULID for a primary key.</summary>
    private static string NewId() => Ulid.New();

    /// <summary>Current unix timestamp in whole seconds, matching Go's time.Now().Unix().</summary>
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---- low-level helpers shared by the per-table partials ----

    private SqliteCommand NewCommand(string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Bind(SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string? NullableString(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? GetNullableString(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static long? GetNullableLong(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetInt64(ordinal);

    private static int? GetNullableInt(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : (int)r.GetInt64(ordinal);

    private static double? GetNullableDouble(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetDouble(ordinal);
}

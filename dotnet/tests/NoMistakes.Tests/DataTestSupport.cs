using Microsoft.Data.Sqlite;
using NoMistakes.Data;

namespace NoMistakes.Tests;

/// <summary>
/// Shared helpers for the run-database tests, mirroring the openTestDB helper and
/// the raw-SQL probes in Go's internal/db test suite. Legacy-schema setup and
/// column probing use a second short-lived connection to the same file, keeping
/// the Database type's own connection private.
/// </summary>
internal static class DataTestSupport
{
    /// <summary>Opens a fresh run database in an isolated temp file.</summary>
    public static Database OpenTestDb(TempDir dir) => Database.Open(dir.File("test.sqlite"));

    /// <summary>Opens a raw connection to a database file for test setup/probing.</summary>
    public static SqliteConnection OpenRaw(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
        }.ToString();
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL");
        return conn;
    }

    /// <summary>Executes a non-query statement on an open connection.</summary>
    public static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reports whether a table has the named column via PRAGMA table_info.</summary>
    public static bool HasColumn(string path, string table, string column)
    {
        using var conn = OpenRaw(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // Columns: cid, name, type, notnull, dflt_value, pk.
            if (r.GetString(1) == column)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Reports whether a table exists and is queryable.</summary>
    public static bool TableExists(string path, string table)
    {
        using var conn = OpenRaw(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table}";
        try
        {
            cmd.ExecuteScalar();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}

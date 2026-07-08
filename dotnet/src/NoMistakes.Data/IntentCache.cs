namespace NoMistakes.Data;

/// <summary>A cached summarization for a known agent session. Mirrors Go's db.IntentCacheEntry.</summary>
public sealed class IntentCacheEntry
{
    public string CacheKey { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}

public sealed partial class Database
{
    /// <summary>Returns the cached summary for a key, or null if absent.</summary>
    public IntentCacheEntry? GetIntentCache(string key)
    {
        lock (gate)
        {
            using var cmd = NewCommand("SELECT cache_key, summary, agent_name, session_id, created_at FROM intent_cache WHERE cache_key = $key");
            Bind(cmd, "$key", key);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return null;
            }
            return new IntentCacheEntry
            {
                CacheKey = r.GetString(0),
                Summary = r.GetString(1),
                AgentName = r.GetString(2),
                SessionId = r.GetString(3),
                CreatedAt = r.GetInt64(4),
            };
        }
    }

    /// <summary>Inserts or replaces an intent cache entry.</summary>
    public void PutIntentCache(IntentCacheEntry entry)
    {
        var createdAt = entry.CreatedAt == 0 ? Now() : entry.CreatedAt;
        lock (gate)
        {
            using var cmd = NewCommand(
                "INSERT OR REPLACE INTO intent_cache (cache_key, summary, agent_name, session_id, created_at) VALUES ($key, $summary, $agent, $session, $created)");
            Bind(cmd, "$key", entry.CacheKey);
            Bind(cmd, "$summary", entry.Summary);
            Bind(cmd, "$agent", entry.AgentName);
            Bind(cmd, "$session", entry.SessionId);
            Bind(cmd, "$created", createdAt);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes entries older than <paramref name="maxAge"/>. Returns rows deleted.</summary>
    public long CleanupOldIntentCache(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge).ToUnixTimeSeconds();
        lock (gate)
        {
            using var cmd = NewCommand("DELETE FROM intent_cache WHERE created_at < $cutoff");
            Bind(cmd, "$cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }
}

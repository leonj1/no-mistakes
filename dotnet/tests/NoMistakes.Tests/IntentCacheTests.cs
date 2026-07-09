using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>Ported from Go's internal/db/intent_cache_test.go (cache put/get/cleanup).</summary>
public sealed class IntentCacheTests
{
    [Fact]
    public void IntentCachePutGet()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);

        Assert.Null(db.GetIntentCache("missing"));

        db.PutIntentCache(new IntentCacheEntry
        {
            CacheKey = "k1",
            Summary = "do the thing",
            AgentName = "claude",
            SessionId = "sess-1",
        });

        var got = db.GetIntentCache("k1");
        Assert.NotNull(got);
        Assert.Equal("do the thing", got!.Summary);
        Assert.Equal("claude", got.AgentName);
        Assert.NotEqual(0, got.CreatedAt);

        // Replace.
        db.PutIntentCache(new IntentCacheEntry
        {
            CacheKey = "k1",
            Summary = "do the new thing",
            AgentName = "claude",
            SessionId = "sess-1",
        });
        Assert.Equal("do the new thing", db.GetIntentCache("k1")!.Summary);
    }

    [Fact]
    public void IntentCacheCleanup()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        db.PutIntentCache(new IntentCacheEntry
        {
            CacheKey = "old",
            Summary = "x",
            AgentName = "claude",
            SessionId = "s",
            CreatedAt = now - (long)TimeSpan.FromDays(40).TotalSeconds,
        });
        db.PutIntentCache(new IntentCacheEntry
        {
            CacheKey = "new",
            Summary = "x",
            AgentName = "claude",
            SessionId = "s",
            CreatedAt = now,
        });

        var deleted = db.CleanupOldIntentCache(TimeSpan.FromDays(30));
        Assert.Equal(1, deleted);
        Assert.Null(db.GetIntentCache("old"));
        Assert.NotNull(db.GetIntentCache("new"));
    }
}

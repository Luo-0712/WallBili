using Microsoft.Data.Sqlite;
using WallBili.Core;
using WallBili.Infrastructure.Storage;
using Xunit;

namespace WallBili.Infrastructure.Tests;

/// <summary>The steady-state checkpoint moves one playback number, so the catalog tables behind it must
/// not be rewritten. A trigger counts row activity, which proves the skip instead of trusting timing.</summary>
public sealed class SqliteIncrementalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PositionOnlySavesLeaveTheCatalogUntouchedAndCatalogEditsStillPersist()
    {
        using var f = new StoreFixture();
        var store = new SqliteLibraryStore(f.DatabasePath);
        await store.SaveAsync(State(7), Token);
        await using (var db = await f.OpenAsync())
            using (var command = db.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE touched(kind TEXT NOT NULL);
                    CREATE TRIGGER count_entries AFTER DELETE ON entries BEGIN INSERT INTO touched VALUES('entries'); END;
                    CREATE TRIGGER count_sources AFTER DELETE ON sources BEGIN INSERT INTO touched VALUES('sources'); END;
                    CREATE TRIGGER count_queue AFTER DELETE ON queue BEGIN INSERT INTO touched VALUES('queue'); END;
                    """;
                await command.ExecuteNonQueryAsync(Token);
            }

        await store.SaveAsync(State(8), Token); // Only the playback position moved.
        await using (var db = await f.OpenAsync())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM touched";
                Assert.Equal(0L, await command.ExecuteScalarAsync(Token));
                command.CommandText = "SELECT payload FROM playback WHERE id=1";
                Assert.Contains("\"PositionSeconds\":8", (string)await command.ExecuteScalarAsync(Token)!);
            }

        await store.SaveAsync(State(9, title: "Renamed"), Token);
        var restored = await store.LoadAsync(Token);
        Assert.Equal("Renamed", Assert.Single(restored.Entries).Title);
        Assert.Equal(9, restored.Playback.PositionSeconds);
        await using (var db = await f.OpenAsync())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM touched WHERE kind='entries'";
                Assert.Equal(1L, await command.ExecuteScalarAsync(Token)); // The catalog was rewritten once.
            }
    }

    [Fact]
    public async Task AFailedIncrementalSaveDoesNotPoisonTheCache()
    {
        using var f = new StoreFixture();
        var store = new SqliteLibraryStore(f.DatabasePath);
        await store.SaveAsync(State(7), Token);
        await using (var db = await f.OpenAsync())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "CREATE TRIGGER fail_playback BEFORE INSERT ON playback BEGIN SELECT RAISE(FAIL,'disk'); END;";
                await command.ExecuteNonQueryAsync(Token);
            }

        await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync(State(8), Token));

        await using (var db = await f.OpenAsync())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "DROP TRIGGER fail_playback;";
                await command.ExecuteNonQueryAsync(Token);
            }
        await store.SaveAsync(State(8), Token); // Same state as the failed attempt: the cache must not skip it.
        var restored = await store.LoadAsync(Token);
        Assert.Equal(8, restored.Playback.PositionSeconds);
    }

    private static readonly Guid FixedQueueId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static LibraryState State(double position, string title = "Light")
    {
        var key = new ContentKey("BV1GJ411x7h7", 101);
        var queue = new QueueItem(FixedQueueId, key, 2);
        return new()
        {
            Entries = [new(key, title, "Creator", 1, TimeSpan.FromSeconds(90))],
            Sources = [new("video-source", title, key.Bvid, null, DateTimeOffset.UnixEpoch, [key], [])],
            Queue = [queue], Playback = new(queue.Id, position, 62, PlaybackIntent.Paused, QueueMode.RepeatAll),
            ManualQueue = [queue], ManualPlayback = new(queue.Id, position, 62, PlaybackIntent.Paused, QueueMode.RepeatAll),
        };
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wallbili-incremental-" + Guid.NewGuid().ToString("N")));
        public string DatabasePath => Path.Combine(_directory, "library.db");
        public StoreFixture() => Directory.CreateDirectory(_directory);
        public async Task<SqliteConnection> OpenAsync()
        {
            var db = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            await db.OpenAsync(Token);
            return db;
        }
        public void Dispose()
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!_directory.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(_directory).StartsWith("wallbili-incremental-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test artifact directory.");
            Directory.Delete(_directory, recursive: true);
        }
    }
}

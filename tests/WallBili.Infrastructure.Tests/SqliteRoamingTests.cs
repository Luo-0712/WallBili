using System.Text.Json;
using Microsoft.Data.Sqlite;
using WallBili.Core;
using WallBili.Infrastructure.Storage;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class SqliteRoamingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MigratesVersionOneWithoutChangingExistingCatalogQueueOrPlayback()
    {
        using var f = new StoreFixture();
        var entry = new CatalogEntry(new("BV1GJ411x7h7", 101), "A video", "Creator", 1, TimeSpan.FromMinutes(3));
        var queue = new QueueItem(Guid.NewGuid(), entry.Key, 2);
        var playback = new PlaybackState(queue.Id, 27.5, 62, PlaybackIntent.Paused, QueueMode.RepeatAll);
        var source = new CatalogSource("video-source", "A video", entry.Key.Bvid, null,
            DateTimeOffset.FromUnixTimeSeconds(1760000020), [entry.Key], []);
        await using (var db = await f.OpenDatabaseAsync())
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE entries(bvid TEXT NOT NULL,cid INTEGER NOT NULL,ordinal INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(bvid,cid));
                CREATE TABLE sources(id TEXT PRIMARY KEY,ordinal INTEGER NOT NULL,payload TEXT NOT NULL);
                CREATE TABLE queue(id TEXT PRIMARY KEY,ordinal INTEGER NOT NULL UNIQUE,bvid TEXT NOT NULL,cid INTEGER NOT NULL,completed INTEGER NOT NULL CHECK(completed>=0),FOREIGN KEY(bvid,cid) REFERENCES entries(bvid,cid));
                CREATE TABLE playback(id INTEGER PRIMARY KEY CHECK(id=1),payload TEXT NOT NULL);
                INSERT INTO entries VALUES('BV1GJ411x7h7',101,0,$entry);
                INSERT INTO sources VALUES('video-source',0,$source);
                INSERT INTO queue VALUES($id,0,'BV1GJ411x7h7',101,2);
                INSERT INTO playback VALUES(1,$playback);
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(entry));
            command.Parameters.AddWithValue("$id", queue.Id.ToString());
            command.Parameters.AddWithValue("$source", JsonSerializer.Serialize(source));
            command.Parameters.AddWithValue("$playback", JsonSerializer.Serialize(playback));
            await command.ExecuteNonQueryAsync(Token);
        }

        var state = await f.Store.LoadAsync(Token);

        Assert.Equal(entry, Assert.Single(state.Entries));
        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(Assert.Single(state.Sources)));
        Assert.Equal(queue, Assert.Single(state.Queue));
        Assert.Equal(playback, state.Playback);
        Assert.Equal(PlaybackSource.Manual, state.ActiveSource);
        Assert.Empty(state.RoamingAccounts);
        await using var verify = await f.OpenDatabaseAsync();
        using var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, await version.ExecuteScalarAsync(Token));
        version.CommandText = "SELECT payload FROM playback WHERE id=1";
        Assert.Equal(JsonSerializer.Serialize(playback), await version.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task RoundTripsIndependentAccountHistoryScheduleSyncAndManualPlayback()
    {
        using var f = new StoreFixture();
        var state = RoamingState();

        await f.Store.SaveAsync(state, Token);
        var restored = await f.Store.LoadAsync(Token);

        Assert.Equal(PlaybackSource.Roaming, restored.ActiveSource);
        Assert.Equal(42, restored.ActiveRoamingAccountId);
        Assert.Equal(state.ManualQueue, restored.ManualQueue);
        Assert.Equal(state.ManualPlayback, restored.ManualPlayback);
        Assert.Equal(state.Queue, restored.Queue);
        Assert.Equal(state.Playback, restored.Playback);
        Assert.Equal(new[] { 42L, 84L }, restored.RoamingAccounts.Keys.Order());
        var first = restored.RoamingAccounts[42];
        Assert.Equal(new FollowedCreator(7, "Creator"), Assert.Single(first.Subscriptions));
        Assert.Equal(state.RoamingAccounts[42].Videos, first.Videos);
        Assert.Equal(3, first.Sync[7].NextPage);
        Assert.NotNull(first.Sync[7].Watermark);
        Assert.Equal("Network", first.Sync[7].Failure);
        Assert.Contains("BV1xx411c7mD", first.ExcludedBvids);
        Assert.True(first.RoundActive);
        Assert.True(first.ScheduleEnabled);
        Assert.Equal(TimeSpan.FromHours(10), first.ScheduleTime);
        Assert.Equal(new DateOnly(2026, 10, 17), first.LastScheduleDate);
        Assert.Equal(new DateOnly(2026, 10, 16), first.LastRefreshDate);
        Assert.False(restored.RoamingAccounts[84].ScheduleEnabled);
        Assert.Empty(restored.RoamingAccounts[84].Subscriptions);
        await using var db = await f.OpenDatabaseAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM extension_state WHERE id=1";
        var payload = Assert.IsType<string>(await command.ExecuteScalarAsync(Token));
        using var document = JsonDocument.Parse(payload);
        Assert.False(document.RootElement.TryGetProperty("Entries", out _));
        Assert.False(document.RootElement.TryGetProperty("Sources", out _));
        Assert.False(document.RootElement.TryGetProperty("Playback", out _));
        Assert.DoesNotContain("SESSDATA", payload);
        Assert.DoesNotContain("https://", payload);
    }

    [Fact]
    public async Task FailedWriteRollsBackBothExtensionAndExistingQueueInOneTransaction()
    {
        using var f = new StoreFixture();
        var original = RoamingState();
        await f.Store.SaveAsync(original, Token);
        var invalid = RoamingState();
        invalid.RoamingAccounts[42].ScheduleTime = TimeSpan.FromHours(15);
        invalid.ManualPlayback = new(PositionSeconds: 99);
        invalid.Queue.Add(invalid.Queue[0]);

        await Assert.ThrowsAsync<SqliteException>(() => f.Store.SaveAsync(invalid, Token));

        var restored = await f.Store.LoadAsync(Token);
        Assert.Equal(original.ManualPlayback, restored.ManualPlayback);
        Assert.Equal(original.RoamingAccounts[42].ScheduleTime, restored.RoamingAccounts[42].ScheduleTime);
        Assert.Equal(original.Queue, restored.Queue);
    }

    [Fact]
    public async Task RejectsCorruptExtendedPlaybackWithoutPartiallyReturningTheLibrary()
    {
        using var f = new StoreFixture();
        await f.Store.SaveAsync(RoamingState(), Token);
        await using (var db = await f.OpenDatabaseAsync())
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE extension_state SET payload=json_set(payload,'$.ManualPlayback.Volume',101) WHERE id=1";
            await command.ExecuteNonQueryAsync(Token);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.LoadAsync(Token));
    }

    [Theory]
    [InlineData("$.ActiveSource", "99")]
    [InlineData("$.RoamingAccounts.42.ScheduleTime", "\"-00:01:00\"")]
    [InlineData("$.RoamingAccounts.42.ScheduleTime", "\"1.00:00:00\"")]
    [InlineData("$.ManualQueue[0].Key.Cid", "999")]
    [InlineData("$.RoamingAccounts.42.Queue[0].Key.Cid", "999")]
    [InlineData("$.ManualQueue[0].CompletedCount", "-1")]
    public async Task RejectsCorruptScheduleSourceAndInactiveQueueReferences(string path, string value)
    {
        using var f = new StoreFixture();
        await f.Store.SaveAsync(RoamingState(), Token);
        await using (var db = await f.OpenDatabaseAsync())
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE extension_state SET payload=json_set(payload,$path,json($value)) WHERE id=1";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(Token);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.LoadAsync(Token));
    }

    private static LibraryState RoamingState()
    {
        var manualKey = new ContentKey("BV1GJ411x7h7", 101);
        var roamingKey = new ContentKey("BV1xx411c7mD", 202);
        var manual = new QueueItem(Guid.NewGuid(), manualKey, 1);
        var roaming = new QueueItem(Guid.NewGuid(), roamingKey);
        var now = new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero);
        return new()
        {
            Entries = [new(manualKey, "Manual", "Creator", 1, TimeSpan.FromSeconds(60)),
                new(roamingKey, "Roaming", "Creator", 1, TimeSpan.FromSeconds(80))],
            Queue = [roaming], Playback = new(roaming.Id, 7, 65, PlaybackIntent.Playing),
            ActiveSource = PlaybackSource.Roaming, ActiveRoamingAccountId = 42,
            ManualQueue = [manual], ManualPlayback = new(manual.Id, 23, 65, PlaybackIntent.Paused),
            RoamingAccounts =
            {
                [42] = new()
                {
                    Subscriptions = [new(7, "Creator")],
                    Videos = [new(new(roamingKey.Bvid, 7, "Creator", "Roaming", now, TimeSpan.FromSeconds(80)), true, now, now)],
                    Sync = { [7] = new(true, now.AddDays(-1), 3, now, now.AddDays(-1), "Network") },
                    Queue = [roaming], Playback = new(roaming.Id, 7, 65, PlaybackIntent.Playing),
                    ExcludedBvids = [roamingKey.Bvid], RoundActive = true, ScheduleEnabled = true,
                    ScheduleTime = TimeSpan.FromHours(10), LastScheduleDate = new(2026, 10, 17), LastRefreshDate = new(2026, 10, 16)
                },
                [84] = new()
            }
        };
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wallbili-storage-tests-" + Guid.NewGuid().ToString("N")));
        public string DatabasePath => Path.Combine(_directory, "library.db");
        public SqliteLibraryStore Store => new(DatabasePath);
        public StoreFixture() => Directory.CreateDirectory(_directory);
        public async Task<SqliteConnection> OpenDatabaseAsync()
        {
            var db = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            await db.OpenAsync(Token);
            return db;
        }
        public void Dispose()
        {
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!_directory.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(_directory).StartsWith("wallbili-storage-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test artifact directory.");
            Directory.Delete(_directory, recursive: true);
        }
    }
}

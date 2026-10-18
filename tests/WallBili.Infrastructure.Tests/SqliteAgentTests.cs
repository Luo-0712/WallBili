using System.Text.Json;
using Microsoft.Data.Sqlite;
using WallBili.Core;
using WallBili.Infrastructure.Storage;
using Xunit;

namespace WallBili.Infrastructure.Tests;

public sealed class SqliteAgentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MigratesVersionTwoWithoutRewritingCatalogQueuePlaybackOrRoamingExtension()
    {
        using var fixture = new StoreFixture();
        var original = LibraryWithPlaylist();
        original.Agent = new();
        await fixture.Store.SaveAsync(original, Token);
        string extension;
        string playback;
        await using (var db = await fixture.OpenAsync())
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT payload FROM extension_state WHERE id=1";
            extension = Assert.IsType<string>(await command.ExecuteScalarAsync(Token));
            command.CommandText = "SELECT payload FROM playback WHERE id=1";
            playback = Assert.IsType<string>(await command.ExecuteScalarAsync(Token));
            command.CommandText = "DROP TABLE agent_state; PRAGMA user_version=2;";
            await command.ExecuteNonQueryAsync(Token);
        }

        var restored = await fixture.Store.LoadAsync(Token);

        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored));
        Assert.Empty(restored.Agent.Playlists);
        Assert.Empty(restored.Agent.Tasks);
        Assert.Empty(restored.Agent.Receipts);
        await using var verify = await fixture.OpenAsync();
        using var check = verify.CreateCommand();
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, await check.ExecuteScalarAsync(Token));
        check.CommandText = "SELECT payload FROM extension_state WHERE id=1";
        Assert.Equal(extension, await check.ExecuteScalarAsync(Token));
        check.CommandText = "SELECT payload FROM playback WHERE id=1";
        Assert.Equal(playback, await check.ExecuteScalarAsync(Token));
        check.CommandText = "SELECT COUNT(*) FROM agent_state";
        Assert.Equal(0L, await check.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task RoundTripsNamedPlaylistOrderedPartsSourceExclusionsTaskAndIdempotencyReceipt()
    {
        using var fixture = new StoreFixture();
        var original = LibraryWithPlaylist();

        await fixture.Store.SaveAsync(original, Token);
        var restored = await fixture.Store.LoadAsync(Token);

        Assert.Equal(JsonSerializer.Serialize(original.Agent), JsonSerializer.Serialize(restored.Agent));
        Assert.Equal(original.Queue, restored.Queue);
        Assert.Equal(original.Playback, restored.Playback);
        var playlist = Assert.Single(restored.Agent.Playlists);
        Assert.Equal("摄影入门", playlist.Name);
        Assert.Equal(3, playlist.Revision);
        Assert.Equal(new CollectionSource(7, 11), new CollectionSource(
            playlist.Inputs[0].UploaderId!.Value, playlist.Inputs[0].CollectionId!.Value, playlist.Inputs[0].CollectionKind!.Value));
        Assert.Equal(new ContentKey("BV1GJ411x7h7", 101), Assert.Single(playlist.Members[0].Parts));
        Assert.Equal("Removed", playlist.Members[1].Failure);
        Assert.Single(playlist.Excluded);
        Assert.Equal("video:BV1xx411c7mD", Assert.Single(playlist.ExcludedReferences!));
        var task = Assert.Single(restored.Agent.Tasks);
        Assert.Equal(playlist.Id, task.PlaylistId);
        Assert.Equal("partial", task.State);
        Assert.Equal(2, task.Processed);
        var receipt = restored.Agent.Receipts["create-photography-1"];
        var result = JsonSerializer.Deserialize<AgentResult>(receipt.Result, AgentJson.Options)!;
        Assert.False(result.IsError);
        Assert.Equal(playlist.Id, result.Data.GetProperty("playlistId").GetGuid());
        Assert.Equal(task.Id, result.Data.GetProperty("taskId").GetGuid());

        await using var db = await fixture.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM agent_state WHERE id=1";
        var payload = Assert.IsType<string>(await command.ExecuteScalarAsync(Token));
        Assert.DoesNotContain("SESSDATA", payload);
        Assert.DoesNotContain("https://", payload);
        Assert.DoesNotContain("MediaSession", payload);
    }

    [Theory]
    [InlineData("$.QueueRevision", "-1")]
    [InlineData("$.Playlists", "null")]
    [InlineData("$.Playlists[0].Id", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("$.Playlists[0].Revision", "-1")]
    [InlineData("$.Playlists[0].Inputs", "null")]
    [InlineData("$.Playlists[0].Members[0].Parts[0].Cid", "0")]
    [InlineData("$.Tasks[0].State", "\"unrecognized\"")]
    [InlineData("$.Tasks[0].Processed", "-1")]
    [InlineData("$.Receipts", "null")]
    public async Task RejectsCorruptAgentPayloadWithoutReturningAPartialLibrary(string path, string value)
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync(LibraryWithPlaylist(), Token);
        await using (var db = await fixture.OpenAsync())
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE agent_state SET payload=json_set(payload,$path,json($value)) WHERE id=1";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(Token);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.LoadAsync(Token));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    public async Task RejectsUnreadableOrEmptyAgentRecords(string payload)
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync(LibraryWithPlaylist(), Token);
        await using (var db = await fixture.OpenAsync())
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE agent_state SET payload=$payload WHERE id=1";
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync(Token);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.LoadAsync(Token));
    }

    [Fact]
    public async Task FailedAgentWriteRollsBackQueuePlaybackAndRoamingChanges()
    {
        using var fixture = new StoreFixture();
        var original = LibraryWithPlaylist();
        await fixture.Store.SaveAsync(original, Token);
        await using (var db = await fixture.OpenAsync())
        {
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_agent BEFORE INSERT ON agent_state BEGIN SELECT RAISE(FAIL,'test write failure'); END;";
            await command.ExecuteNonQueryAsync(Token);
        }
        var changed = LibraryWithPlaylist();
        changed.Playback = changed.Playback with { PositionSeconds = 89 };
        changed.RoamingAccounts[42].ScheduleTime = TimeSpan.FromHours(15);
        changed.Agent.Playlists[0] = changed.Agent.Playlists[0] with { Name = "Changed" };

        await Assert.ThrowsAsync<SqliteException>(() => fixture.Store.SaveAsync(changed, Token));

        var restored = await fixture.Store.LoadAsync(Token);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored));
    }

    private static LibraryState LibraryWithPlaylist()
    {
        var key = new ContentKey("BV1GJ411x7h7", 101);
        var queue = new QueueItem(Guid.NewGuid(), key, 2);
        var playback = new PlaybackState(queue.Id, 27.5, 62, PlaybackIntent.Paused, QueueMode.RepeatAll);
        var playlistId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        ContentRef[] inputs = [new("collection", UploaderId: 7, CollectionId: 11, CollectionKind: CollectionKind.Season),
            new("video", Bvid: "BV1xx411c7mD")];
        var result = AgentResult.Success("Playlist accepted.", new { playlistId, taskId });
        return new()
        {
            Entries = [new(key, "Light", "Creator", 1, TimeSpan.FromSeconds(90))],
            Sources = [new("video-source", "Light", key.Bvid, null, DateTimeOffset.UnixEpoch, [key], [])],
            Queue = [queue], Playback = playback, ManualQueue = [queue], ManualPlayback = playback,
            RoamingAccounts = { [42] = new() { Subscriptions = [new(7, "Creator")], ScheduleTime = TimeSpan.FromHours(9) } },
            Agent = new()
            {
                QueueRevision = 7,
                Playlists = [new(playlistId, "摄影入门", 3, inputs,
                    [new(Guid.NewGuid(), new("video", Bvid: key.Bvid), "Light", [key]),
                     new(Guid.NewGuid(), new("video", Bvid: "BV1xx411c7mD"), "Unavailable", [], "Removed")],
                    [new(key.Bvid, 202)], taskId, DateTimeOffset.UnixEpoch, ["video:BV1xx411c7mD"])],
                Tasks = [new(taskId, playlistId, inputs, "partial", 2, 3, "Removed")],
                Receipts = { ["create-photography-1"] = new(new string('A', 64), JsonSerializer.SerializeToElement(result, AgentJson.Options)) }
            }
        };
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wallbili-agent-store-" + Guid.NewGuid().ToString("N")));
        private string DatabasePath => Path.Combine(_directory, "library.db");
        public SqliteLibraryStore Store => new(DatabasePath);
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
                !Path.GetFileName(_directory).StartsWith("wallbili-agent-store-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test artifact directory.");
            Directory.Delete(_directory, recursive: true);
        }
    }
}

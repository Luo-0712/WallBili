using Microsoft.Data.Sqlite;
using WallBili.Core;
using WallBili.Infrastructure.Bilibili;
using Xunit;

namespace WallBili.Library.Tests;

public sealed class LibraryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task TwoPagedSourcesExpandPartsAndRefreshWithoutDuplicatesOrResettingPlayback()
    {
        await using var f = new LibraryFixture();
        await f.SeedAsync();
        Assert.Equal(2, f.Session.Snapshot.Sources.Count);
        Assert.Equal(4, f.Session.Snapshot.Entries.Count);
        Assert.Equal(new long[] { 11, 12, 21, 31 }, f.Session.Snapshot.Queue.Select(q => q.Key.Cid));
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(17), token: Token);
        var current = f.Session.Snapshot.Playback;
        var queue = f.Session.Snapshot.Queue.ToArray();
        f.Content.Collections[10].Add(TestContent.D);
        var source = f.Session.Snapshot.Sources[0].Id;
        await f.Session.RefreshSourceAsync(source, token: Token);
        await f.Session.RefreshSourceAsync(source, token: Token);
        Assert.Equal(5, f.Session.Snapshot.Entries.Count);
        Assert.Equal(4, f.Session.Snapshot.Sources[0].Members.Count);
        Assert.Equal(queue, f.Session.Snapshot.Queue);
        Assert.Equal(current, f.Session.Snapshot.Playback);
        Assert.Single(f.Player.Loads);
        Assert.Equal(5, (await f.Store.LoadAsync(cancellationToken: Token)).Entries.Count);
    }

    [Fact]
    public async Task InterruptedRefreshIsAtomicAndUnavailableMetadataDoesNotInventCids()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        var source = f.Session.Snapshot.Sources[0];
        f.Content.MetadataFailures[TestContent.B] = ContentFailureKind.Network;
        await Assert.ThrowsAsync<ContentAccessException>(() => f.Session.RefreshSourceAsync(source.Id, token: Token));
        Assert.Equal(source, f.Session.Snapshot.Sources[0]);
        f.Content.MetadataFailures[TestContent.B] = ContentFailureKind.Removed;
        f.Content.Collections[10].Add(TestContent.D);
        f.Content.MetadataFailures[TestContent.D] = ContentFailureKind.AuthenticationRequired;
        await f.Session.RefreshSourceAsync(source.Id, token: Token);
        Assert.Equal(3, f.Session.Snapshot.Sources[0].Members.Count);
        Assert.Equal(2, f.Session.Snapshot.Sources[0].Problems.Count);
        Assert.DoesNotContain(f.Session.Snapshot.Entries, e => e.Key.Bvid == TestContent.D);
    }

    [Fact]
    public async Task RepeatingPaginationIsRejectedWithoutPartialImport()
    {
        await using var f = new LibraryFixture(); await f.Session.InitializeAsync(token: Token);
        f.Content.RepeatedPage = true;
        await Assert.ThrowsAsync<ContentAccessException>(() => f.Session.ImportCollectionAsync(new(1, 10), token: Token));
        Assert.Empty(f.Session.Snapshot.Entries);
        Assert.Empty((await f.Store.LoadAsync(cancellationToken: Token)).Sources);
    }

    [Fact]
    public async Task EditingAndDeletingCurrentItemPreservesIdentityAndUsesSuccessor()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.PlayAsync(token: Token); await f.Session.SeekAsync(TimeSpan.FromSeconds(9), token: Token);
        var id = f.Session.Snapshot.Playback.CurrentId!.Value;
        await f.Session.MoveQueueAsync(id, 1, token: Token);
        Assert.Equal(id, f.Session.Snapshot.Queue[1].Id);
        Assert.Equal(9, f.Session.Snapshot.Playback.PositionSeconds);
        await f.Session.PauseAsync(token: Token);
        var successor = f.Session.Snapshot.Queue[2];
        await f.Session.RemoveQueueAsync(id, token: Token);
        Assert.Equal(successor.Id, f.Session.Snapshot.Playback.CurrentId);
        Assert.Equal(PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
        Assert.True(f.Player.Paused);
        Assert.Equal(0, f.Session.Snapshot.Playback.PositionSeconds);
        await f.Session.SetModeAsync(QueueMode.RepeatAll, token: Token);
        await f.Session.PlayAsync(f.Session.Snapshot.Queue[^1].Id, token: Token);
        await f.Session.RemoveQueueAsync(f.Session.Snapshot.Playback.CurrentId!.Value, token: Token);
        Assert.Equal(PlaybackIntent.Stopped, f.Session.Snapshot.Playback.Intent);
        Assert.Null(f.Session.Snapshot.Playback.CurrentId);
    }

    [Fact]
    public async Task CatalogDeletionRemovesDuplicatesAndMembershipsButNotOtherContent()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        var key = f.Session.Snapshot.Entries[0].Key;
        await f.Session.EnqueueAsync([key], token: Token);
        Assert.Equal(5, f.Session.Snapshot.Queue.Select(q => q.Id).Distinct().Count());
        await f.Session.PlayAsync(token: Token);
        await f.Session.RemoveEntryAsync(key, token: Token);
        Assert.DoesNotContain(f.Session.Snapshot.Queue, q => q.Key == key);
        Assert.DoesNotContain(f.Session.Snapshot.Entries, e => e.Key == key);
        Assert.All(f.Session.Snapshot.Sources, s => Assert.DoesNotContain(key, s.Members));
        Assert.Equal(12, f.Session.Snapshot.Current!.Key.Cid);
        await f.Session.RemoveSourceAsync(f.Session.Snapshot.Sources[0].Id, token: Token);
        Assert.Equal(3, f.Session.Snapshot.Entries.Count);
    }

    [Fact]
    public async Task OnlyCurrentEofAdvancesAndOnlyEofRecordsCompletion()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync(); await f.Session.PlayAsync(token: Token);
        var old = f.Player.Generation;
        var first = f.Session.Snapshot.Queue[0].Id;
        await f.Session.SeekAsync(TimeSpan.FromSeconds(60), token: Token);
        Assert.Equal(first, f.Session.Snapshot.Playback.CurrentId);
        Assert.All(f.Session.Snapshot.Queue, q => Assert.Equal(0, q.CompletedCount));
        f.Player.End();
        await UntilAsync(() => f.Session.Snapshot.Queue[0].CompletedCount == 1 &&
            f.Session.Snapshot.Playback.CurrentId == f.Session.Snapshot.Queue[1].Id);
        var second = f.Session.Snapshot.Playback.CurrentId;
        f.Player.End(generation: old);
        await f.Session.CheckpointAsync(token: Token);
        Assert.Equal(second, f.Session.Snapshot.Playback.CurrentId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            f.Player.End("error");
            await UntilAsync(() => f.Session.Snapshot.RecoveryPending);
            Assert.Equal(second, f.Session.Snapshot.Playback.CurrentId);
            await f.Session.RetryRecoveryAsync(immediate: true, token: Token);
        }
        f.Player.End("error"); // Repeated media errors eventually skip, without a false completion.
        await UntilAsync(() => f.Session.Snapshot.Playback.CurrentId == f.Session.Snapshot.Queue[2].Id);
        Assert.Equal(0, f.Session.Snapshot.Queue[1].CompletedCount);
        Assert.Equal(EntryAvailability.Failed, f.Session.Snapshot.Entries[1].Availability);
    }

    [Fact]
    public async Task RepeatModeWrapsAndAllFailuresTerminateWithoutInfiniteRetry()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.SetModeAsync(QueueMode.RepeatAll, token: Token);
        await f.Session.PlayAsync(f.Session.Snapshot.Queue[^1].Id, token: Token);
        f.Player.End();
        await UntilAsync(() => f.Session.Snapshot.Playback.CurrentId == f.Session.Snapshot.Queue[0].Id);
        foreach (var entry in f.Session.Snapshot.Entries) f.Content.MediaFailures.Add(entry.Key);
        f.Content.Resolved.Clear();
        await f.Session.PlayAsync(f.Session.Snapshot.Queue[0].Id, token: Token);
        Assert.Equal(4, f.Content.Resolved.Count);
        Assert.Equal(PlaybackIntent.Stopped, f.Session.Snapshot.Playback.Intent);
        Assert.All(f.Session.Snapshot.Entries, e => Assert.Equal(EntryAvailability.Failed, e.Availability));
        Assert.Equal(1, f.Session.Snapshot.Queue.Sum(q => q.CompletedCount));
    }

    [Theory]
    [InlineData(PlaybackIntent.Playing)]
    [InlineData(PlaybackIntent.Paused)]
    [InlineData(PlaybackIntent.Stopped)]
    public async Task CheckpointsRestoreCurrentPositionVolumeAndExplicitStopPolicy(PlaybackIntent intent)
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.PlayAsync(f.Session.Snapshot.Queue[2].Id, token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(23), token: Token);
        await f.Session.SetVolumeAsync(42, token: Token);
        if (intent == PlaybackIntent.Paused) await f.Session.PauseAsync(token: Token);
        if (intent == PlaybackIntent.Stopped) await f.Session.StopAsync(token: Token);
        await f.Session.CheckpointAsync(token: Token);
        await f.Session.DisposeAsync();
        var player = new TestPlayer();
        await using var restored = new LibrarySession(f.Content, f.Store, player);
        await restored.InitializeAsync(token: Token);
        Assert.Equal(f.Session.Snapshot.Playback.CurrentId, restored.Snapshot.Playback.CurrentId);
        Assert.Equal(intent, restored.Snapshot.Playback.Intent);
        Assert.Equal(42, restored.Snapshot.Playback.Volume);
        Assert.Equal(intent == PlaybackIntent.Stopped ? 0 : 23, restored.Snapshot.Playback.PositionSeconds);
        if (intent == PlaybackIntent.Stopped) Assert.Empty(player.Loads);
        else
        {
            Assert.Equal(TimeSpan.FromSeconds(23), Assert.Single(player.Loads).Position);
            Assert.Equal(intent == PlaybackIntent.Paused, player.Paused);
        }
        var bytes = await File.ReadAllBytesAsync(f.DatabasePath, cancellationToken: Token);
        Assert.DoesNotContain("temporary-secret", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("https://", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task PlayerConfigurationFailureDoesNotMarkEveryVideoInvalid()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Player.FailLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Session.PlayAsync(token: Token));
        Assert.Single(f.Content.Resolved);
        Assert.Equal(PlaybackIntent.Paused, f.Session.Snapshot.Playback.Intent);
        Assert.All(f.Session.Snapshot.Entries, e => Assert.Equal(EntryAvailability.Unknown, e.Availability));
        Assert.All(f.Session.Snapshot.Queue, q => Assert.Equal(0, q.CompletedCount));
    }

    [Fact]
    public async Task EofArrivingBeforeLoadReturnsIsNotLost()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        f.Player.EndDuringNextLoad = true;
        await f.Session.PlayAsync(token: Token);
        await UntilAsync(() => f.Session.Snapshot.Playback.CurrentId == f.Session.Snapshot.Queue[1].Id);
        Assert.Equal(1, f.Session.Snapshot.Queue[0].CompletedCount);
        Assert.Equal(0, f.Session.Snapshot.Queue[1].CompletedCount);
    }

    [Fact]
    public async Task UnexpectedPlayerExitPreservesProgressAndDoesNotClaimCompletion()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await f.Session.PlayAsync(token: Token);
        await f.Session.SeekAsync(TimeSpan.FromSeconds(18), Token);
        f.Player.End("disconnect");
        await UntilAsync(() => f.Session.Snapshot.RecoveryPending);
        Assert.Equal(PlaybackIntent.Playing, f.Session.Snapshot.Playback.Intent);
        Assert.Equal(18, f.Session.Snapshot.Playback.PositionSeconds);
        Assert.Single(f.Content.Resolved);
        Assert.All(f.Session.Snapshot.Queue, q => Assert.Equal(0, q.CompletedCount));
        await f.Session.RetryRecoveryAsync(immediate: true, token: Token);
        Assert.Equal(TimeSpan.FromSeconds(18), f.Player.Loads[^1].Position);
    }

    [Fact]
    public async Task InvalidEnqueueIsAtomicAndUninitializedSessionCannotOverwriteExistingLibrary()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Session.EnqueueAsync(
            [f.Session.Snapshot.Entries[0].Key, new("unknown", 999)], Token));
        Assert.Equal(4, f.Session.Snapshot.Queue.Count);
        await using (var unopened = new LibrarySession(f.Content, f.Store, new TestPlayer()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => unopened.StopAsync(Token));
        Assert.Equal(4, (await f.Store.LoadAsync(Token)).Queue.Count);
    }

    [Fact]
    public async Task FailedSnapshotWriteRollsBackAndNewerSchemaIsNeverOverwritten()
    {
        await using var f = new LibraryFixture(); await f.SeedAsync();
        var invalid = await f.Store.LoadAsync(cancellationToken: Token);
        invalid.Queue.Add(invalid.Queue[0]);
        await Assert.ThrowsAsync<SqliteException>(() => f.Store.SaveAsync(invalid, cancellationToken: Token));
        Assert.Equal(4, (await f.Store.LoadAsync(cancellationToken: Token)).Queue.Count);
        await using (var db = new SqliteConnection($"Data Source={f.DatabasePath};Pooling=False"))
        {
            await db.OpenAsync(cancellationToken: Token); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=99";
            await command.ExecuteNonQueryAsync(cancellationToken: Token);
        }
        await using (var other = new LibrarySession(f.Content, f.Store, new TestPlayer()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.InitializeAsync(token: Token));
        await using (var db = new SqliteConnection($"Data Source={f.DatabasePath};Pooling=False"))
        {
            await db.OpenAsync(cancellationToken: Token); using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM queue";
            Assert.Equal(4L, await command.ExecuteScalarAsync(cancellationToken: Token));
            command.CommandText = "PRAGMA user_version=1"; await command.ExecuteNonQueryAsync(cancellationToken: Token);
        }
    }

    [Theory]
    [InlineData("https://space.bilibili.com/123/lists/456?type=season", CollectionKind.Season)]
    [InlineData("https://space.bilibili.com/123/lists/456?type=series", CollectionKind.Series)]
    [InlineData("https://space.bilibili.com/123/channel/seriesdetail?sid=456", CollectionKind.Series)]
    [InlineData("https://space.bilibili.com/123/channel/collectiondetail?sid=456", CollectionKind.Season)]
    public void CollectionLinksIdentifyKindAndOwner(string input, CollectionKind kind) =>
        Assert.Equal(new CollectionSource(123, 456, kind), BilibiliCollectionInput.Parse(input));

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}

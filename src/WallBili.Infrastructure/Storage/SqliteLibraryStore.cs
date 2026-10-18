using System.Text.Json;
using Microsoft.Data.Sqlite;
using WallBili.Core;

namespace WallBili.Infrastructure.Storage;

/// <summary>Small-library transactional snapshots. No temporary playback resources are accepted by this schema.</summary>
public sealed class SqliteLibraryStore(string path) : ILibraryStore
{
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fullPath, ForeignKeys = true, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var current = Convert.ToInt32(await version.ExecuteScalarAsync(ct));
            if (current > 3) throw new InvalidOperationException("The library database requires a newer WallBili version.");
            if (current < 2)
            {
                using var transaction = connection.BeginTransaction();
                using var schema = connection.CreateCommand();
                schema.Transaction = transaction;
                schema.CommandText = current == 0 ? """
                    CREATE TABLE entries(bvid TEXT NOT NULL, cid INTEGER NOT NULL, ordinal INTEGER NOT NULL,
                        payload TEXT NOT NULL, PRIMARY KEY(bvid,cid));
                    CREATE TABLE sources(id TEXT PRIMARY KEY, ordinal INTEGER NOT NULL, payload TEXT NOT NULL);
                    CREATE TABLE queue(id TEXT PRIMARY KEY, ordinal INTEGER NOT NULL UNIQUE,
                        bvid TEXT NOT NULL, cid INTEGER NOT NULL, completed INTEGER NOT NULL CHECK(completed>=0),
                        FOREIGN KEY(bvid,cid) REFERENCES entries(bvid,cid));
                    CREATE TABLE playback(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
                    """ : "";
                schema.CommandText += """
                    CREATE TABLE IF NOT EXISTS extension_state(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
                    PRAGMA user_version=2;
                    """;
                await schema.ExecuteNonQueryAsync(ct);
                transaction.Commit();
            }
            if (current < 3)
            {
                using var migration = connection.BeginTransaction();
                using var schema = connection.CreateCommand();
                schema.Transaction = migration;
                schema.CommandText = "CREATE TABLE IF NOT EXISTS agent_state(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL); PRAGMA user_version=3;";
                await schema.ExecuteNonQueryAsync(ct);
                migration.Commit();
            }
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        using var transaction = db.BeginTransaction();
        var state = new LibraryState();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM entries ORDER BY ordinal";
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) state.Entries.Add(Parse<CatalogEntry>(reader.GetString(0)));
        command.CommandText = "SELECT payload FROM sources ORDER BY ordinal";
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) state.Sources.Add(Parse<CatalogSource>(reader.GetString(0)));
        command.CommandText = "SELECT id,bvid,cid,completed FROM queue ORDER BY ordinal";
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                state.Queue.Add(new(Guid.Parse(reader.GetString(0)), new(reader.GetString(1), reader.GetInt64(2)), reader.GetInt32(3)));
        command.CommandText = "SELECT payload FROM playback WHERE id=1";
        if (await command.ExecuteScalarAsync(cancellationToken) is string playback)
            state.Playback = Parse<PlaybackState>(playback);
        ValidatePlayback(state.Playback);
        command.CommandText = "SELECT payload FROM extension_state WHERE id=1";
        if (await command.ExecuteScalarAsync(cancellationToken) is string extension)
        {
            var additional = Parse<ExtensionState>(extension);
            ValidateExtension(additional, state.Entries);
            state.RoamingAccounts = additional.RoamingAccounts;
            state.ActiveSource = additional.ActiveSource;
            state.ManualQueue = additional.ManualQueue;
            state.ManualPlayback = additional.ManualPlayback;
            state.ActiveRoamingAccountId = additional.ActiveRoamingAccountId;
        }
        else
        {
            // Version-one databases contained only the manual context. Keep it available immediately
            // after migration, without rewriting any existing catalog, queue or playback records.
            state.ManualQueue = state.Queue.ToList();
            state.ManualPlayback = state.Playback;
        }
        command.CommandText = "SELECT payload FROM agent_state WHERE id=1";
        if (await command.ExecuteScalarAsync(cancellationToken) is string agent)
            state.Agent = Parse<AgentLibraryState>(agent);
        ValidateAgent(state.Agent);
        transaction.Commit();
        return state;
    }

    public async Task SaveAsync(LibraryState state, CancellationToken cancellationToken = default)
    {
        var additional = new ExtensionState
        {
            RoamingAccounts = state.RoamingAccounts, ActiveSource = state.ActiveSource,
            ManualQueue = state.ManualQueue, ManualPlayback = state.ManualPlayback,
            ActiveRoamingAccountId = state.ActiveRoamingAccountId
        };
        ValidatePlayback(state.Playback);
        ValidateExtension(additional, state.Entries);
        ValidateAgent(state.Agent);
        await using var db = await OpenAsync(cancellationToken);
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM queue; DELETE FROM entries; DELETE FROM sources; DELETE FROM playback; DELETE FROM extension_state;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            await InsertAsync("INSERT INTO entries VALUES($a,$b,$c,$d)", cancellationToken,
                entry.Key.Bvid, entry.Key.Cid, i, JsonSerializer.Serialize(entry));
        }
        for (var i = 0; i < state.Sources.Count; i++)
            await InsertAsync("INSERT INTO sources VALUES($a,$b,$c)", cancellationToken,
                state.Sources[i].Id, i, JsonSerializer.Serialize(state.Sources[i]));
        for (var i = 0; i < state.Queue.Count; i++)
        {
            var item = state.Queue[i];
            await InsertAsync("INSERT INTO queue VALUES($a,$b,$c,$d,$e)", cancellationToken,
                item.Id.ToString(), i, item.Key.Bvid, item.Key.Cid, item.CompletedCount);
        }
        await InsertAsync("INSERT INTO playback VALUES(1,$a)", cancellationToken, JsonSerializer.Serialize(state.Playback));
        await InsertAsync("INSERT INTO extension_state VALUES(1,$a)", cancellationToken, JsonSerializer.Serialize(additional));
        await InsertAsync("INSERT OR REPLACE INTO agent_state VALUES(1,$a)", cancellationToken, JsonSerializer.Serialize(state.Agent));
        transaction.Commit();

        async Task InsertAsync(string sql, CancellationToken ct, params object[] values)
        {
            command.CommandText = sql;
            command.Parameters.Clear();
            for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$" + (char)('a' + i), values[i]);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    // Only the new stable identities and checkpoints are persisted here. Do not serialize LibraryState:
    // its catalog and active queue already have their own tables, and it may grow transient fields later.
    private sealed class ExtensionState
    {
        public Dictionary<long, RoamingAccountState> RoamingAccounts { get; set; } = [];
        public PlaybackSource ActiveSource { get; set; } = PlaybackSource.Manual;
        public List<QueueItem> ManualQueue { get; set; } = [];
        public PlaybackState ManualPlayback { get; set; } = new();
        public long? ActiveRoamingAccountId { get; set; }
    }

    private static void ValidatePlayback(PlaybackState playback)
    {
        if (playback is null || !double.IsFinite(playback.PositionSeconds) || playback.PositionSeconds < 0 ||
            !double.IsFinite(playback.Volume) || playback.Volume is < 0 or > 100 ||
            !Enum.IsDefined(playback.Intent) || !Enum.IsDefined(playback.Mode))
            throw new InvalidDataException("The playback checkpoint is invalid.");
    }

    private static void ValidateAgent(AgentLibraryState state)
    {
        if (state is null || state.Playlists is null || state.Tasks is null || state.Receipts is null || state.RemovedSources is null || state.QueueRevision < 0 ||
            state.Playlists.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Name) || p.Revision < 0 ||
                p.Inputs is null || p.Members is null || p.Excluded is null ||
                p.Inputs.Any(r => !ValidReference(r)) || p.Excluded.Any(k => !ValidKey(k)) ||
                p.Members.Select(m => m?.Id).Distinct().Count() != p.Members.Length ||
                p.Members.Any(m => m is null || m.Id == Guid.Empty || m.Reference is null || m.Parts is null ||
                    !ValidReference(m.Reference) || m.Reference.Kind is not ("video" or "part") ||
                    m.Parts.Any(k => !ValidKey(k) || k.Bvid != m.Reference.Bvid || m.Reference.Kind == "part" && k.Cid != m.Reference.Cid))) ||
            state.Playlists.Select(p => p.Id).Distinct().Count() != state.Playlists.Count ||
            state.Tasks.Select(t => t?.Id).Distinct().Count() != state.Tasks.Count ||
            state.Tasks.Any(t => t is null || t.Id == Guid.Empty || t.Inputs is null || t.Processed < 0 || t.Total is < 0 ||
                t.Inputs.Any(r => !ValidReference(r)) ||
                t.State is not ("pending" or "running" or "completed" or "partial" or "failed" or "cancelled" or "interrupted")))
            throw new InvalidDataException("The saved assistant library is invalid.");
        foreach (var (id, receipt) in state.Receipts)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || receipt is null || string.IsNullOrWhiteSpace(receipt.Fingerprint) ||
                receipt.Result.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A saved command receipt is invalid.");
            try
            {
                var result = receipt.Result.Deserialize<AgentResult>(AgentJson.Options);
                if (result is null || result.Summary is null || result.Data.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("A saved command result is invalid.");
            }
            catch (JsonException) { throw new InvalidDataException("A saved command result is unreadable."); }
        }
    }

    private static bool ValidKey(ContentKey? key) => key is { Cid: > 0 } && ValidBvid(key.Bvid);
    private static bool ValidBvid(string? bvid) => bvid is { Length: 12 } && bvid.StartsWith("BV", StringComparison.Ordinal) &&
        bvid.AsSpan(2).ToArray().All(char.IsAsciiLetterOrDigit);
    private static bool ValidReference(ContentRef? reference) => reference is not null && reference.Kind switch
    {
        "video" => ValidBvid(reference.Bvid),
        "part" => ValidBvid(reference.Bvid) && reference.Cid is > 0,
        "collection" => reference.UploaderId is > 0 && reference.CollectionId is > 0 &&
            (reference.CollectionKind is null || Enum.IsDefined(reference.CollectionKind.Value)),
        "playlist" => reference.PlaylistId is { } id && id != Guid.Empty,
        _ => false
    };

    private static void ValidateExtension(ExtensionState additional, IReadOnlyList<CatalogEntry> entries)
    {
        if (!Enum.IsDefined(additional.ActiveSource) || additional.RoamingAccounts is null || additional.ManualQueue is null ||
            additional.ActiveRoamingAccountId is <= 0 ||
            additional.ActiveSource == PlaybackSource.Roaming &&
                (additional.ActiveRoamingAccountId is not { } accountId || !additional.RoamingAccounts.ContainsKey(accountId)))
            throw new InvalidDataException("The subscription playback checkpoint is invalid.");
        ValidatePlayback(additional.ManualPlayback);
        var keys = entries.Select(entry => entry.Key).ToHashSet();
        ValidateStoredQueue(additional.ManualQueue, keys);
        foreach (var (id, account) in additional.RoamingAccounts)
        {
            if (id <= 0 || account is null || account.Subscriptions is null || account.Videos is null || account.Sync is null ||
                account.Queue is null || account.ExcludedBvids is null || account.ScheduleTime < TimeSpan.Zero ||
                account.ScheduleTime >= TimeSpan.FromDays(1))
                throw new InvalidDataException("The subscription account checkpoint is invalid.");
            ValidatePlayback(account.Playback);
            ValidateStoredQueue(account.Queue, keys);
        }
    }

    private static void ValidateStoredQueue(IReadOnlyList<QueueItem> queue, HashSet<ContentKey> catalogKeys)
    {
        if (queue.Any(item => item is null || item.Key is null || item.Id == Guid.Empty || item.CompletedCount < 0 ||
                !catalogKeys.Contains(item.Key)) || queue.Select(item => item.Id).Distinct().Count() != queue.Count)
            throw new InvalidDataException("A saved inactive queue contains invalid catalog references or counters.");
    }

    private static T Parse<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("The library contains an empty record.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The library contains an unreadable record.");
        }
    }
}

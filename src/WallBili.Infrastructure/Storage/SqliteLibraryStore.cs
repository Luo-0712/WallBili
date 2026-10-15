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
            if (current > 1) throw new InvalidOperationException("The library database requires a newer WallBili version.");
            if (current == 0)
            {
                using var transaction = connection.BeginTransaction();
                using var schema = connection.CreateCommand();
                schema.Transaction = transaction;
                schema.CommandText = """
                    CREATE TABLE entries(bvid TEXT NOT NULL, cid INTEGER NOT NULL, ordinal INTEGER NOT NULL,
                        payload TEXT NOT NULL, PRIMARY KEY(bvid,cid));
                    CREATE TABLE sources(id TEXT PRIMARY KEY, ordinal INTEGER NOT NULL, payload TEXT NOT NULL);
                    CREATE TABLE queue(id TEXT PRIMARY KEY, ordinal INTEGER NOT NULL UNIQUE,
                        bvid TEXT NOT NULL, cid INTEGER NOT NULL, completed INTEGER NOT NULL CHECK(completed>=0),
                        FOREIGN KEY(bvid,cid) REFERENCES entries(bvid,cid));
                    CREATE TABLE playback(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
                    PRAGMA user_version=1;
                    """;
                await schema.ExecuteNonQueryAsync(ct);
                transaction.Commit();
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
        if (!double.IsFinite(state.Playback.PositionSeconds) || state.Playback.PositionSeconds < 0 ||
            !double.IsFinite(state.Playback.Volume) || state.Playback.Volume is < 0 or > 100 ||
            !Enum.IsDefined(state.Playback.Intent) || !Enum.IsDefined(state.Playback.Mode))
            throw new InvalidDataException("The playback checkpoint is invalid.");
        transaction.Commit();
        return state;
    }

    public async Task SaveAsync(LibraryState state, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM queue; DELETE FROM entries; DELETE FROM sources; DELETE FROM playback;";
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
        transaction.Commit();

        async Task InsertAsync(string sql, CancellationToken ct, params object[] values)
        {
            command.CommandText = sql;
            command.Parameters.Clear();
            for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$" + (char)('a' + i), values[i]);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidDataException("The library contains an empty record.");
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallBili.Core;

public sealed record ContentRef(string Kind, string? Bvid = null, long? Cid = null,
    long? UploaderId = null, long? CollectionId = null, CollectionKind? CollectionKind = null,
    Guid? PlaylistId = null);

public sealed record PlaylistMember(Guid Id, ContentRef Reference, string Title,
    ContentKey[] Parts, string? Failure = null);
public sealed record LocalPlaylist(Guid Id, string Name, long Revision, ContentRef[] Inputs,
    PlaylistMember[] Members, ContentKey[] Excluded, Guid? TaskId, DateTimeOffset UpdatedAt,
    string[]? ExcludedReferences = null);

public sealed class AgentControlException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed record PreparationTask(Guid Id, Guid? PlaylistId, ContentRef[] Inputs,
    string State, int Processed = 0, int? Total = null, string? Failure = null);
public sealed record CommandReceipt(string Fingerprint, JsonElement Result);
public sealed class AgentLibraryState
{
    public List<LocalPlaylist> Playlists { get; set; } = [];
    public List<PreparationTask> Tasks { get; set; } = [];
    public Dictionary<string, CommandReceipt> Receipts { get; set; } = [];
    public List<string> RemovedSources { get; set; } = [];
    public long QueueRevision { get; set; }
}
public sealed record AgentSnapshot(LocalPlaylist[] Playlists, PreparationTask[] Tasks,
    long QueueRevision, bool WaitingForContent = false);

public sealed record AgentToolDefinition(string Name, string Description, JsonElement InputSchema,
    bool ReadOnly = false, bool Destructive = false);
public sealed record AgentResult(string Summary, JsonElement Data, bool IsError = false)
{
    public static AgentResult Success(string summary, object data) =>
        new(summary, JsonSerializer.SerializeToElement(data, AgentJson.Options));
    public static AgentResult Error(string code, string message, string nextStep) =>
        new(message, JsonSerializer.SerializeToElement(new { error = new { code, message, nextStep } }, AgentJson.Options), true);
}
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
public interface IWallBiliControl
{
    IReadOnlyList<AgentToolDefinition> Tools { get; }
    Task<AgentResult> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default);
}
public interface IContentSessionVersion
{
    int SessionVersion { get; }
}

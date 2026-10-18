using System.Text.Json;

namespace WallBili.Core;

internal static class AgentToolCatalog
{
    private static object String(params string[] values) => values.Length == 0 ? new { type = "string", maxLength = 512 } : new { type = "string", @enum = values };
    private static object Integer(int min = 0) => new { type = "integer", minimum = min };
    private static object Id => new { type = "string", format = "uuid" };
    private static object Ids => new { type = "array", items = Id, maxItems = 10000 };
    private static object Reference => new { type = "object", required = new[] { "kind" }, additionalProperties = false,
        properties = new Dictionary<string, object> { ["kind"] = String("video", "part", "collection", "playlist"),
            ["bvid"] = String(), ["cid"] = Integer(1), ["uploaderId"] = Integer(1), ["collectionId"] = Integer(1),
            ["collectionKind"] = String("season", "series"), ["playlistId"] = Id } };
    private static object References => new { type = "array", items = Reference, maxItems = 200 };
    private static AgentToolDefinition Tool(string name, string description, bool readOnly, bool destructive,
        Dictionary<string, object> properties, params string[] required)
    {
        if (!readOnly) properties["requestId"] = new { type = "string", minLength = 1, maxLength = 128 };
        return new(name, description, JsonSerializer.SerializeToElement(new { type = "object", properties,
            required, additionalProperties = false }), readOnly, destructive);
    }
    public static IReadOnlyList<AgentToolDefinition> All { get; } =
    [
        Tool("search_content", "Search public Bilibili videos or creators. Read metadata before choosing; search does not import or play.", true, false,
            new() { ["query"] = String(), ["kind"] = String("video", "creator"), ["page"] = Integer(1), ["pageSize"] = Integer(1), ["order"] = String("relevance", "published", "views", "favorites") }, "query"),
        Tool("list_creator_content", "List an uploader's videos or native collections/series, paginated.", true, false,
            new() { ["uploaderId"] = Integer(1), ["kind"] = String("videos", "collections"), ["page"] = Integer(1), ["pageSize"] = Integer(1) }, "uploaderId"),
        Tool("get_content", "Get video parts or ordered native collection contents from a returned ContentRef. Does not resolve signed playback URLs.", true, false,
            new() { ["reference"] = Reference, ["page"] = Integer(1), ["pageSize"] = Integer(1) }, "reference"),
        Tool("list_sources", "List imported source records. Removing a source does not remove queued content.", true, false,
            new() { ["page"] = Integer(1), ["pageSize"] = Integer(1) }),
        Tool("import_source", "Start background metadata import for a video, part or collection. Returns taskId; import alone never starts playback.", false, false,
            new() { ["reference"] = Reference }, "reference"),
        Tool("refresh_source", "Refresh an imported source in the background, preserving playback.", false, false, new() { ["sourceId"] = String() }, "sourceId"),
        Tool("remove_source", "Remove source record only; catalog and queue remain.", false, true, new() { ["sourceId"] = String() }, "sourceId"),
        Tool("list_playlists", "List saved local playlists with revisions and preparation progress.", true, false, new() { ["page"] = Integer(1), ["pageSize"] = Integer(1) }),
        Tool("get_playlist", "Get ordered members, resolved parts, exclusions and revision of a local playlist.", true, false,
            new() { ["playlistId"] = Id, ["page"] = Integer(1), ["pageSize"] = Integer(1) }, "playlistId"),
        Tool("create_playlist", "Create a named playlist from ordered ContentRefs, preparing incrementally. Returns playlistId and taskId immediately; load_playlist may be called while preparation runs.", false, false,
            new() { ["name"] = String(), ["references"] = References }, "name", "references"),
        Tool("edit_playlist", "Rename, append refs, remove members or reorder all memberIds. Requires current expectedRevision; preserve explicit user order.", false, true,
            new() { ["playlistId"] = Id, ["action"] = String("rename", "append", "remove", "reorder"), ["name"] = String(), ["references"] = References, ["memberIds"] = Ids, ["expectedRevision"] = Integer() }, "playlistId", "action", "expectedRevision"),
        Tool("delete_playlist", "Delete saved playlist only; current queue remains. Requires current expectedRevision.", false, true,
            new() { ["playlistId"] = Id, ["expectedRevision"] = Integer() }, "playlistId", "expectedRevision"),
        Tool("refresh_playlist", "Refresh original refs, append newly discovered episodes, preserve manual order and excluded members. Returns taskId.", false, false,
            new() { ["playlistId"] = Id, ["expectedRevision"] = Integer() }, "playlistId", "expectedRevision"),
        Tool("get_queue", "Read manual queue, stable item IDs and queue revision, plus currently active playback source.", true, false,
            new() { ["page"] = Integer(1), ["pageSize"] = Integer(1) }),
        Tool("edit_queue", "Edit manual queue. append/insert_next take resolved part refs; remove/move/reorder/clear/replace require expectedRevision from get_queue. move uses one itemId and zero-based index; reorder takes all itemIds. Does not implicitly play.", false, true,
            new() { ["action"] = String("append", "insert_next", "remove", "move", "reorder", "clear", "replace"), ["references"] = References, ["itemIds"] = Ids, ["index"] = Integer(), ["expectedRevision"] = Integer() }, "action"),
        Tool("load_playlist", "Load prepared playlist parts and subscribe queue to remaining preparation. mode defaults append; autoplay defaults false. Explicit autoplay starts first new part. replace requires queue expectedRevision. startIndex is zero-based flattened part offset.", false, true,
            new() { ["playlistId"] = Id, ["mode"] = String("append", "replace"), ["autoplay"] = new { type = "boolean" }, ["startIndex"] = Integer(), ["expectedRevision"] = Integer() }, "playlistId"),
        Tool("get_status", "Read actual outputActive, playback intent, source, position, waiting/recovery and task states. An accepted task is not evidence of playing.", true, false, new()),
        Tool("control_playback", "Control current playback. play may select itemId; resume keeps current source. seek takes seconds, set_volume takes volume 0..100, set_mode takes sequential/repeatAll. show_video/hide_video changes desktop visibility. Use requestId for next/previous.", false, false,
            new() { ["action"] = String("play", "resume", "pause", "stop", "next", "previous", "seek", "set_volume", "set_mode", "show_video", "hide_video"), ["itemId"] = Id, ["seconds"] = new { type = "number", minimum = 0 }, ["volume"] = new { type = "number", minimum = 0, maximum = 100 }, ["mode"] = String("sequential", "repeatAll") }, "action"),
        Tool("get_task", "Get one preparation task or paginated history. Tasks and playback survive assistant disconnect; app restart marks unfinished tasks interrupted.", true, false,
            new() { ["taskId"] = Id, ["page"] = Integer(1), ["pageSize"] = Integer(1) }),
        Tool("cancel_task", "Cancel remaining metadata preparation. Already queued items and current playback remain.", false, false, new() { ["taskId"] = Id }, "taskId"),
        Tool("retry_task", "Retry a failed, partial, interrupted or cancelled task; preserve existing content and avoid duplicate parts.", false, false,
            new() { ["taskId"] = Id }, "taskId")
    ];
}

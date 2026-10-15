using System.Text.Json;

namespace WallBili.Player.Mpv;

public sealed class MpvEventArgs(string eventName, JsonElement data) : EventArgs
{
    public string EventName { get; } = eventName;
    public JsonElement Data { get; } = data;
}

public sealed class PlaybackEndedEventArgs(string reason, bool isError, long? playlistEntryId = null) : EventArgs
{
    public string Reason { get; } = reason;
    public bool IsError { get; } = isError;
    public long? PlaylistEntryId { get; } = playlistEntryId;
}

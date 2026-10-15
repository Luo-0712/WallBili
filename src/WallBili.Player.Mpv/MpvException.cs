namespace WallBili.Player.Mpv;

public enum MpvErrorCode
{
    ExecutableNotFound,
    StartupFailed,
    ConnectionFailed,
    Timeout,
    CommandFailed,
    Disconnected,
    MediaFailed,
    InvalidSession,
    InvalidArgument,
    Disposed
}

/// <summary>Messages deliberately omit command arguments, media URLs and mpv diagnostic output.</summary>
public sealed class MpvException(MpvErrorCode code, string message) : Exception(message)
{
    public MpvErrorCode Code { get; } = code;
}

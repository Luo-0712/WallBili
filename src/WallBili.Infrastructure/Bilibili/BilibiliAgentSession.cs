using WallBili.Core;

namespace WallBili.Infrastructure.Bilibili;

public sealed partial class BilibiliClient : IContentSessionVersion
{
    public int SessionVersion { get { lock (_accountGate) return _sessionVersion; } }
}

#!/usr/bin/env bash
set -euo pipefail
WALLBILI_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$WALLBILI_REPO_ROOT"
mkdir -p work
# The complete verification command requires actual mpv/FFmpeg; it must not silently skip media checks.
command -v "${MPV_PATH:-mpv}" >/dev/null
command -v "${FFMPEG_PATH:-ffmpeg}" >/dev/null
dotnet restore WallBili.slnx --locked-mode --disable-parallel -m:1
dotnet build WallBili.slnx -c Release --no-restore -m:1
dotnet run --project tests/WallBili.Infrastructure.Tests -c Release --no-build -- -result-xml work/content-tests.xml -nologo
dotnet run --project tests/WallBili.Player.Mpv.Tests -c Release --no-build -- -result-xml work/player-tests.xml -nologo
dotnet run --project tests/WallBili.Library.Tests -c Release --no-build -- -result-xml work/library-tests.xml -nologo
dotnet run --project tests/WallBili.Desktop.Tests -c Release --no-build -- -result-xml work/desktop-tests.xml -nologo
dotnet run --project tests/WallBili.Mcp.Tests -c Release --no-build -- -result-xml work/mcp-tests.xml -nologo

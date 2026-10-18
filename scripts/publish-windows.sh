#!/usr/bin/env bash
set -euo pipefail
WALLBILI_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$WALLBILI_REPO_ROOT"
WALLBILI_PACKAGE_DIR="${1:-work/windows-phase3}"
mkdir -p "$WALLBILI_PACKAGE_DIR"
WALLBILI_PACKAGE_DIR="$(cd "$WALLBILI_PACKAGE_DIR" && pwd)"
case "${2:-}" in
    ''|--framework-dependent) WALLBILI_SELF_CONTAINED=false ;;
    --self-contained) WALLBILI_SELF_CONTAINED=true ;;
    *) printf '%s\n' 'Usage: publish-windows.sh [OUTPUT_DIRECTORY] [--self-contained|--framework-dependent]' >&2; exit 2 ;;
esac
dotnet publish src/WallBili.Desktop -c Release -r win-x64 --self-contained "$WALLBILI_SELF_CONTAINED" -o "$WALLBILI_PACKAGE_DIR/app" -p:PublishSingleFile=false -m:1
dotnet publish tools/WallBili.DesktopProbe -c Release -r win-x64 --self-contained "$WALLBILI_SELF_CONTAINED" -o "$WALLBILI_PACKAGE_DIR/desktop-probe" -p:PublishSingleFile=false -m:1
dotnet publish tools/WallBili.McpBridge -c Release -r win-x64 --self-contained "$WALLBILI_SELF_CONTAINED" -o "$WALLBILI_PACKAGE_DIR/mcp-bridge" -p:PublishSingleFile=false -m:1
cp LICENSE THIRD-PARTY-NOTICES.md README.md "$WALLBILI_PACKAGE_DIR/"
cp -R licenses "$WALLBILI_PACKAGE_DIR/"
cp docs/windows-quickstart.md docs/windows-phase3-checklist.md docs/phase3-validation.md "$WALLBILI_PACKAGE_DIR/"
cp scripts/measure-resources.ps1 "$WALLBILI_PACKAGE_DIR/"
python3 - "$WALLBILI_PACKAGE_DIR" <<'PY'
import pathlib, sys, zipfile
root = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(root.with_suffix('.zip'), 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(root.rglob('*')):
        if path.is_file(): archive.write(path, path.relative_to(root))
print(root.with_suffix('.zip'))
PY
printf '%s\n' 'mpv 0.41.0 is installed separately. Framework-dependent packages also need .NET 10 Desktop Runtime and ASP.NET Core Runtime 10.'
printf '%s\n' 'Self-contained packages include the .NET runtimes for the application and MCP bridge.'

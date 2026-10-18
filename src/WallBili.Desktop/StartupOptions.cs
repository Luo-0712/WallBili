using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WallBili.Desktop;

internal sealed record StartupOptions(string DataDirectory, bool Preview = false, bool EmptyPreview = false, bool Background = false)
{
    public string InstanceId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(DataDirectory).TrimEnd('\\').ToUpperInvariant())))[..20];
    public bool IsIsolated => Preview || !Path.GetFullPath(DataDirectory).TrimEnd('\\').Equals(DefaultDirectory, StringComparison.OrdinalIgnoreCase);
    internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallBili");
    public static StartupOptions Parse(string[] args)
    {
        // This rebuild runs beside the existing desktop app. Sharing the production database is opt-in.
        var directory = Path.Combine(DefaultDirectory, "profiles", "design-rebuild"); var preview = false; var empty = false; var background = false;
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Length: directory = Path.GetFullPath(args[++i]); break;
                case "--preview": preview = true; break;
                case "--empty": empty = true; preview = true; break;
                case "--background": background = true; break;
                default: throw new ArgumentException("支持的启动参数：--data-dir 路径、--preview、--empty、--background。");
            }
        if (preview) directory = Path.Combine(Path.GetTempPath(), "wallbili-preview-" + Guid.NewGuid().ToString("N"));
        return new(directory, preview, empty, background);
    }
}

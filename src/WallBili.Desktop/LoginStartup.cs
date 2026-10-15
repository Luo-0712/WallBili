using System.IO;
using Microsoft.Win32;

namespace WallBili.Desktop;

internal static class LoginStartup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return string.Equals(key?.GetValue("WallBili") as string, Command(), StringComparison.Ordinal);
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue("WallBili", Command(), RegistryValueKind.String);
        else key.DeleteValue("WallBili", throwOnMissingValue: false);
    }
    private static string Command()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        // dotnet-hosted development runs need the DLL as well as the host path.
        var arguments = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $" \"{typeof(App).Assembly.Location}\"" : "";
        return $"\"{executable}\"{arguments} --background";
    }
}

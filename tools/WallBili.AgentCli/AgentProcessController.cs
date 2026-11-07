using System.Diagnostics;

namespace WallBili.AgentCli;

public interface IAgentProcessController
{
    bool IsRunning(string appPath);
    bool Launch(string appPath, string? dataDirectory);
}

/// <summary>Starts the desktop application hidden in the tray. A second launch would instead pop the
/// existing window, so callers must check <see cref="IsRunning"/> first.</summary>
public sealed class AgentProcessController : IAgentProcessController
{
    public bool IsRunning(string appPath)
    {
        var name = Path.GetFileNameWithoutExtension(appPath);
        var expected = Normalize(appPath);
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (string.Equals(Normalize(process.MainModule?.FileName ?? string.Empty), expected,
                        StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process belongs to another user or architecture; assume it is the app rather
                // than risk a second launch that surfaces the window.
                return true;
            }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('\\');
        }
    }

    public bool Launch(string appPath, string? dataDirectory)
    {
        try
        {
            var start = new ProcessStartInfo(appPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory
            };
            start.ArgumentList.Add("--background");
            if (dataDirectory is not null)
            {
                start.ArgumentList.Add("--data-dir");
                start.ArgumentList.Add(dataDirectory);
            }
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WallBili.Desktop;

/// <summary>Stores the account cookie text in <c>%LOCALAPPDATA%\WallBili\auth.dat</c>, encrypted with
/// Windows DPAPI (CurrentUser scope). Writes go through a temporary file and an atomic replace;
/// credentials never reach SQLite, plain preference files or diagnostics.</summary>
internal sealed class AuthStore
{
    public string FilePath { get; }

    public AuthStore(string path) => FilePath = path;

    public string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var encrypted = File.ReadAllBytes(FilePath);
            if (encrypted.Length == 0) return null;
            var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var text = Encoding.UTF8.GetString(plain);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // A damaged or unreadable file is treated as "no stored credentials"; it is recreated on next login.
            return null;
        }
    }

    public void Save(string cookieText)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookieText),
            optionalEntropy: null, DataProtectionScope.CurrentUser);
        var temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, FilePath, overwrite: true);
    }

    public void Clear()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

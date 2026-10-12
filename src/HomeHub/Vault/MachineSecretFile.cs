using System.Security.Cryptography;
using System.Text;

namespace HomeHub.Vault;

/// <summary>
/// A small secret on disk, readable only on this machine. On Windows it is
/// DPAPI-protected to the machine (so the service account can read it without
/// a user profile) and the folder's ACL keeps everyone but SYSTEM and the
/// Administrators out; elsewhere it is a 0600 file, the same promise the OS
/// gives an SSH key.
/// <para>
/// This holds the vault's unseal key and tokens. Anyone who can read it as an
/// administrator of this box can open the vault — which is also true of the box
/// itself; the file keeps the key off backups, other machines and other users.
/// </para>
/// </summary>
public sealed class MachineSecretFile(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("homehub.vault.keys.v1");

    private readonly string _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("A path is required.", nameof(path)) : path;

    public bool Exists => File.Exists(_path);

    public string? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(_path);

        if (!OperatingSystem.IsWindows())
        {
            return Encoding.UTF8.GetString(bytes);
        }

        try
        {
            var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine);

            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var plain = Encoding.UTF8.GetBytes(text);
        var temporary = _path + ".tmp";

        try
        {
            var bytes = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine)
                : plain;

            File.WriteAllBytes(temporary, bytes);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // Written beside and moved over: a crash mid-write leaves the old
            // keys, never half a file — half a file would lock the vault for good.
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}

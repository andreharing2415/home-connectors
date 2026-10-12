using System.Security.Cryptography;
using System.Text;

namespace HomeHub;

/// <summary>
/// The bearer token clients present. Generated on first start into
/// <c>client-token</c> in the data folder; a client on this machine reads it
/// from there (the installer grants the owner read access to that one file),
/// a phone gets it by pairing. Replacing the file and restarting the hub
/// revokes every client at once.
/// </summary>
public sealed class ClientToken
{
    private readonly byte[] _expected;

    public ClientToken(HubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Path = System.IO.Path.Combine(options.DataDirectory, "client-token");

        if (!File.Exists(Path))
        {
            Directory.CreateDirectory(options.DataDirectory);
            File.WriteAllText(Path, Base64Url(RandomNumberGenerator.GetBytes(32)));
        }

        _expected = Encoding.UTF8.GetBytes(File.ReadAllText(Path).Trim());
    }

    public string Path { get; }

    /// <summary>Constant-time comparison of an <c>Authorization: Bearer …</c> header.</summary>
    public bool Accepts(string? authorization)
    {
        const string scheme = "Bearer ";

        if (authorization is null || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(authorization[scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, _expected);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

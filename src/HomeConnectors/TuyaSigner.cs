using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HomeConnectors.Adapters;

/// <summary>
/// Tuya's OpenAPI signature, version 2, exactly as documented:
/// <c>sign = HMAC-SHA256(client_id + [access_token] + t + nonce + stringToSign, secret)</c>
/// in upper-case hex, where <c>stringToSign</c> is the method, the SHA-256 of
/// the body, the signed headers and the path with sorted query, joined by
/// newlines. Pure: no clock, no network, no state — so it can be checked against
/// the vendor's own worked examples.
/// </summary>
public static class TuyaSigner
{
    /// <summary>SHA-256 of nothing. Every GET carries it.</summary>
    public const string EmptyBodyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// The path and query as the signature wants them: the query sorted by key,
    /// byte order, exactly as sent. The same string must be what goes on the wire.
    /// </summary>
    public static string CanonicalPath(string path, IReadOnlyDictionary<string, string>? query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (query is null || query.Count == 0)
        {
            return path;
        }

        var builder = new StringBuilder(path).Append('?');
        var first = true;

        foreach (var pair in query.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            builder.Append(pair.Key).Append('=').Append(pair.Value);
        }

        return builder.ToString();
    }

    public static string BodyHash(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return EmptyBodyHash;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    }

    /// <summary>
    /// <c>method \n bodyHash \n signedHeaders \n canonicalPath</c>. The signed
    /// header block is <c>key:value\n</c> per header in the order the
    /// <c>Signature-Headers</c> header lists them, and empty when there are none —
    /// which still leaves its newline in place.
    /// </summary>
    public static string StringToSign(
        string method,
        string canonicalPath,
        string? body,
        IReadOnlyList<KeyValuePair<string, string>>? signedHeaders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);

        var headers = new StringBuilder();

        if (signedHeaders is not null)
        {
            foreach (var header in signedHeaders)
            {
                headers.Append(header.Key).Append(':').Append(header.Value).Append('\n');
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{method.ToUpperInvariant()}\n{BodyHash(body)}\n{headers}\n{canonicalPath}");
    }

    /// <summary>
    /// The final signature. <paramref name="accessToken"/> is empty for the token
    /// endpoints and the current token everywhere else.
    /// </summary>
    public static string Sign(
        string clientId,
        string secret,
        string accessToken,
        long timestampMilliseconds,
        string nonce,
        string stringToSign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(accessToken);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(stringToSign);

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"{clientId}{accessToken}{timestampMilliseconds}{nonce}{stringToSign}");

        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));

        return Convert.ToHexString(mac);
    }
}

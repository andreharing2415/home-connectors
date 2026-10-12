using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HomeConnectors.Adapters;

/// <summary>
/// AWS Signature Version 4, just the header form. The Blue Riiot API sits behind
/// AWS API Gateway and wants every call after the login signed with the
/// temporary keys the login hands out. Written by hand, like the Tuya and Xiaomi
/// signers: one SDK for three HMACs is not worth the dependency, and this is
/// checked against AWS's own published test vectors.
/// </summary>
public static class AwsSigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>SHA-256 of nothing, the payload hash of every GET.</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary><c>20150830T123600Z</c>: the form <c>X-Amz-Date</c> carries.</summary>
    public static string AmzDate(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>Authorization</c> header value for one request.
    /// </summary>
    /// <param name="method">GET, POST.</param>
    /// <param name="url">The full request URL, path already percent-encoded.</param>
    /// <param name="headers">Every header to sign, <c>host</c> and <c>x-amz-date</c> included. Names are lower-cased here.</param>
    /// <param name="payloadHash">Lower-case hex SHA-256 of the body.</param>
    /// <param name="at">The same instant <c>x-amz-date</c> names.</param>
    public static string Authorization(
        string method,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string payloadHash,
        string accessKey,
        string secretKey,
        string region,
        string service,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(headers);

        var amzDate = AmzDate(at);
        var dateStamp = amzDate[..8];
        var scope = string.Create(CultureInfo.InvariantCulture, $"{dateStamp}/{region}/{service}/aws4_request");

        var sorted = headers
            .Select(h => (Name: h.Key.Trim().ToLowerInvariant(), Value: CollapseSpaces(h.Value.Trim())))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToArray();

        var canonicalHeaders = new StringBuilder();

        foreach (var (name, value) in sorted)
        {
            canonicalHeaders.Append(name).Append(':').Append(value).Append('\n');
        }

        var signedHeaders = string.Join(';', sorted.Select(h => h.Name));

        var canonicalRequest = string.Join('\n',
            method.ToUpperInvariant(),
            url.AbsolutePath.Length == 0 ? "/" : url.AbsolutePath,
            CanonicalQuery(url.Query),
            canonicalHeaders.ToString(),
            signedHeaders,
            payloadHash);

        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");

        var signature = Hex(Hmac(key, stringToSign));

        return string.Create(CultureInfo.InvariantCulture,
            $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    public static string PayloadHash(string body) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    /// <summary>Pairs sorted by name then value, each side RFC 3986-encoded exactly once.</summary>
    internal static string CanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return string.Empty;
        }

        var pairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var at = pair.IndexOf('=', StringComparison.Ordinal);
                var name = Uri.UnescapeDataString(at < 0 ? pair : pair[..at]);
                var value = at < 0 ? string.Empty : Uri.UnescapeDataString(pair[(at + 1)..]);
                return (Name: Uri.EscapeDataString(name), Value: Uri.EscapeDataString(value));
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join('&', pairs.Select(p => p.Name + "=" + p.Value));
    }

    private static string CollapseSpaces(string value)
    {
        if (!value.Contains("  ", StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var previousSpace = false;

        foreach (var c in value)
        {
            if (c == ' ' && previousSpace)
            {
                continue;
            }

            builder.Append(c);
            previousSpace = c == ' ';
        }

        return builder.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

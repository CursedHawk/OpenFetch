using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace OpenFetch;

/// <summary>
/// Signed cursor profile v1 (docs/cursor-profile-v1.md): <c>of1.{kid}.{base64url(payload)}.{base64url(HMAC-SHA256)}</c>.
/// The MAC covers <c>"of1." + kid + "." + payload</c>. Portable: any implementation with the shared key can verify it.
/// Cursors are authenticated, not encrypted: the payload only holds values the caller already received plus hashes.
/// Several keys may verify (rotation); only the current key signs.
/// </summary>
public sealed class SignedCursorProtector : ICursorProtector
{
    public const string Prefix = "of1";
    private readonly string _currentKeyId;
    private readonly Dictionary<string, byte[]> _keys;

    public SignedCursorProtector(string currentKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        if (!keys.ContainsKey(currentKeyId)) throw new ArgumentException("The current key id must be one of the keys.", nameof(currentKeyId));
        foreach (var (id, key) in keys)
        {
            if (id.Length is 0 or > 32 || !id.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException($"Key id '{id}' must be 1-32 ASCII letters or digits.", nameof(keys));
            if (key.Length < 32) throw new ArgumentException($"Key '{id}' must be at least 256 bits.", nameof(keys));
        }
        _currentKeyId = currentKeyId;
        _keys = keys.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.Ordinal);
    }

    public string Protect(byte[] payload)
    {
        var body = $"{Prefix}.{_currentKeyId}.{Base64Url.EncodeToString(payload)}";
        return body + "." + Base64Url.EncodeToString(Mac(_keys[_currentKeyId], body));
    }

    public byte[] Unprotect(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != Prefix || !_keys.TryGetValue(parts[1], out var key))
            throw new CryptographicException("Malformed cursor.");
        var expected = Mac(key, $"{parts[0]}.{parts[1]}.{parts[2]}");
        if (!CryptographicOperations.FixedTimeEquals(expected, Base64Url.DecodeFromChars(parts[3])))
            throw new CryptographicException("Cursor signature mismatch.");
        return Base64Url.DecodeFromChars(parts[2]);
    }

    private static byte[] Mac(byte[] key, string body) => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body));
}

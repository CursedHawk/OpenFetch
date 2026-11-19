using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace OpenFetch;

/// <summary>
/// Portable cursor profile v1 (docs/cursor-profile-v1.md): <c>of1.{kid}.{base64url(nonce ‖ ciphertext ‖ tag)}</c>,
/// AES-256-GCM with a key derived by HKDF-SHA256 from the shared key (info "openfetch cursor v1") and
/// associated data <c>"of1." + kid</c>. Cursors are encrypted and authenticated: ordering values (which may include
/// hidden key or sort-only columns) never appear in clear. Any implementation holding the shared key can interoperate.
/// Several keys may decrypt (rotation); only the current key encrypts.
/// </summary>
public sealed class PortableCursorProtector : ICursorProtector
{
    public const string Prefix = "of1";
    private const int NonceSize = 12, TagSize = 16;
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("openfetch cursor v1");
    private readonly string _currentKeyId;
    private readonly Dictionary<string, byte[]> _keys;

    public PortableCursorProtector(string currentKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        if (!keys.ContainsKey(currentKeyId)) throw new ArgumentException("The current key id must be one of the keys.", nameof(currentKeyId));
        foreach (var (id, key) in keys)
        {
            if (id.Length is 0 or > 32 || !id.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException($"Key id '{id}' must be 1-32 ASCII letters or digits.", nameof(keys));
            if (key.Length < 32) throw new ArgumentException($"Key '{id}' must be at least 256 bits.", nameof(keys));
        }
        _currentKeyId = currentKeyId;
        _keys = keys.ToDictionary(k => k.Key, k => HKDF.DeriveKey(HashAlgorithmName.SHA256, k.Value, 32, [], Info), StringComparer.Ordinal);
    }

    public string Protect(byte[] payload) => Protect(payload, RandomNumberGenerator.GetBytes(NonceSize));

    /// <summary>Deterministic variant for test vectors only; production nonces must be random.</summary>
    internal string Protect(byte[] payload, byte[] nonce)
    {
        var sealedBox = new byte[NonceSize + payload.Length + TagSize];
        nonce.CopyTo(sealedBox, 0);
        using var aes = new AesGcm(_keys[_currentKeyId], TagSize);
        aes.Encrypt(nonce, payload, sealedBox.AsSpan(NonceSize, payload.Length), sealedBox.AsSpan(NonceSize + payload.Length), Aad(_currentKeyId));
        return $"{Prefix}.{_currentKeyId}.{Base64Url.EncodeToString(sealedBox)}";
    }

    public byte[] Unprotect(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix || !_keys.TryGetValue(parts[1], out var key)) throw new CryptographicException("Malformed cursor.");
        var sealedBox = Base64Url.DecodeFromChars(parts[2]);
        if (sealedBox.Length < NonceSize + TagSize) throw new CryptographicException("Malformed cursor.");
        var plaintext = new byte[sealedBox.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBox.AsSpan(0, NonceSize), sealedBox.AsSpan(NonceSize, plaintext.Length), sealedBox.AsSpan(NonceSize + plaintext.Length), plaintext, Aad(parts[1]));
        return plaintext;
    }

    private static byte[] Aad(string keyId) => Encoding.ASCII.GetBytes($"{Prefix}.{keyId}");
}

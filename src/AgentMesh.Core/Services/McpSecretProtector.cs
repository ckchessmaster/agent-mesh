using System.Text.Json;

namespace AgentMesh.Core.Services;

/// <summary>
/// Encrypts, decrypts, masks, and merges MCP server credential maps (environment variables
/// and HTTP headers) using <see cref="ICredentialEncryptionService"/> so plaintext secrets are
/// never persisted in PostgreSQL or leaked in API responses.
/// </summary>
public static class McpSecretProtector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serializes and encrypts a string key/value secret map. Returns <c>null</c> when the
    /// dictionary is null or empty so unconfigured columns stay SQL <c>NULL</c>.
    /// </summary>
    public static string? EncryptDictionary(
        ICredentialEncryptionService encryption,
        IReadOnlyDictionary<string, string>? values)
    {
        ArgumentNullException.ThrowIfNull(encryption);

        if (values is null || values.Count == 0)
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in values)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                throw new ArgumentException("MCP secret map keys cannot be null or whitespace.", nameof(values));
            }

            normalized[rawKey.Trim()] = rawValue ?? string.Empty;
        }

        if (normalized.Count == 0)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        return encryption.Encrypt(json);
    }

    /// <summary>
    /// Decrypts an encrypted secret map back into a case-sensitive dictionary.
    /// Returns an empty dictionary if the ciphertext is null, empty, or fails decryption/parsing.
    /// </summary>
    public static Dictionary<string, string> DecryptDictionary(
        ICredentialEncryptionService encryption,
        string? cipherText)
    {
        ArgumentNullException.ThrowIfNull(encryption);

        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var json = encryption.Decrypt(cipherText);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return parsed is not null
                ? new Dictionary<string, string>(parsed, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Decrypts an encrypted secret map and masks every value via
    /// <see cref="ICredentialEncryptionService.Mask"/> for safe API/UI display.
    /// </summary>
    public static Dictionary<string, string> MaskDictionary(
        ICredentialEncryptionService encryption,
        string? cipherText)
    {
        ArgumentNullException.ThrowIfNull(encryption);

        var decrypted = DecryptDictionary(encryption, cipherText);
        var masked = new Dictionary<string, string>(decrypted.Count, StringComparer.Ordinal);
        foreach (var (key, value) in decrypted)
        {
            masked[key] = encryption.Mask(value);
        }

        return masked;
    }

    /// <summary>
    /// Decrypts a server-level secret map and overlays a persona-level encrypted override map
    /// on top of it. Persona keys replace matching server keys.
    /// </summary>
    public static Dictionary<string, string> MergeAndDecrypt(
        ICredentialEncryptionService encryption,
        string? baseCipherText,
        string? overrideCipherText)
    {
        ArgumentNullException.ThrowIfNull(encryption);

        var merged = DecryptDictionary(encryption, baseCipherText);
        var overrides = DecryptDictionary(encryption, overrideCipherText);

        foreach (var (key, value) in overrides)
        {
            merged[key] = value;
        }

        return merged;
    }
}

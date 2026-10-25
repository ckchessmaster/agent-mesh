using System.Security.Cryptography;
using System.Text;
using AgentMesh.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Services;

public class AesCredentialEncryptionService : ICredentialEncryptionService
{
    private readonly byte[] _key;
    private readonly ILogger<AesCredentialEncryptionService>? _logger;

    public AesCredentialEncryptionService(IConfiguration config, ILogger<AesCredentialEncryptionService>? logger = null)
    {
        _logger = logger;
        _key = ResolveKey(config, logger);
    }

    // Constructor accepting explicit key (useful for tests)
    public AesCredentialEncryptionService(byte[] key, ILogger<AesCredentialEncryptionService>? logger = null)
    {
        if (key == null || key.Length != 32)
        {
            throw new ArgumentException("Key must be exactly 32 bytes for AES-256.", nameof(key));
        }
        _key = key.ToArray();
        _logger = logger;
    }

    private static byte[] ResolveKey(IConfiguration config, ILogger? logger)
    {
        var explicitKey = config["AGENTMESH_ENCRYPTION_KEY"] ??
                          config["Auth:CredentialsEncryptionKey"] ??
                          Environment.GetEnvironmentVariable("AGENTMESH_ENCRYPTION_KEY");

        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            // Deterministically hash to 32 bytes with SHA256
            return SHA256.HashData(Encoding.UTF8.GetBytes(explicitKey.Trim()));
        }

        try
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var meshDir = Path.Combine(homeDir, ".agentmesh");
            var keyFile = Path.Combine(meshDir, "mesh.key");

            if (File.Exists(keyFile))
            {
                var content = File.ReadAllText(keyFile).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return SHA256.HashData(Encoding.UTF8.GetBytes(content));
                }
            }

            Directory.CreateDirectory(meshDir);
            var generatedKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            File.WriteAllText(keyFile, generatedKey);
            logger?.LogInformation("Generated new persistent encryption key at {KeyFile}", keyFile);
            return SHA256.HashData(Encoding.UTF8.GetBytes(generatedKey));
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Failed to access or persist ~/.agentmesh/mesh.key ({Error}). Falling back to in-memory fallback key.", ex.Message);
            return SHA256.HashData(Encoding.UTF8.GetBytes("agentmesh-fallback-static-encryption-key-seed"));
        }
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];     // 16 bytes
        var cipherBytes = new byte[plainBytes.Length];

        RandomNumberGenerator.Fill(nonce);

        using var aesGcm = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Layout: [12 bytes Nonce] [16 bytes Tag] [Ciphertext]
        var combined = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, nonce.Length + tag.Length, cipherBytes.Length);

        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(cipherText);
        }
        catch
        {
            // If already plaintext or invalid base64, return empty string
            return string.Empty;
        }

        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;

        if (combined.Length < nonceLength + tagLength)
            return string.Empty;

        var nonce = new byte[nonceLength];
        var tag = new byte[tagLength];
        var cipherBytes = new byte[combined.Length - nonceLength - tagLength];

        Buffer.BlockCopy(combined, 0, nonce, 0, nonceLength);
        Buffer.BlockCopy(combined, nonceLength, tag, 0, tagLength);
        Buffer.BlockCopy(combined, nonceLength + tagLength, cipherBytes, 0, cipherBytes.Length);

        var plainBytes = new byte[cipherBytes.Length];
        try
        {
            using var aesGcm = new AesGcm(_key, tagLength);
            aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Failed to decrypt credential: {Error}", ex.Message);
            return string.Empty;
        }
    }

    public string Mask(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var trimmed = token.Trim();
        if (trimmed.Length <= 8)
            return new string('*', trimmed.Length);

        var prefix = "";
        if (trimmed.StartsWith("ghp_")) prefix = "ghp_";
        else if (trimmed.StartsWith("github_pat_")) prefix = "github_pat_";
        else if (trimmed.StartsWith("gho_")) prefix = "gho_";
        else if (trimmed.Length > 12) prefix = trimmed[..4];

        var last4 = trimmed[^4..];
        return $"{prefix}...{last4}";
    }
}

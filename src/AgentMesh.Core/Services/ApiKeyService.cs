using System.Security.Cryptography;
using System.Text;

namespace AgentMesh.Core.Services;

public class ApiKeyService : IApiKeyService
{
    public string GenerateToken(string prefix = "mesh_tok_")
    {
        var randomBytes = RandomNumberGenerator.GetBytes(24);
        var hex = Convert.ToHexString(randomBytes).ToLowerInvariant();
        return $"{prefix}{hex}";
    }

    public string HashToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(token.Trim());
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public bool VerifyToken(string token, string expectedHash)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expectedHash))
            return false;

        var actualHash = HashToken(token);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actualHash),
            Encoding.UTF8.GetBytes(expectedHash));
    }
}

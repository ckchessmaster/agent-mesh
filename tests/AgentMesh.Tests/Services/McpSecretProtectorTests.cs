using System.Security.Cryptography;
using System.Text;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

public class McpSecretProtectorTests
{
    private static ICredentialEncryptionService CreateEncryption()
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("mcp-secret-protector-test-key"));
        return new AesCredentialEncryptionService(key);
    }

    [Fact]
    public void EncryptAndDecryptDictionary_RoundTripsSecrets()
    {
        var encryption = CreateEncryption();
        var secrets = new Dictionary<string, string>
        {
            ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "ghp_1234567890abcdef",
            ["LOG_LEVEL"] = "info"
        };

        var cipherText = McpSecretProtector.EncryptDictionary(encryption, secrets);
        cipherText.Should().NotBeNullOrWhiteSpace();
        cipherText.Should().NotContain("ghp_1234567890abcdef");
        cipherText.Should().NotContain("GITHUB_PERSONAL_ACCESS_TOKEN");

        var decrypted = McpSecretProtector.DecryptDictionary(encryption, cipherText);
        decrypted.Should().Equal(secrets);
    }

    [Fact]
    public void EncryptDictionary_NullOrEmpty_ReturnsNull()
    {
        var encryption = CreateEncryption();

        McpSecretProtector.EncryptDictionary(encryption, null).Should().BeNull();
        McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>()).Should().BeNull();
    }

    [Fact]
    public void EncryptDictionary_BlankKey_ThrowsArgumentException()
    {
        var encryption = CreateEncryption();

        var act = () => McpSecretProtector.EncryptDictionary(
            encryption,
            new Dictionary<string, string> { ["   "] = "secret" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DecryptDictionary_InvalidOrEmptyCiphertext_ReturnsEmptyDictionary()
    {
        var encryption = CreateEncryption();

        McpSecretProtector.DecryptDictionary(encryption, null).Should().BeEmpty();
        McpSecretProtector.DecryptDictionary(encryption, string.Empty).Should().BeEmpty();
        McpSecretProtector.DecryptDictionary(encryption, "not-valid-base64!").Should().BeEmpty();

        var nonJsonCipher = encryption.Encrypt("not-json");
        McpSecretProtector.DecryptDictionary(encryption, nonJsonCipher).Should().BeEmpty();
    }

    [Fact]
    public void MaskDictionary_ReturnsMaskedValuesWithoutLeakingPlaintext()
    {
        var encryption = CreateEncryption();
        var cipherText = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer sk-live-secret-1234",
            ["Short"] = "abcd"
        });

        var masked = McpSecretProtector.MaskDictionary(encryption, cipherText);

        masked.Should().ContainKey("Authorization").WhoseValue.Should().Be("Bear...1234");
        masked.Should().ContainKey("Short").WhoseValue.Should().Be("****");
        masked["Authorization"].Should().NotContain("sk-live-secret");
    }

    [Fact]
    public void MergeAndDecrypt_OverlaysPersonaOverridesOntoBaseSecrets()
    {
        var encryption = CreateEncryption();
        var baseCipher = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
        {
            ["GITHUB_TOKEN"] = "ghp_default_org_token",
            ["GITHUB_API_URL"] = "https://api.github.com"
        });
        var overrideCipher = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
        {
            ["GITHUB_TOKEN"] = "ghp_persona_specific_token",
            ["EXTRA_FLAG"] = "1"
        });

        var merged = McpSecretProtector.MergeAndDecrypt(encryption, baseCipher, overrideCipher);

        merged.Should().Equal(new Dictionary<string, string>
        {
            ["GITHUB_TOKEN"] = "ghp_persona_specific_token",
            ["GITHUB_API_URL"] = "https://api.github.com",
            ["EXTRA_FLAG"] = "1"
        });
    }
}

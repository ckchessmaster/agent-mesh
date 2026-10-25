using System.Security.Cryptography;
using System.Text;
using AgentMesh.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentMesh.Tests.Services;

public class AesCredentialEncryptionServiceTests
{
    private readonly byte[] _testKey = SHA256.HashData(Encoding.UTF8.GetBytes("test-key-for-aes-credential-encryption"));

    [Fact]
    public void EncryptAndDecrypt_RoundTrip_ReturnsOriginalPlaintext()
    {
        var service = new AesCredentialEncryptionService(_testKey);
        var original = "ghp_1234567890abcdefghijklmnopqrstuvwxyz";

        var encrypted = service.Encrypt(original);
        encrypted.Should().NotBeNullOrWhiteSpace();
        encrypted.Should().NotBe(original);

        var decrypted = service.Decrypt(encrypted);
        decrypted.Should().Be(original);
    }

    [Theory]
    [InlineData("ghp_1234567890abcdef", "ghp_...cdef")]
    [InlineData("github_pat_11AAAAAA_BBBBBBBBBBBB1234", "github_pat_...1234")]
    [InlineData("gho_secret1234567890", "gho_...7890")]
    [InlineData("custom_token_12345678", "cust...5678")]
    public void Mask_ValidTokens_ProducesExpectedPreview(string token, string expectedMask)
    {
        var service = new AesCredentialEncryptionService(_testKey);
        var masked = service.Mask(token);
        masked.Should().Be(expectedMask);
    }

    [Fact]
    public void Mask_ShortOrEmptyTokens_HandledSafely()
    {
        var service = new AesCredentialEncryptionService(_testKey);
        service.Mask("").Should().BeEmpty();
        service.Mask("1234").Should().Be("****");
    }

    [Fact]
    public void Decrypt_InvalidCipherText_ReturnsEmptyString()
    {
        var service = new AesCredentialEncryptionService(_testKey);
        service.Decrypt("invalid_base64_or_gibberish").Should().BeEmpty();
        service.Decrypt("").Should().BeEmpty();
    }

    [Fact]
    public void Service_WithConfiguration_DerivesKeyDeterministically()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["AGENTMESH_ENCRYPTION_KEY"] = "my-secret-mesh-key"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        var service1 = new AesCredentialEncryptionService(config);
        var service2 = new AesCredentialEncryptionService(config);

        var encrypted = service1.Encrypt("ghp_hello_world_secret");
        var decrypted = service2.Decrypt(encrypted);

        decrypted.Should().Be("ghp_hello_world_secret");
    }
}

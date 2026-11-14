using AgentMesh.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Core;

public class CopilotCredentialHelperTests
{
    [Theory]
    [InlineData("ghp_1234567890abcdef", true)]
    [InlineData("  GHP_UpperCaseClassic  ", true)]
    [InlineData("github_pat_11ABCDEF", false)]
    [InlineData("gho_oauthToken123", false)]
    [InlineData("ghu_userToServerToken", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsClassicGitHubPat_DetectsClassicPatsOnly(string? token, bool expected)
    {
        CopilotCredentialHelper.IsClassicGitHubPat(token).Should().Be(expected);
    }

    [Fact]
    public void SanitizeCopilotEnvToken_RejectsClassicPatAndMirroredPersonaPat()
    {
        CopilotCredentialHelper.SanitizeCopilotEnvToken("ghp_classic123", null)
            .Should().BeNull();

        CopilotCredentialHelper.SanitizeCopilotEnvToken(
                "github_pat_repo_only",
                personaGitHubToken: "github_pat_repo_only")
            .Should().BeNull("a mirrored Persona GitHub PAT must not override ~/.copilot/config.json");

        CopilotCredentialHelper.SanitizeCopilotEnvToken(
                "github_pat_copilot_requests",
                personaGitHubToken: "ghp_repo_push_pat")
            .Should().Be("github_pat_copilot_requests");
    }

    [Fact]
    public void TryExtractCopilotTokenFromJson_ReadsCopilotCliPlaintextFormat_AndSkipsClassicPats()
    {
        var copilotCliConfig = """
        {
          "copilot_tokens": {
            "https://github.com:CKingdon_bee": "gho_emuCopilotToken999"
          },
          "logged_in_users": [
            { "host": "https://github.com", "login": "CKingdon_bee" }
          ],
          "trusted_folders": ["/workspace"]
        }
        """;

        CopilotCredentialHelper.TryExtractCopilotTokenFromJson(copilotCliConfig)
            .Should().Be("gho_emuCopilotToken999");
        CopilotCredentialHelper.HasActiveCopilotConfigLogin(copilotCliConfig)
            .Should().BeTrue();

        var poisonedConfig = """
        {
          "token": "ghp_classicPoison123",
          "authTokens": {
            "https://github.com": { "token": "ghp_classicPoison123" }
          }
        }
        """;

        CopilotCredentialHelper.TryExtractCopilotTokenFromJson(poisonedConfig)
            .Should().BeNull();
        CopilotCredentialHelper.HasActiveCopilotConfigLogin(poisonedConfig)
            .Should().BeFalse();
    }

    [Fact]
    public void ScrubClassicPatFromConfigJson_RemovesPoisonedClassicPat_WhilePreservingTrustedFoldersAndValidLogins()
    {
        var poisonedOnly = """
        {
          "token": "ghp_classic123",
          "authTokens": { "https://github.com": { "token": "ghp_classic123" } },
          "trusted_folders": ["/workspace"]
        }
        """;

        var cleaned = CopilotCredentialHelper.ScrubClassicPatFromConfigJson(poisonedOnly, out var modified);
        modified.Should().BeTrue();
        cleaned.Should().NotBeNull();
        cleaned.Should().NotContain("ghp_");
        cleaned.Should().Contain("/workspace");

        var poisonedWithEmuLogin = """
        {
          "token": "ghp_classic123",
          "authTokens": { "https://github.com": { "token": "ghp_classic123" } },
          "copilot_tokens": { "https://github.com:CKingdon_bee": "gho_validEmuToken" },
          "logged_in_users": [{ "host": "https://github.com", "login": "CKingdon_bee" }]
        }
        """;

        var cleanedWithEmu = CopilotCredentialHelper.ScrubClassicPatFromConfigJson(poisonedWithEmuLogin, out var mod2);
        mod2.Should().BeTrue();
        cleanedWithEmu.Should().NotContain("ghp_");
        CopilotCredentialHelper.TryExtractCopilotTokenFromJson(cleanedWithEmu)
            .Should().Be("gho_validEmuToken");
    }

    [Fact]
    public void MergeCopilotTokenIntoConfigJson_PreservesExistingInteractiveLoginWhenOverwriteFalse()
    {
        var interactiveConfig = """
        {
          "copilot_tokens": { "https://github.com:CKingdon_bee": "gho_interactiveLogin" },
          "logged_in_users": [{ "host": "https://github.com", "login": "CKingdon_bee" }],
          "trusted_folders": ["/workspace"]
        }
        """;

        var merged = CopilotCredentialHelper.MergeCopilotTokenIntoConfigJson(
            interactiveConfig,
            "gho_driverFallback",
            overwriteExisting: false);

        CopilotCredentialHelper.TryExtractCopilotTokenFromJson(merged)
            .Should().Be("gho_interactiveLogin");
        merged.Should().Contain("/workspace");
    }

    [Fact]
    public void ParseContainerCopilotReadOutput_IgnoresPersonaGitHubPat_AndExtractsCopilotToken()
    {
        var d = CopilotCredentialHelper.ContainerReadDelimiter;
        var patOnlyOutput = $"{{\"trusted_folders\":[\"/workspace\"]}}\n{d}\n\n{d}\n\n{d}\nghp_classic123\n{d}\nghp_classic123";
        CopilotCredentialHelper.ParseContainerCopilotReadOutput(patOnlyOutput)
            .Should().BeNull("a classic PAT or settings-only config.json must not count as Copilot CLI auth");

        var validCopilotWithSeparatePat =
            $"{{\"copilot_tokens\":{{\"https://github.com:CKingdon_bee\":\"gho_emuToken\"}}}}\n{d}\n\n{d}\n\n{d}\n\n{d}\nghp_personalRepoPat";
        CopilotCredentialHelper.ParseContainerCopilotReadOutput(validCopilotWithSeparatePat)
            .Should().Be("gho_emuToken");
    }

    [Fact]
    public void FormatGhHostsYaml_ProducesModernMultiAccountSchema()
    {
        var yaml = CopilotCredentialHelper.FormatGhHostsYaml("ghp_repoPat123");
        yaml.Should().Contain("users:")
            .And.Contain("oauth_token: ghp_repoPat123")
            .And.Contain("user: git")
            .And.Contain("git_protocol: https");
    }
}

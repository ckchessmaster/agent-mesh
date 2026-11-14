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

        var legacyHostsJsonOnly =
            $"\n{d}\n{{\"github.com\":{{\"oauth_token\":\"ghu_legacyVsCodeToken\"}}}}\n{d}\n\n{d}\n\n{d}\nghp_personalRepoPat";
        CopilotCredentialHelper.ParseContainerCopilotReadOutput(legacyHostsJsonOnly)
            .Should().BeNull("legacy github-copilot hosts.json/apps.json are not read by Copilot CLI 1.x");
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

    [Fact]
    public void HasActiveCopilotConfigLogin_WhenLoggedInUsersWithoutCopilotTokens_ReturnsFalseEvenIfAuthTokensPresent()
    {
        // Reproduces the exact broken state that causes Copilot CLI to ignore authTokens and fail
        // with "Saved sign-in could not be read from credential storage".
        var brokenConfig = """
        {
          "logged_in_users": [{ "host": "https://github.com", "login": "CKingdon_bee" }],
          "last_logged_in_user": { "host": "https://github.com", "login": "CKingdon_bee" },
          "authTokens": { "https://github.com": { "token": "gho_validToken123" } }
        }
        """;

        CopilotCredentialHelper.HasActiveCopilotConfigLogin(brokenConfig).Should().BeFalse();

        var cleanAuthTokensOnly = """
        {
          "authTokens": { "https://github.com": { "token": "gho_validToken123" } },
          "trusted_folders": ["/workspace"]
        }
        """;

        CopilotCredentialHelper.HasActiveCopilotConfigLogin(cleanAuthTokensOnly).Should().BeTrue();
    }

    [Fact]
    public void ScrubClassicPatFromConfigJson_WhenPruningOrphans_RemovesLoggedInUsersWithoutMatchingCopilotTokens()
    {
        var poisonedWithOrphanedUser = """
        {
          "copilot_tokens": { "https://github.com:CKingdon_bee": "ghp_classicPoison" },
          "logged_in_users": [{ "host": "https://github.com", "login": "CKingdon_bee" }],
          "last_logged_in_user": { "host": "https://github.com", "login": "CKingdon_bee" },
          "trusted_folders": ["/workspace"]
        }
        """;

        var cleaned = CopilotCredentialHelper.ScrubClassicPatFromConfigJson(
            poisonedWithOrphanedUser,
            out var modified,
            pruneOrphanedLoggedInUsers: true);

        modified.Should().BeTrue();
        cleaned.Should().NotBeNull();
        cleaned.Should().NotContain("logged_in_users");
        cleaned.Should().NotContain("last_logged_in_user");
        cleaned.Should().NotContain("ghp_");
        cleaned.Should().Contain("/workspace");
    }

    [Fact]
    public void MergeCopilotTokenIntoConfigJson_PrunesOrphanedLoggedInUsers_AndOverwritesStaleSeededAuthTokens()
    {
        var brokenConfig = """
        {
          "logged_in_users": [{ "host": "https://github.com", "login": "stale_user" }],
          "last_logged_in_user": { "host": "https://github.com", "login": "stale_user" },
          "authTokens": { "https://github.com": { "token": "gho_oldToken" } },
          "trusted_folders": ["/workspace"]
        }
        """;

        var merged = CopilotCredentialHelper.MergeCopilotTokenIntoConfigJson(
            brokenConfig,
            "gho_freshToken",
            overwriteExisting: false);

        merged.Should().NotBeNull();
        merged.Should().NotContain("logged_in_users");
        merged.Should().NotContain("last_logged_in_user");
        merged.Should().Contain("/workspace");
        CopilotCredentialHelper.HasActiveCopilotConfigLogin(merged).Should().BeTrue();
        CopilotCredentialHelper.TryExtractCopilotTokenFromJson(merged).Should().Be("gho_freshToken");
    }

    [Fact]
    public void MergeCopilotTokenIntoConfigJson_WhenOverwriteTrue_ReplacesStaleInteractiveCopilotTokens()
    {
        var oldInteractive = """
        {
          "copilot_tokens": { "https://github.com:old_user": "gho_oldInteractive" },
          "logged_in_users": [{ "host": "https://github.com", "login": "old_user" }],
          "last_logged_in_user": { "host": "https://github.com", "login": "old_user" }
        }
        """;

        var replaced = CopilotCredentialHelper.MergeCopilotTokenIntoConfigJson(
            oldInteractive,
            "gho_newDriverAuth",
            overwriteExisting: true);

        replaced.Should().NotContain("old_user");
        replaced.Should().NotContain("gho_oldInteractive");
        CopilotCredentialHelper.HasActiveCopilotConfigLogin(replaced).Should().BeTrue();
        CopilotCredentialHelper.TryExtractCopilotTokenFromJson(replaced).Should().Be("gho_newDriverAuth");
    }

    [Fact]
    public void EnsurePlaintextStorageSettingsJson_SetsStoreTokenPlaintextTrue_AndPreservesExistingSettings()
    {
        var existing = """{"model":"claude-sonnet-5","theme":"github"}""";
        var updated = CopilotCredentialHelper.EnsurePlaintextStorageSettingsJson(existing, out var modified);

        modified.Should().BeTrue();
        updated.Should().Contain("\"storeTokenPlaintext\":true");
        updated.Should().Contain("\"model\":\"claude-sonnet-5\"");

        CopilotCredentialHelper.EnsurePlaintextStorageSettingsJson(updated, out var modifiedAgain);
        modifiedAgain.Should().BeFalse();
    }
}

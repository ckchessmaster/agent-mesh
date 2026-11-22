using AgentMesh.Core.Models;
using FluentAssertions;

namespace AgentMesh.Tests.Models;

/// <summary>
/// The one question that decides whether an agent is standing in the right repository: do these two
/// remote strings name the same tree.
/// </summary>
/// <remarks>
/// Cases are the spellings that actually occur: whatever the operator pasted into the dashboard, and
/// whatever <c>git remote get-url origin</c> reports after a clone. A comparison that only matched
/// identical strings would report a mismatch on a correctly configured project, which is worse than
/// useless — it trains the operator to dismiss the check.
/// </remarks>
public class RemoteIdentityTests
{
    [Theory]
    [InlineData("https://github.com/o/repo", "https://github.com/o/repo")]
    [InlineData("https://github.com/o/repo", "https://github.com/o/repo.git")]
    [InlineData("https://github.com/o/repo/", "https://github.com/o/repo")]
    [InlineData("https://GitHub.com/o/repo", "https://github.com/o/repo")]
    [InlineData("git@github.com:o/repo.git", "https://github.com/o/repo")]
    [InlineData("ssh://git@github.com/o/repo.git", "https://github.com/o/repo")]
    [InlineData("ssh://git@github.com:2222/o/repo.git", "https://github.com/o/repo")]
    [InlineData("https://user:ghp_token@github.com/o/repo.git", "https://github.com/o/repo")]
    [InlineData("https://oauth2@github.com/o/repo", "git@github.com:o/repo.git")]
    [InlineData("/srv/git/repo.git", "/srv/git/repo")]
    public void AreRemotesEquivalent_SameRepositoryDifferentSpelling_IsTrue(string left, string right)
    {
        WorkspacePathResolver.AreRemotesEquivalent(left, right).Should().BeTrue(
            $"'{left}' and '{right}' name the same repository");
    }

    [Theory]
    [InlineData("https://github.com/o/repo", "https://github.com/other/repo")]
    [InlineData("https://github.com/o/repo", "https://gitlab.com/o/repo")]
    [InlineData("https://github.com/o/repo", "https://github.com/o/other")]
    [InlineData("git@github.com:ckchessmaster/agent-mesh.git", "https://github.com/ckchessmaster/budget-app")]
    [InlineData(null, "https://github.com/o/repo")]
    [InlineData("", "https://github.com/o/repo")]
    [InlineData(null, null)]
    [InlineData("   ", "")]
    public void AreRemotesEquivalent_DifferentOrUnknownRepository_IsFalse(string? left, string? right)
    {
        // A blank on either side is "unknown", and unknown must never satisfy a safety check.
        WorkspacePathResolver.AreRemotesEquivalent(left, right).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://github.com/ckchessmaster/budget-app", "budget-app")]
    [InlineData("https://github.com/ckchessmaster/budget-app.git", "budget-app")]
    [InlineData("https://github.com/ckchessmaster/budget-app/", "budget-app")]
    [InlineData("git@github.com:ckchessmaster/budget-app.git", "budget-app")]
    [InlineData("/srv/git/agent-mesh.git", "agent-mesh")]
    [InlineData("https://github.com/", null)]
    [InlineData("https://github.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryDeriveRepositoryNameFromRemote_ReturnsTheDirectoryNameOrNull(string? remote, string? expected)
    {
        WorkspacePathResolver.TryDeriveRepositoryNameFromRemote(remote).Should().Be(expected);
    }

    [Fact]
    public void NormalizeRemoteUrl_DropsCredentialsSoTheyNeverLeakIntoComparedText()
    {
        var normalized = WorkspacePathResolver.NormalizeRemoteUrl("https://user:secret-token@github.com/o/r.git");

        normalized.Should().Be("github.com/o/r");
        normalized.Should().NotContain("secret-token");
    }
}

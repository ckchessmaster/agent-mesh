using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Activities;

/// <summary>
/// Which credential signs a platform merge: the job's author-persona PAT when the persona store has
/// one, configured deployment credentials otherwise, and never a silently invented identity.
/// </summary>
/// <remarks>
/// These tests pin the credential selection, not the GitHub transport — the transport is
/// <see cref="GitHubPullRequestServiceTests"/>'s job. The activity is mocked one layer out so the
/// assertion is exactly "what token did the activity hand the merge service, and when did it hand
/// null to let configuration answer".
/// </remarks>
public class GitActivitiesPersonaTokenTests
{
    private const string AuthorPat = "ghp_author_persona_pat";
    private const string PrUrl = "https://github.com/ckchessmaster/homelab-manager/pull/1";

    private static readonly GitHubMergeResult Merged = new(true, "Merged pull request #1 via squash.");

    private static (GitActivities Activity, List<GitHubMergeRequest> Captured) Build(
        AgentMeshDbContext? db,
        ICredentialEncryptionService? encryption)
    {
        var captured = new List<GitHubMergeRequest>();

        var pulls = new Mock<IGitHubPullRequestService>();
        pulls.Setup(s => s.MergePullRequestAsync(
                It.IsAny<GitHubMergeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<GitHubMergeRequest, CancellationToken>((r, _) => captured.Add(r))
            .ReturnsAsync(Merged);

        var activity = new GitActivities(
            NullLogger<GitActivities>.Instance,
            pulls.Object,
            new ConfigurationBuilder().Build(),
            db,
            encryption);

        return (activity, captured);
    }

    private static async Task SeedJobWithPersonaAsync(
        AgentMeshDbContext db, Guid jobId, string assignedPersona, bool withToken)
    {
        var enc = new AesCredentialEncryptionService(new byte[32]);
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, AssignedAgentId = assignedPersona });
        db.Personas.Add(new Persona
        {
            Id = assignedPersona.Trim().ToLowerInvariant(),
            Name = assignedPersona,
            DriverId = "claude",
            GitHubTokenEncrypted = withToken ? enc.Encrypt(AuthorPat) : null,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task MergeJobPullRequest_SignsWithAuthorPersonaPat()
    {
        var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        // The job row stores the assignee capitalized; the persona key is lowercase — the lookup
        // must normalize, which is exactly what a real 'Bee'-assigned job would exercise.
        await SeedJobWithPersonaAsync(db, jobId, "Bee", withToken: true);
        var (activity, captured) = Build(db, new AesCredentialEncryptionService(new byte[32]));

        await activity.MergeJobPullRequestAsync(new MergePullRequestInput(
            JobId: jobId.ToString("D"),
            TargetBranch: "mesh/job-1",
            PrUrl: PrUrl,
            ApprovedHeadSha: "abc123"));

        captured.Should().ContainSingle();
        captured[0].Token.Should().Be(AuthorPat);
        captured[0].Sha.Should().Be("abc123");
        captured[0].PrUrlOrNumber.Should().Be(PrUrl);
    }

    [Fact]
    public async Task MergeJobPullRequest_WhenPersonaHasNoToken_LetsConfigurationAnswer()
    {
        var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        await SeedJobWithPersonaAsync(db, jobId, "rook", withToken: false);
        var (activity, captured) = Build(db, new AesCredentialEncryptionService(new byte[32]));

        await activity.MergeJobPullRequestAsync(new MergePullRequestInput(
            JobId: jobId.ToString(), TargetBranch: "main", PrUrl: PrUrl, ApprovedHeadSha: null));

        // Null means "use what the deployment configures" — the service keeps its own precedence and
        // its own loud refusal when that is also absent.
        captured.Should().ContainSingle();
        captured[0].Token.Should().BeNull();
    }

    [Fact]
    public async Task MergeJobPullRequest_WithoutDbWiring_BehavesLikeTheThreeArgConstructor()
    {
        // Older hosts and the existing construction patterns pass no persona store; merge must not
        // even attempt a lookup, and must not fail for it.
        var (activity, captured) = Build(db: null, encryption: null);

        await activity.MergeJobPullRequestAsync(new MergePullRequestInput(
            JobId: Guid.NewGuid().ToString(), TargetBranch: "main", PrUrl: PrUrl, ApprovedHeadSha: null));

        captured.Should().ContainSingle();
        captured[0].Token.Should().BeNull();
    }

    [Fact]
    public async Task MergeJobPullRequest_NonGuidJobId_SkipsThePersonaLookupWithoutError()
    {
        var db = TestDbContextFactory.Create();
        var (activity, captured) = Build(db, new AesCredentialEncryptionService(new byte[32]));

        await activity.MergeJobPullRequestAsync(new MergePullRequestInput(
            JobId: "session-legacy-not-a-guid", TargetBranch: "main", PrUrl: PrUrl, ApprovedHeadSha: null));

        captured.Should().ContainSingle();
        captured[0].Token.Should().BeNull();
    }

    [Fact]
    public async Task MergeJobPullRequest_ProjectJobMiss_FallsBackToTheAgentJobRow()
    {
        var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var enc = new AesCredentialEncryptionService(new byte[32]);
        db.AgentJobs.Add(new AgentJob { Id = jobId, AssignedAgentId = "vane" });
        db.Personas.Add(new Persona
        {
            Id = "vane",
            Name = "Vane",
            DriverId = "claude",
            GitHubTokenEncrypted = enc.Encrypt(AuthorPat),
        });
        await db.SaveChangesAsync();
        var (activity, captured) = Build(db, enc);

        await activity.MergeJobPullRequestAsync(new MergePullRequestInput(
            JobId: jobId.ToString(), TargetBranch: "main", PrUrl: PrUrl, ApprovedHeadSha: null));

        captured[0].Token.Should().Be(AuthorPat);
    }
}

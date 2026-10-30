using System.Security.Claims;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// How a platform tool decides who is calling and which job they are calling about.
/// </summary>
/// <remarks>
/// <para>
/// This resolver is the difference between the three tools working from a runner and working only
/// from a hand-crafted curl. A runner is handed a bearer token and a session id by the control
/// plane; it is never told its job id, its persona's GitHub login, or where on disk its checkout
/// lives. Every one of those is recovered here, and each has a precedence rule that a caller can
/// get wrong in a way that produces a plausible-looking but mis-attributed write.
/// </para>
/// <para>
/// The tests are organised per resolution rule rather than per method, because the rule — not the
/// method — is what could regress.
/// </para>
/// </remarks>
public class PlatformCallContextResolverTests
{
    private const string WorkspaceRootConfigKey = PlatformCallContextResolver.WorkspaceRootConfigKey;

    // ── Agent identity ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_TakesTheAgentIdFromTheVerifiedClaimAheadOfTheHeader()
    {
        // X-Agent-Id is a plain header anyone can set. A verified bearer claim must win, or the
        // header becomes a way to post a message as another persona.
        var (resolver, _) = Build(
            authenticated: true,
            claimAgentId: "astra",
            headers: new Dictionary<string, string> { ["X-Agent-Id"] = "impostor" });

        var context = await resolver.ResolveAsync();

        context.AgentId.Should().Be("astra");
        context.AgentIdSource.Should().Be("claim:name-identifier");
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToTheHeaderOnlyWhenThereIsNoClaim()
    {
        // This is the documented master-key / dev path the header exists for, not a bypass: with no
        // authenticated principal there is no claim to prefer.
        var (resolver, _) = Build(
            authenticated: false,
            headers: new Dictionary<string, string> { ["X-Agent-Id"] = "rook" });

        var context = await resolver.ResolveAsync();

        context.AgentId.Should().Be("rook");
        context.AgentIdSource.Should().Be("header:X-Agent-Id");
    }

    [Fact]
    public async Task ResolveAsync_ReportsNoIdentityForAnAnonymousRequestWithNoHeaders()
    {
        var (resolver, _) = Build(authenticated: false);

        var context = await resolver.ResolveAsync();

        context.AgentId.Should().BeNull();
        context.AgentIdSource.Should().Be("unauthenticated");
        context.HasIdentity.Should().BeFalse();
    }

    [Theory]
    [InlineData("  Rook  ", "rook")]
    [InlineData("@rook", "rook")]
    [InlineData("@@ROOK", "rook")]
    public async Task ResolveAsync_NormalizesIdentitySoLookupsMatchTheStoredSlug(string raw, string expected)
    {
        // Persona and agent ids are stored lowercase and are addressed with an @ sigil in chat.
        // Without normalization, "@Rook" from a prompt would silently resolve to nothing and the
        // caller would be told it has no identity.
        var (resolver, _) = Build(authenticated: false, headers: new Dictionary<string, string> { ["X-Agent-Id"] = raw });

        (await resolver.ResolveAsync()).AgentId.Should().Be(expected);
    }

    // ── Persona identity ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolvePersonaId_ComesFromTheTurnSessionNotFromTheHeader()
    {
        // The session was created by the control plane FOR a specific persona. That is evidence;
        // a header is an assertion. Preferring the header would let any caller post as any persona.
        var dbName = Guid.NewGuid().ToString();
        var sessionId = Guid.NewGuid();

        var (_, seedDb) = Build(authenticated: false, dbName: dbName);
        await SeedSessionAsync(seedDb, sessionId, personaId: "rook", jobId: null);

        var (resolver, _) = Build(authenticated: false, dbName: dbName, headers: new Dictionary<string, string>
        {
            ["X-Session-Id"] = sessionId.ToString(),
            ["X-Persona-Id"] = "sorren",
        });

        var context = await resolver.ResolveAsync();

        context.PersonaId.Should().Be("rook");
        context.PersonaIdSource.Should().Be("session:personaId");
    }

    [Fact]
    public async Task ResolvePersonaId_UsesTheHeaderWhenThereIsNoSessionToConsult()
    {
        var (resolver, _) = Build(authenticated: false, headers: new Dictionary<string, string>
        {
            ["X-Persona-Id"] = "sorren",
        });

        var context = await resolver.ResolveAsync();

        context.PersonaId.Should().Be("sorren");
        context.PersonaIdSource.Should().Be("header:X-Persona-Id");
    }

    [Fact]
    public async Task ResolvePersonaId_FallsBackToTheAgentIdRatherThanReportingNothing()
    {
        // A bare driver (an external MCP client with only X-Agent-Id) is still an identity the chat
        // can attribute. Reporting null here would make post_job_message refuse a legitimate caller.
        var (resolver, _) = Build(authenticated: false, headers: new Dictionary<string, string>
        {
            ["X-Agent-Id"] = "spark",
        });

        var context = await resolver.ResolveAsync();

        context.PersonaId.Should().Be("spark");
        context.PersonaIdSource.Should().StartWith("fallback:");
    }

    // ── Session id ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("not-a-guid", "not a GUID")]
    [InlineData("", "absent")]
    [InlineData("   ", "absent")]
    public async Task ResolveSessionId_ReportsWhyItIsMissing(string raw, string expectedFragment)
    {
        // The source string is what the tool prints back when it refuses, so "absent" and
        // "present but malformed" must be distinguishable — they need different fixes.
        var (resolver, _) = Build(authenticated: false,
            headers: raw.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["X-Session-Id"] = raw });

        var context = await resolver.ResolveAsync();

        context.SessionId.Should().BeNull();
        context.SessionIdSource.Should().Contain(expectedFragment);
    }

    [Fact]
    public async Task ResolveSessionId_ReportsAMissingRowRatherThanPretendingItResolved()
    {
        var sessionId = Guid.NewGuid();
        var (resolver, _) = Build(authenticated: false, headers: new Dictionary<string, string>
        {
            ["X-Session-Id"] = sessionId.ToString(),
        });

        var context = await resolver.ResolveAsync();

        // Two things must hold. The source string says "header (session ... not found)" so the
        // caller learns its header arrived and was rejected — a different conversation from "your
        // header never got here". And the id itself is dropped: it would otherwise be written into
        // job_messages.turn_session_id, where a dangling reference renders a chat message with no
        // avatar and no way to tell why.
        context.SessionId.Should().BeNull();
        context.SessionIdSource.Should().Contain("not found");
    }

    // ── Job id ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveJobId_ComesFromTheTurnSessionsMetadata()
    {
        // This is the whole reason the feature works from a runner: the dispatcher writes jobId
        // into the turn session's metadata, and a runner knows its own session id.
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var dbName = Guid.NewGuid().ToString();
        var (_, seedDb) = Build(authenticated: false, dbName: dbName);
        await SeedSessionAsync(seedDb, sessionId, personaId: "rook", jobId: jobId);

        var (resolver, _) = Build(authenticated: false, dbName: dbName, headers: new Dictionary<string, string>
        {
            ["X-Session-Id"] = sessionId.ToString(),
        });

        var context = await resolver.ResolveAsync();

        context.JobId.Should().Be(jobId);
        context.JobIdSource.Should().Be($"session:{sessionId}:metadata");
    }

    [Fact]
    public async Task ResolveJobId_IgnoresAMalformedMetadataBlobRatherThanThrowing()
    {
        var sessionId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (_, db) = Build(authenticated: false, dbName: dbName);

        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = "broken metadata",
            Status = "thinking",
            PersonaId = "rook",
            MetadataJson = "{ this is not json",
        });
        await db.SaveChangesAsync();

        var (resolver, _) = Build(authenticated: false, dbName: dbName, headers: new Dictionary<string, string>
        {
            ["X-Session-Id"] = sessionId.ToString(),
        });

        var context = await resolver.ResolveAsync();

        context.JobId.Should().BeNull();
        // The persona still resolves off the row itself, so a corrupt metadata blob degrades the
        // job lookup only; it does not cost the caller its identity too.
        context.PersonaId.Should().Be("rook");
    }

    [Fact]
    public async Task ResolveJobId_UsesTheHeaderWhenTheSessionCarriesNoJob()
    {
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var dbName = Guid.NewGuid().ToString();
        var (_, seedDb) = Build(authenticated: false, dbName: dbName);
        await SeedSessionAsync(seedDb, sessionId, personaId: "rook", jobId: null);

        var (resolver, _) = Build(authenticated: false, dbName: dbName, headers: new Dictionary<string, string>
        {
            ["X-Session-Id"] = sessionId.ToString(),
            ["X-Job-Id"] = jobId.ToString(),
        });

        var context = await resolver.ResolveAsync();

        context.JobId.Should().Be(jobId);
        context.JobIdSource.Should().Be("header:X-Job-Id");
    }

    [Fact]
    public async Task ResolveJobId_PrefersTheSessionOverAConflictingHeaderAndSaysSo()
    {
        // A runner that sends the wrong X-Job-Id is confused about which job it is in. Trusting the
        // session — the control plane's own record — is the safe resolution, and the source string
        // is what lets the tool log the disagreement instead of silently picking one.
        var realJob = Guid.NewGuid();
        var wrongJob = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var dbName = Guid.NewGuid().ToString();
        var (_, seedDb) = Build(authenticated: false, dbName: dbName);
        await SeedSessionAsync(seedDb, sessionId, personaId: "rook", jobId: realJob);

        var (resolver, _) = Build(authenticated: false, dbName: dbName, headers: new Dictionary<string, string>
        {
            ["X-Session-Id"] = sessionId.ToString(),
            ["X-Job-Id"] = wrongJob.ToString(),
        });

        var context = await resolver.ResolveAsync();

        context.JobId.Should().Be(realJob);
        context.JobIdSource.Should().StartWith("session:");
    }

    [Fact]
    public async Task ResolveJobId_ReportsAbsentWhenNothingSuppliesIt()
    {
        var (resolver, _) = Build(authenticated: false, headers: new Dictionary<string, string>
        {
            ["X-Agent-Id"] = "rook",
        });

        var context = await resolver.ResolveAsync();

        context.JobId.Should().BeNull();
        context.JobIdSource.Should().Be("absent");
    }

    // ── Git context ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_LiftsTheGitContextOutOfTheTurnSession()
    {
        // JobTurnDispatcher writes these four keys when it creates a turn. Reading them back is
        // what lets open_pull_request name a head branch, a base branch, and a remote without the
        // agent supplying any of them.
        var sessionId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var dbName = Guid.NewGuid().ToString();
        var (_, db) = Build(authenticated: false, dbName: dbName);
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = "Job Turn: platform tools (@rook)",
            Status = "thinking",
            PersonaId = "rook",
            DriverId = "rook-driver",
            MetadataJson = """
                {"jobId":"JOB","gitBranch":"feat/task-205","gitUrl":"https://github.com/acme/widgets.git",
                 "gitBaseBranch":"develop","projectId":"agent-mesh","workspaceGitRemote":"https://github.com/acme/fork.git"}
                """.Replace("JOB", jobId.ToString()),
        });
        await db.SaveChangesAsync();

        var (resolver, _) = Build(authenticated: false, dbName: dbName, headers: new Dictionary<string, string>
        {
            ["X-Session-Id"] = sessionId.ToString(),
        });

        var context = await resolver.ResolveAsync();

        context.GitBranch.Should().Be("feat/task-205");
        // workspaceGitRemote wins over gitUrl: it is the remote the workspace was actually hydrated
        // from, which for a fork-based flow is not the project's upstream URL.
        context.GitRemoteUrl.Should().Be("https://github.com/acme/fork.git");
        context.GitBaseBranch.Should().Be("develop");
        context.ProjectId.Should().Be("agent-mesh");
    }

    [Fact]
    public async Task ResolveAsync_DoesNotInventABranchFromTheSessionTitle()
    {
        // A turn session's title is "Job Turn: <job> (@persona)". Returning it as a branch name
        // would hand open_pull_request a string containing spaces and parentheses to push.
        var sessionId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (_, db) = Build(authenticated: false, dbName: dbName);
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = "Job Turn: Platform Tools (@rook)",
            Status = "thinking",
            PersonaId = "rook",
            MetadataJson = "{}",
        });
        await db.SaveChangesAsync();

        var (resolver, _) = Build(authenticated: false, dbName: dbName, headers: new Dictionary<string, string>
        {
            ["X-Session-Id"] = sessionId.ToString(),
        });

        (await resolver.ResolveAsync()).GitBranch.Should().BeNull();
    }

    // ── Workspace directory ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveWorkspaceDirectory_PrefersTheConfiguredRoot()
    {
        var jobId = Guid.NewGuid();
        var root = CreateWorkspace(jobId, nested: true);

        try
        {
            // The directory is only computed when a job is known, so the job has to come from
            // somewhere; the header is the shortest route to that.
            var (resolver, _) = Build(
                authenticated: false,
                headers: new Dictionary<string, string> { ["X-Job-Id"] = jobId.ToString() },
                settings: new Dictionary<string, string?> { [WorkspaceRootConfigKey] = root });

            var context = await resolver.ResolveAsync();

            context.WorkspaceDirectory.Should().Be(Path.Combine(root, "jobs", jobId.ToString()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveWorkspaceDirectory_AcceptsTheLegacyFlatLayout()
    {
        // Older runners created workspaces/<jobId> rather than workspaces/jobs/<jobId>. Both are
        // checked so an upgrade does not silently break open_pull_request for an existing checkout.
        var jobId = Guid.NewGuid();
        var root = CreateWorkspace(jobId, nested: false);

        try
        {
            var (resolver, _) = Build(
                authenticated: false,
                headers: new Dictionary<string, string> { ["X-Job-Id"] = jobId.ToString() },
                settings: new Dictionary<string, string?> { [WorkspaceRootConfigKey] = root });

            var context = await resolver.ResolveAsync();

            context.WorkspaceDirectory.Should().Be(Path.Combine(root, jobId.ToString()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveWorkspaceDirectory_IgnoresADirectoryWithoutAGitRepository()
    {
        // The PR service refuses to run git outside a repository, so a directory that exists but is
        // not a checkout must not be reported as "the workspace" — that turns a clear
        // no_working_directory refusal into a confusing not_a_git_repository one.
        var jobId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "agentmesh-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "jobs", jobId.ToString()));

        try
        {
            var (resolver, _) = Build(
                authenticated: false,
                headers: new Dictionary<string, string> { ["X-Job-Id"] = jobId.ToString() },
                settings: new Dictionary<string, string?> { [WorkspaceRootConfigKey] = root });

            var context = await resolver.ResolveAsync();

            context.WorkspaceDirectory.Should().BeNull();
            context.JobId.Should().Be(jobId, "the job still resolves; only the checkout is missing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveWorkspaceDirectory_IsNullWhenNoJobIsKnown()
    {
        var (resolver, _) = Build(authenticated: false, headers: new Dictionary<string, string>
        {
            ["X-Agent-Id"] = "rook",
        });

        (await resolver.ResolveAsync()).WorkspaceDirectory.Should().BeNull();
    }

    [Fact]
    public async Task ResolveWorkspaceDirectory_ExpandsAnEnvironmentVariableInTheConfiguredRoot()
    {
        // A root written as $SOME_ROOT/jobs in a compose file or Helm value has to expand, or the
        // lookup silently finds nothing and open_pull_request reports no workspace with no hint
        // that the configured path was never the literal string it looks like.
        var marker = "AGENTMESH_TEST_WS_ROOT_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), marker);
        var jobId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(root, "jobs", jobId.ToString(), ".git"));

        try
        {
            Environment.SetEnvironmentVariable(marker, root);

            var (resolver, _) = Build(
                authenticated: false,
                headers: new Dictionary<string, string> { ["X-Job-Id"] = jobId.ToString() },
                settings: new Dictionary<string, string?> { [WorkspaceRootConfigKey] = "$" + marker + "/jobs" });

            var context = await resolver.ResolveAsync();

            // The configured value names the jobs root, so the per-job directory is appended to it.
            context.WorkspaceDirectory.Should().Be(Path.Combine(root, "jobs", jobId.ToString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("$ROOK_EXPANSION_HOME/nested", "/base/nested")]
    [InlineData("${ROOK_EXPANSION_HOME}/nested", "/base/nested")]
    [InlineData("%ROOK_EXPANSION_HOME%/nested", "/base/nested")]
    public void Expand_RecognisesEveryFormAnOperatorActuallyWrites(string input, string expected)
    {
        // The regression this pins: Environment.ExpandEnvironmentVariables alone recognises only
        // %VAR% on Unix, so a $HOME-style path copied from a shell script silently matches nothing
        // and open_pull_request reports no workspace with no clue why.
        Environment.SetEnvironmentVariable("ROOK_EXPANSION_HOME", "/base");
        try
        {
            PlatformCallContextResolver.Expand(input).Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROOK_EXPANSION_HOME", null);
        }
    }

    [Fact]
    public void Expand_LeavesAnUnresolvableReferenceLiteralRatherThanBlankingIt()
    {
        // Blanking would turn "$MISSING/nested" into "/nested" — a real absolute path that could
        // match something. The unexpanded string matches nothing, which produces the honest
        // no_working_directory refusal instead of a wrong repository.
        PlatformCallContextResolver.Expand("$ROOK_DEFINITELY_UNSET_VAR/nested").Should().Be("$ROOK_DEFINITELY_UNSET_VAR/nested");
        PlatformCallContextResolver.Expand("${ROOK_DEFINITELY_UNSET_VAR}/nested").Should().Be("${ROOK_DEFINITELY_UNSET_VAR}/nested");
    }

    [Fact]
    public void Expand_ResolvesATildeToTheUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        PlatformCallContextResolver.Expand("~/.agentmesh/workspaces")
            .Should().Be(Path.Combine(home, ".agentmesh", "workspaces"));
        PlatformCallContextResolver.Expand("~").Should().Be(home);
    }

    [Fact]
    public void Expand_LeavesAPathThatMerelyContainsADollarAlone()
    {
        // A directory named "cost$100" is legal on Linux. Only a dollar followed by an identifier
        // start is a reference; anything else must survive untouched.
        PlatformCallContextResolver.Expand("/data/cost$100/workspaces").Should().Be("/data/cost$100/workspaces");
    }

    [Fact]
    public void Expand_PassesThroughAPlainPath() =>
        PlatformCallContextResolver.Expand("/workspace").Should().Be("/workspace");

    [Fact]
    public async Task ResolveWorkspaceDirectory_ReturnsNullWhenTheConfiguredRootDoesNotExist()
    {
        // A configured-but-wrong root is the common misconfiguration. It must resolve to null (and
        // then to the PR service's no_working_directory refusal) rather than fall through to the
        // default /workspace, which on a runner IS a repository — just the wrong one.
        var (resolver, _) = Build(
            authenticated: false,
            headers: new Dictionary<string, string> { ["X-Job-Id"] = Guid.NewGuid().ToString() },
            settings: new Dictionary<string, string?>
            {
                [WorkspaceRootConfigKey] = Path.Combine(Path.GetTempPath(), "agentmesh-absent-" + Guid.NewGuid().ToString("N")),
            });

        var context = await resolver.ResolveAsync();

        context.WorkspaceDirectory.Should().BeNull();
        context.JobId.Should().NotBeNull();
    }

    // ── HasIdentity ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task HasIdentity_IsTrueWhenEitherHalfOfTheIdentityResolves(bool authenticate, bool withHeader)
    {
        var headers = withHeader
            ? new Dictionary<string, string> { ["X-Agent-Id"] = "rook" }
            : new Dictionary<string, string>();

        var (resolver, _) = Build(authenticated: authenticate, claimAgentId: authenticate ? "astra" : null, headers: headers);

        var context = await resolver.ResolveAsync();
        context.HasIdentity.Should().Be(authenticate || withHeader);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A resolver over a named store, plus a context bound to that same store.
    /// </summary>
    /// <remarks>
    /// The name is a parameter rather than generated per call because several tests seed a session
    /// and then rebuild the resolver with an <c>X-Session-Id</c> header pointing at it. Two
    /// anonymous in-memory databases are two different databases, so the seeded row would be
    /// invisible to the resolver under test and every such test would pass vacuously or fail
    /// confusingly.
    /// </remarks>
    private static (PlatformCallContextResolver Resolver, AgentMeshDbContext Db) Build(
        bool authenticated,
        string? claimAgentId = null,
        Dictionary<string, string>? headers = null,
        Dictionary<string, string?>? settings = null,
        string? dbName = null)
    {
        var db = TestDbContextFactory.Create(dbName ?? Guid.NewGuid().ToString());

        var context = new DefaultHttpContext();
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
        {
            // Assigned through the indexer rather than by replacing HttpRequest.Headers, which is
            // read-only; DefaultHttpContext gives a writable dictionary to begin with.
            context.Request.Headers[key] = value;
        }

        if (authenticated && claimAgentId is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, claimAgentId),
            }, authenticationType: "Test"));
        }

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(context);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        return (new PlatformCallContextResolver(
            db, accessor.Object, configuration, NullLogger<PlatformCallContextResolver>.Instance), db);
    }

    private static async Task SeedSessionAsync(
        AgentMeshDbContext db, Guid sessionId, string personaId, Guid? jobId)
    {
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = "Job Turn",
            Status = "thinking",
            PersonaId = personaId,
            DriverId = "rook-driver",
            MetadataJson = jobId is null ? "{}" : $"{{\"jobId\":\"{jobId}\"}}",
        });

        await db.SaveChangesAsync();
    }

    private static string CreateWorkspace(Guid jobId, bool nested)
    {
        var root = Path.Combine(Path.GetTempPath(), "agentmesh-ws-" + Guid.NewGuid().ToString("N"));
        var relative = nested ? Path.Combine("jobs", jobId.ToString()) : jobId.ToString();
        Directory.CreateDirectory(Path.Combine(root, relative, ".git"));
        return root;
    }
}

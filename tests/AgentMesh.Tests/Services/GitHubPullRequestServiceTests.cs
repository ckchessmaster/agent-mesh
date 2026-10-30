using System.Net;
using System.Security.Cryptography;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The push-and-open-PR service: body formatting, credential handling, and the refusal paths.
/// </summary>
/// <remarks>
/// <para>
/// Two things dominate these tests. The first is the 3-R body, which is a contract with the
/// reviewer on the other end — a persona whose prompt tells it to find Result / Reasoning / Risk in
/// the PR description. The second is that no code path here may leak a token, because the strings
/// this service returns are written into the job ledger and broadcast to every Cockpit client.
/// </para>
/// <para>
/// <c>git</c> is never invoked: every test injects the <c>gitRunner</c> seam, and the "is this a
/// repository" precondition is satisfied by creating a <c>.git</c> directory rather than by running
/// <c>git init</c>. A test that shelled out to a real push would either reach GitHub or depend on a
/// scratch repository's state, and neither tells us anything about this service's behaviour.
/// </para>
/// </remarks>
public class GitHubPullRequestServiceTests
{
    private const string Token = "ghp_supersecrettoken1234567890";

    private static Summary3R Summary(
        string result = "Added the tool and pinned it with 12 tests.",
        string reasoning = "Chose REST over a new SDK dependency.",
        string risk = "Reviewer login mapping is an assumption.") =>
        new(result, reasoning, risk);

    private static GitHubPullRequestRequest Request(
        string? workingDirectory = null,
        string? remoteUrl = "https://github.com/ckchessmaster/agent-mesh.git",
        string headBranch = "feat/task-205-session-paging",
        string? baseBranch = "main",
        string? reviewerLogin = "astra-lead") =>
        new(
            Title: "feat(api): platform mcp tools",
            Summary: Summary(),
            HeadBranch: headBranch,
            BaseBranch: baseBranch,
            WorkingDirectory: workingDirectory,
            RemoteUrl: remoteUrl,
            ReviewerLogin: reviewerLogin,
            SenderAgentId: "rook",
            TeamLeadAgentId: reviewerLogin);

    // ── Body formatting ───────────────────────────────────────────────────────────

    [Fact]
    public void FormatPrBody_RendersTheThreeRSectionsAndTheAttributionLine()
    {
        var body = GitHubPullRequestService.FormatPrBody(Summary(), senderAgentId: "rook", teamLeadAgentId: "astra");

        body.Should().Contain("## Summary (3-R Standard)");
        body.Should().Contain("**Result:** Added the tool and pinned it with 12 tests.");
        body.Should().Contain("**Reasoning:** Chose REST over a new SDK dependency.");
        body.Should().Contain("**Risk:** Reviewer login mapping is an assumption.");
        body.Should().Contain("---");
        body.Should().Contain("Automated pull request submitted by AgentMesh Persona @rook.");
        body.Should().Contain("Awaiting review from @astra.");
    }

    [Fact]
    public void FormatPrBody_KeepsTheFieldOrderTheReviewerPromptExpects()
    {
        // The reviewer persona is told to read Result, then Reasoning, then Risk. Reordering the
        // sections would not break a test that only checks containment, so the order is pinned.
        var body = GitHubPullRequestService.FormatPrBody(Summary(), "rook", "astra");

        body.IndexOf("**Result:**", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("**Reasoning:**", StringComparison.Ordinal));
        body.IndexOf("**Reasoning:**", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("**Risk:**", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatPrBody_CollapsesEmbeddedNewlinesSoOneFieldCannotBecomeAnother()
    {
        // A model that puts a bulleted list in `result` would otherwise push "**Reasoning:**" onto a
        // new line and break the single-line shape the reviewer parses.
        var body = GitHubPullRequestService.FormatPrBody(
            Summary(result: "first line\nsecond line"), "rook", "astra");

        body.Should().Contain("**Result:** first line second line");
        body.Split('\n').Count(line => line.TrimStart().StartsWith("**Result:", StringComparison.Ordinal))
            .Should().Be(1);
    }

    [Fact]
    public void FormatPrBody_LabelsAnOmittedFieldInsteadOfPrintingAnEmptyLine()
    {
        var body = GitHubPullRequestService.FormatPrBody(
            new Summary3R("Real result.", "", "   "), "rook", "astra");

        body.Should().Contain("**Result:** Real result.");
        body.Should().Contain("**Reasoning:** _Not provided._");
        body.Should().Contain("**Risk:** _Not provided._");
    }

    [Fact]
    public void FormatPrBody_DegradesGracefullyWithoutIdentities()
    {
        var body = GitHubPullRequestService.FormatPrBody(Summary(), senderAgentId: null, teamLeadAgentId: null);

        body.Should().Contain("submitted by AgentMesh.");
        body.Should().Contain("Awaiting review from the assigned reviewer.");
    }

    [Fact]
    public void FormatPrBody_StripsAnAtSignSomeoneAlreadyTypedIntoTheLeadId()
    {
        // The reviewer id arrives from configuration or a persona id, both of which are written in
        // several styles. Doubling the sigil produces "@@astra", which GitHub does not resolve.
        var body = GitHubPullRequestService.FormatPrBody(Summary(), "@rook", "@astra");

        body.Should().Contain("@rook.").And.NotContain("@@");
    }

    // ── Repository parsing ────────────────────────────────────────────────────────

    /// <remarks>
    /// The parser takes the last two path segments. For GitHub — the only host this platform
    /// actually pushes to — the owner is always the segment after the host and there are exactly
    /// two, so the two rules coincide. The nested case is pinned to document which rule wins when
    /// they do not: a GitLab subgroup URL resolves to its nearest namespace rather than to the
    /// top-level group, which is the shape that is a real repository on that host.
    /// </remarks>
    [Theory]
    [InlineData("https://github.com/ckchessmaster/agent-mesh.git", "ckchessmaster", "agent-mesh")]
    [InlineData("https://github.com/ckchessmaster/agent-mesh", "ckchessmaster", "agent-mesh")]
    [InlineData("https://github.com/ckchessmaster/agent-mesh/", "ckchessmaster", "agent-mesh")]
    [InlineData("git@github.com:ckchessmaster/agent-mesh.git", "ckchessmaster", "agent-mesh")]
    [InlineData("ckchessmaster/agent-mesh", "ckchessmaster", "agent-mesh")]
    [InlineData("https://gitlab.internal/group/subgroup/widgets.git", "subgroup", "widgets")]
    public void ParseOwnerRepo_HandlesEveryRemoteShapeThePlatformProduces(
        string input, string expectedOwner, string expectedRepo)
    {
        var parsed = GitHubPullRequestService.ParseOwnerRepo(input);

        parsed.Should().NotBeNull();
        parsed!.Value.Owner.Should().Be(expectedOwner);
        parsed.Value.Repo.Should().Be(expectedRepo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just-a-name")]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com")]
    public void ParseOwnerRepo_ReturnsNullRatherThanGuessing(string? input) =>
        // A wrong owner is not a harmless default: it posts a 404 against somebody else's
        // repository, or worse, succeeds against a same-named repo the team does not own.
        GitHubPullRequestService.ParseOwnerRepo(input).Should().BeNull();

    [Fact]
    public void ParseOwnerRepo_ReturnsNullForNull() =>
        GitHubPullRequestService.ParseOwnerRepo(null).Should().BeNull();

    [Fact]
    public void ParseOwnerRepo_StripsEmbeddedCredentialsWithoutPropagatingThem()
    {
        // A remote that was once saved from an authenticated push URL can carry a token. The parsed
        // result must be the repository only.
        var parsed = GitHubPullRequestService.ParseOwnerRepo(
            "https://x-access-token:ghp_leakme@github.com/acme/widgets.git");

        parsed.Should().NotBeNull();
        parsed!.Value.Owner.Should().Be("acme");
        parsed.Value.Repo.Should().Be("widgets");
    }

    // ── Redaction ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Redact_RemovesTheTokenFromEveryFormItCouldAppearIn()
    {
        var escaped = Uri.EscapeDataString(Token);
        var input = $"remote: https://x-access-token:{Token}@github.com/acme/widgets.git " +
                    $"and escaped {escaped} and again {Token}";

        var redacted = GitHubPullRequestService.Redact(input, Token);

        redacted.Should().NotContain(Token);
        redacted.Should().NotContain(escaped);
        redacted.Should().Contain("[redacted]");
    }

    [Fact]
    public void Redact_ScrubsAnyCredentialPairEvenWhenTheTokenIsUnknown()
    {
        // The failure may come from a remote that already carried different credentials, whose value
        // this call site has no way to know.
        var redacted = GitHubPullRequestService.Redact(
            "fatal: unable to access 'https://someone:ghp_othersecret@github.com/a/b/'", Token);

        redacted.Should().NotContain("ghp_othersecret");
    }

    [Fact]
    public void Redact_ToleratesNullAndEmptyInput()
    {
        GitHubPullRequestService.Redact(null, Token).Should().BeEmpty();
        GitHubPullRequestService.Redact(string.Empty, Token).Should().BeEmpty();
        GitHubPullRequestService.Redact("clean", string.Empty).Should().Be("clean");
    }

    // ── Configuration ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReadOptions_TakesTheTokenFromConfigurationOnly()
    {
        // No hand-rolled GITHUB_TOKEN / GH_TOKEN fallback: the host already layers environment
        // variables over the JSON files, and a manual lookup here would make this class's behaviour
        // depend on whatever the test runner's process happens to export.
        var service = BuildService(new Dictionary<string, string?> { ["GitHub:Token"] = "ghp_from_config" });
        service.ReadOptions().Token.Should().Be("ghp_from_config");

        BuildService(new Dictionary<string, string?>()).ReadOptions().Token.Should().BeNull();
    }

    [Fact]
    public void ReadOptions_DefaultsToThePublicApiAndHonoursAnOverride()
    {
        BuildService(new Dictionary<string, string?>()).ReadOptions()
            .ApiBaseUrl.Should().Be("https://api.github.com");

        BuildService(new Dictionary<string, string?> { ["GitHub:ApiBaseUrl"] = "http://localhost:8085/api/v3/" })
            .ReadOptions().ApiBaseUrl.Should().Be("http://localhost:8085/api/v3/");
    }

    [Fact]
    public void ReadOptions_ReadsTheThreeGitHubAppSettings()
    {
        var options = BuildService(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "123456",
            ["GitHub:AppInstallationId"] = "987",
        }).ReadOptions();

        options.AppId.Should().Be("123456");
        options.AppInstallationId.Should().Be("987");
    }

    // ── Private key normalisation ─────────────────────────────────────────────────

    [Fact]
    public void NormalizePrivateKey_UnescapesASingleLineKeyFromAnEnvironmentVariable()
    {
        var pem = GeneratePrivateKeyPem();
        var singleLine = pem.Replace("\r\n", "\\n").Replace("\n", "\\n");

        singleLine.Should().NotContain("\n", "the configured value arrived as one physical line");

        var normalized = GitHubPullRequestService.NormalizePrivateKey(singleLine);

        using var rsa = RSA.Create();
        var act = () => rsa.ImportFromPem(normalized!.AsSpan());
        act.Should().NotThrow("a key pasted from a Helm value or K8s secret must load without reformatting");
    }

    [Fact]
    public void NormalizePrivateKey_ReArmoursBareBase64()
    {
        var pem = GeneratePrivateKeyPem();
        var bare = pem
            .Replace("-----BEGIN RSA PRIVATE KEY-----", string.Empty)
            .Replace("-----END RSA PRIVATE KEY-----", string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Trim();

        var normalized = GitHubPullRequestService.NormalizePrivateKey(bare);

        normalized.Should().StartWith("-----BEGIN RSA PRIVATE KEY-----");
        normalized.Should().EndWith("-----END RSA PRIVATE KEY-----");

        using var rsa = RSA.Create();
        ((Action)(() => rsa.ImportFromPem(normalized!.AsSpan()))).Should().NotThrow();
    }

    [Fact]
    public void NormalizePrivateKey_LeavesAWellFormedPemAlone()
    {
        var pem = GeneratePrivateKeyPem();
        GitHubPullRequestService.NormalizePrivateKey(pem).Should().Be(pem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizePrivateKey_ReturnsNullForBlankInput(string? input) =>
        GitHubPullRequestService.NormalizePrivateKey(input).Should().BeNull();

    [Fact]
    public void CreateAppJwt_ProducesAVerifiableRs256Token()
    {
        var pem = GeneratePrivateKeyPem();

        var jwt = GitHubPullRequestService.CreateAppJwt("123456", pem, DateTimeOffset.UnixEpoch);

        var parts = jwt.Split('.');
        parts.Should().HaveCount(3);

        var header = DecodeSegment(parts[0]);
        header.GetProperty("alg").GetString().Should().Be("RS256");
        header.GetProperty("typ").GetString().Should().Be("JWT");

        var claims = DecodeSegment(parts[1]);
        claims.GetProperty("iss").GetString().Should().Be("123456");
        // GitHub tolerates a minute of clock skew and rejects a token issued in the future; winding
        // iat back is the documented fix, and it is load-bearing enough to pin.
        claims.GetProperty("iat").GetInt64().Should().Be(-60);
        claims.GetProperty("exp").GetInt64().Should().Be(540);

        // Verify the signature over the exact bytes that were signed, with the public half of the
        // same key. Without this, a wrong-but-parseable JWT looks identical until GitHub 401s.
        var signingInput = System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecode(parts[2]);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem.AsSpan());
        rsa.VerifyData(signingInput, signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1)
            .Should().BeTrue();
    }

    [Fact]
    public void CreateAppJwt_ThrowsOnAKeyThatIsNotPemRatherThanSendingGarbage()
    {
        // ImportFromPem rejects a value with no armor block at all. Failing here is what keeps a
        // misconfigured key from producing a well-formed-but-unsigned JWT that GitHub answers with
        // an unhelpful 401 on the token exchange.
        var act = () => GitHubPullRequestService.CreateAppJwt("123", "not a pem at all");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateAppJwt_ThrowsOnPemArmorWrappedAroundNonBase64()
    {
        // The other half of the failure surface: a value that *looks* like a PEM because
        // NormalizePrivateKey re-armoured a truncated base64 body. The armor parses, the body does
        // not, and the caller still gets an exception rather than a bogus token.
        var armored = GitHubPullRequestService.NormalizePrivateKey("not-valid-base64-@#$%");

        var act = () => GitHubPullRequestService.CreateAppJwt("123", armored!);

        // ImportFromPem reports an unrecognized body as a bad argument. Either exception type is an
        // acceptable outcome; what matters is that the caller gets a throw rather than a JWT signed
        // with nothing, which GitHub would answer with an opaque 401 on the token exchange.
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ResolveTokenAsync_PrefersAConfiguredPatOverTheAppSettings()
    {
        // All three App settings are present and valid, so if the PAT did not win outright this
        // would attempt an exchange against the stub - which has no route registered and answers
        // 404 - and return null. Getting the PAT back is therefore the proof that the App path was
        // never taken, without needing a strict mock to assert "no call happened".
        var handler = new StubHttpHandler();
        var service = BuildService(new Dictionary<string, string?>
        {
            ["GitHub:Token"] = "ghp_pat",
            ["GitHub:AppId"] = "123",
            ["GitHub:AppPrivateKey"] = GeneratePrivateKeyPem(),
            ["GitHub:AppInstallationId"] = "987",
        }, handler: handler);

        (await service.ResolveTokenAsyncPublic(service.ReadOptions())).Should().Be("ghp_pat");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveTokenAsync_ReturnsNullWhenNoCredentialIsConfigured()
    {
        var service = BuildService(new Dictionary<string, string?>());

        (await service.ResolveTokenAsyncPublic(service.ReadOptions())).Should().BeNull();
    }

    [Fact]
    public async Task ResolveTokenAsync_RefusesPartialAppConfigurationRatherThanGuessing()
    {
        // A key without an installation id is the common misconfiguration. Minting against a
        // guessed installation could open a PR inside a different org the same App is installed on.
        var service = BuildService(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "123",
            ["GitHub:AppPrivateKey"] = GeneratePrivateKeyPem(),
        });

        (await service.ResolveTokenAsyncPublic(service.ReadOptions())).Should().BeNull();
    }

    [Fact]
    public async Task ResolveTokenAsync_MintsAnInstallationTokenWhenFullyConfigured()
    {
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Post, "/app/installations/987/access_tokens",
                (int)HttpStatusCode.Created, """{"token":"ghs_installationtoken","expires_at":"2026-01-01T00:00:00Z"}""");

        var service = BuildService(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "123",
            ["GitHub:AppPrivateKey"] = GeneratePrivateKeyPem(),
            ["GitHub:AppInstallationId"] = "987",
        }, handler: handler);

        var token = await service.ResolveTokenAsyncPublic(service.ReadOptions());

        token.Should().Be("ghs_installationtoken");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/app/installations/987/access_tokens");
        // The exchange is authenticated with the App JWT, not with the PAT.
        handler.Requests[0].Authorization.Should().StartWith("Bearer eyJ");
    }

    [Fact]
    public async Task ResolveTokenAsync_DegradesToNullWhenTheExchangeIsRejected()
    {
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Post, "/app/installations/987/access_tokens",
                (int)HttpStatusCode.Forbidden, """{"message":"This installation is not authorized."}""");

        var service = BuildService(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "123",
            ["GitHub:AppPrivateKey"] = GeneratePrivateKeyPem(),
            ["GitHub:AppInstallationId"] = "987",
        }, handler: handler);

        // A rejected exchange must fall through to the simulated path rather than throw: the caller
        // is a model mid-turn and needs a readable payload, not a 500.
        (await service.ResolveTokenAsyncPublic(service.ReadOptions())).Should().BeNull();
    }

    // ── Simulated (no-credentials) path ───────────────────────────────────────────

    [Fact]
    public async Task PushBranchAndCreatePr_WithoutCredentials_SimulatesAndNeverTouchesGit()
    {
        var gitCalls = new List<GitCommandRequest>();
        var service = BuildService(new Dictionary<string, string?>(), gitRunner: CaptureGit(gitCalls));

        var result = await service.PushBranchAndCreatePrAsync(Request(workingDirectory: CreateScratchRepository()));

        result.Success.Should().BeTrue("a local Aspire run must not dead-end the agent");
        result.Simulated.Should().BeTrue();
        result.PrNumber.Should().Be(GitHubPullRequestService.SimulatedPrNumber);
        result.Message.Should().Contain("Simulated")
            .And.Contain("no GitHub token configured")
            .And.Contain("nothing was pushed");
        gitCalls.Should().BeEmpty("pushing a developer's working copy to get a fake PR is destructive");
    }

    [Fact]
    public async Task PushBranchAndCreatePr_SimulatedResult_StillReportsTheResolvedBranches()
    {
        var service = BuildService(new Dictionary<string, string?>(), gitRunner: CapturedGit(GitOk));

        var result = await service.PushBranchAndCreatePrAsync(Request(headBranch: "feat/x", baseBranch: "main"));

        result.HeadBranch.Should().Be("feat/x");
        result.BaseBranch.Should().Be("main");
        result.PrUrl.Should().EndWith("/pull/999");
    }

    // ── Validation refusals ───────────────────────────────────────────────────────

    [Fact]
    public async Task PushBranchAndCreatePr_RejectsABlankTitle()
    {
        var service = BuildService(TokenConfig(), gitRunner: CapturedGit(GitOk));

        var result = await service.PushBranchAndCreatePrAsync(Request() with { Title = "   " });

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("invalid_title");
    }

    [Fact]
    public async Task PushBranchAndCreatePr_RejectsABranchThatCannotBeResolved()
    {
        var service = BuildService(TokenConfig(), gitRunner: CapturedGit(GitOk));

        var result = await service.PushBranchAndCreatePrAsync(Request(headBranch: "  "));

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("invalid_branch");
    }

    [Fact]
    public async Task PushBranchAndCreatePr_RejectsHeadEqualToBase()
    {
        var service = BuildService(TokenConfig(), gitRunner: CapturedGit(GitOk));

        var result = await service.PushBranchAndCreatePrAsync(Request(headBranch: "main", baseBranch: "main"));

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("branch_equals_base");
        result.Message.Should().Contain("two different branches");
    }

    [Fact]
    public async Task PushBranchAndCreatePr_DefaultsTheBaseBranchToMain()
    {
        var service = BuildService(new Dictionary<string, string?>(), gitRunner: CapturedGit(GitOk));

        var result = await service.PushBranchAndCreatePrAsync(Request(baseBranch: null));

        result.BaseBranch.Should().Be("main");
    }

    [Fact]
    public async Task PushBranchAndCreatePr_RejectsAMissingWorkingDirectoryRatherThanPushingSomethingElse()
    {
        var gitCalls = new List<GitCommandRequest>();
        var service = BuildService(TokenConfig(), gitRunner: CaptureGit(gitCalls));

        var result = await service.PushBranchAndCreatePrAsync(Request(workingDirectory: null));

        // The control plane owns no checkout. Falling back to its own working directory would push
        // whichever repository the API process happened to start in — a different repo entirely.
        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("no_working_directory");
        gitCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task PushBranchAndCreatePr_RejectsADirectoryThatIsNotARepository()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentmesh-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var gitCalls = new List<GitCommandRequest>();
            var service = BuildService(TokenConfig(), gitRunner: CaptureGit(gitCalls));

            var result = await service.PushBranchAndCreatePrAsync(Request(workingDirectory: directory));

            result.FailureReason.Should().Be("not_a_git_repository");
            gitCalls.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PushBranchAndCreatePr_RejectsWhenNoRepositoryCanBeResolved()
    {
        var service = BuildService(TokenConfig(), gitRunner: CapturedGit(GitOk));

        var result = await service.PushBranchAndCreatePrAsync(
            Request(remoteUrl: null) with { WorkingDirectory = CreateScratchRepository() });

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("repository_unresolved");
    }

    // ── Happy path over the wire ──────────────────────────────────────────────────

    [Fact]
    public async Task PushBranchAndCreatePr_PushesThenOpensThePrAndRequestsTheReviewer()
    {
        var directory = CreateScratchRepository();
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Post, "/repos/ckchessmaster/agent-mesh/pulls", (int)HttpStatusCode.Created,
                """{"number": 42, "html_url": "https://github.com/ckchessmaster/agent-mesh/pull/42"}""")
            .Respond(HttpMethod.Post, "/repos/ckchessmaster/agent-mesh/pulls/42/requested_reviewers",
                (int)HttpStatusCode.Created, "{}");

        var gitCalls = new List<GitCommandRequest>();
        var service = BuildService(TokenConfig(), CaptureGit(gitCalls), handler);

        try
        {
            var result = await service.PushBranchAndCreatePrAsync(Request(workingDirectory: directory));

            result.Success.Should().BeTrue(result.Message);
            result.Simulated.Should().BeFalse();
            result.PrNumber.Should().Be(42);
            result.PrUrl.Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/42");
            result.Message.Should().Be("PR #42 opened: https://github.com/ckchessmaster/agent-mesh/pull/42. " +
                                       "Assigned to @astra-lead for review.");

            gitCalls.Should().ContainSingle();
            gitCalls[0].WorkingDirectory.Should().Be(directory);
            gitCalls[0].Arguments.Should().Equal(
                "push", "--set-upstream",
                $"https://x-access-token:{Token}@github.com/ckchessmaster/agent-mesh.git",
                "feat/task-205-session-paging:feat/task-205-session-paging");

            // The prompt-suppression variables are the difference between an error and a hang: with
            // a credential prompt enabled, git waits on a terminal that will never answer.
            gitCalls[0].Environment["GIT_TERMINAL_PROMPT"].Should().Be("0");
            gitCalls[0].Environment["GCM_INTERACTIVE"].Should().Be("never");

            handler.Requests.Should().HaveCount(2);
            handler.Requests[0].Method.Should().Be(HttpMethod.Post);
            handler.Requests[0].Body.Should().Contain("\"head\":\"feat/task-205-session-paging\"")
                .And.Contain("\"base\":\"main\"")
                .And.Contain("**Result:** Added the tool");
            handler.Requests[0].Authorization.Should().Be($"Bearer {Token}");
            handler.Requests[1].Path.Should().Be("/repos/ckchessmaster/agent-mesh/pulls/42/requested_reviewers");
            handler.Requests[1].Body.Should().Contain("\"reviewers\":[\"astra-lead\"]");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PushBranchAndCreatePr_ReportsPushFailureAndNeverCallsTheApi()
    {
        var directory = CreateScratchRepository();
        var handler = new StubHttpHandler();
        var service = BuildService(TokenConfig(),
            CapturedGit(new GitCommandResult(128, "",
                "remote: Permission to ckchessmaster/agent-mesh.git denied to rook-bot.")), handler);

        try
        {
            var result = await service.PushBranchAndCreatePrAsync(Request(workingDirectory: directory));

            result.Success.Should().BeFalse();
            result.FailureReason.Should().Be("push_failed");
            result.Message.Should().Contain("Permission to ckchessmaster/agent-mesh.git denied");
            handler.Requests.Should().BeEmpty("a failed push must not be followed by a PR request");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PushBranchAndCreatePr_KeepsAPushedBranchHonestWhenThePrCallFails()
    {
        var directory = CreateScratchRepository();
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Post, "/repos/ckchessmaster/agent-mesh/pulls",
                (int)HttpStatusCode.UnprocessableEntity,
                """{"message":"Validation Failed","errors":[{"message":"No commits between main and feat/x"}]}""");

        var service = BuildService(TokenConfig(), CapturedGit(GitOk), handler);

        try
        {
            var result = await service.PushBranchAndCreatePrAsync(Request(workingDirectory: directory));

            result.Success.Should().BeFalse();
            result.FailureReason.Should().Be("pr_create_failed");
            // The branch really is on the remote. Saying only "failed" would send the agent off to
            // re-push it and collide with its own branch.
            result.Message.Should().Contain("was pushed to ckchessmaster/agent-mesh");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PushBranchAndCreatePr_RedactsTheTokenFromAPushFailureMessage()
    {
        var directory = CreateScratchRepository();
        var service = BuildService(TokenConfig(), CapturedGit(new GitCommandResult(128,
            $"fatal: unable to access 'https://x-access-token:{Token}@github.com/ckchessmaster/agent-mesh.git/': " +
            "The requested URL returned error: 403", "")));

        try
        {
            var result = await service.PushBranchAndCreatePrAsync(Request(workingDirectory: directory));

            // This string is written into the job ledger and broadcast to every Cockpit client.
            result.Message.Should().NotContain(Token);
            result.Message.Should().Contain("[redacted]");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PushBranchAndCreatePr_SkipsTheReviewRequestWhenNoReviewerIsConfigured()
    {
        var directory = CreateScratchRepository();
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Post, "/repos/ckchessmaster/agent-mesh/pulls", (int)HttpStatusCode.Created,
                """{"number": 7, "html_url": "https://github.com/ckchessmaster/agent-mesh/pull/7"}""");

        var service = BuildService(TokenConfig(), CapturedGit(GitOk), handler);

        try
        {
            var result = await service.PushBranchAndCreatePrAsync(
                Request(workingDirectory: directory, reviewerLogin: null));

            result.Success.Should().BeTrue(result.Message);
            result.PrNumber.Should().Be(7);
            handler.Requests.Should().ContainSingle("a project without a lead must still get its PR opened");
            result.Message.Should().NotContain("review request");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PushBranchAndCreatePr_ReportsAFailedReviewRequestWithoutFailingThePr()
    {
        var directory = CreateScratchRepository();
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Post, "/repos/ckchessmaster/agent-mesh/pulls", (int)HttpStatusCode.Created,
                """{"number": 8, "html_url": "https://github.com/ckchessmaster/agent-mesh/pull/8"}""")
            .Respond(HttpMethod.Post, "/repos/ckchessmaster/agent-mesh/pulls/8/requested_reviewers",
                (int)HttpStatusCode.NotFound,
                """{"message":"Could not resolve to a User with the login of 'astra-lead'."}""");

        var service = BuildService(TokenConfig(), CapturedGit(GitOk), handler);

        try
        {
            var result = await service.PushBranchAndCreatePrAsync(Request(workingDirectory: directory));

            // The PR exists and the branch is pushed. Reporting the whole call as failed would make
            // the agent re-open a duplicate PR over a cosmetic assignment miss.
            result.Success.Should().BeTrue();
            result.Message.Should().Contain("PR #8 opened")
                .And.Contain("review request for @astra-lead failed")
                .And.Contain("assign it manually");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PushBranchAndCreatePr_LeavesAnSshRemoteUnrewritten()
    {
        var directory = CreateScratchRepository();
        var gitCalls = new List<GitCommandRequest>();
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Post, "/repos/acme/widgets/pulls", (int)HttpStatusCode.Created,
                """{"number": 1, "html_url": "https://github.com/acme/widgets/pull/1"}""");

        var service = BuildService(TokenConfig(), CaptureGit(gitCalls), handler);

        try
        {
            var result = await service.PushBranchAndCreatePrAsync(
                Request(workingDirectory: directory, remoteUrl: "git@github.com:acme/widgets.git") with
                {
                    RepositoryOwner = "acme",
                    RepositoryName = "widgets",
                });

            result.Success.Should().BeTrue(result.Message);
            gitCalls.Should().ContainSingle();
            // A PAT pasted into an ssh URL produces an opaque ssh failure; the runner's own key is
            // what authenticates this transport.
            gitCalls[0].Arguments[2].Should().Be("git@github.com:acme/widgets.git");
            gitCalls[0].Arguments[2].Should().NotContain(Token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PushBranchAndCreatePr_FallsBackToConfiguredRepositoryWhenTheRemoteIsUnusable()
    {
        var directory = CreateScratchRepository();
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Post, "/repos/configured/fallback/pulls", (int)HttpStatusCode.Created,
                """{"number": 3, "html_url": "https://github.com/configured/fallback/pull/3"}""");

        var gitCalls = new List<GitCommandRequest>();
        var service = BuildService(new Dictionary<string, string?>
        {
            ["GitHub:Token"] = Token,
            ["GitHub:Repository"] = "configured/fallback",
        }, CaptureGit(gitCalls), handler);

        try
        {
            var result = await service.PushBranchAndCreatePrAsync(
                Request(workingDirectory: directory, remoteUrl: "origin"));

            result.Success.Should().BeTrue(result.Message);
            result.PrNumber.Should().Be(3);
            handler.Requests.Select(r => r.Path).Should().Equal(
                "/repos/configured/fallback/pulls",
                "/repos/configured/fallback/pulls/3/requested_reviewers");
            // "origin" is a remote *name*, not a URL this service can inject credentials into, so it
            // is passed through untouched and git resolves it against the repository's own config.
            gitCalls[0].Arguments[2].Should().Be("origin");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PushBranchAndCreatePr_UsesTheExplicitOwnerAndRepoAheadOfTheRemote()
    {
        var directory = CreateScratchRepository();
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Post, "/repos/forked/upstream/pulls", (int)HttpStatusCode.Created,
                """{"number": 11, "html_url": "https://github.com/forked/upstream/pull/11"}""");

        var service = BuildService(TokenConfig(), CapturedGit(GitOk), handler);

        try
        {
            var result = await service.PushBranchAndCreatePrAsync(
                Request(workingDirectory: directory, remoteUrl: "https://github.com/ckchessmaster/agent-mesh.git") with
                {
                    RepositoryOwner = "forked",
                    RepositoryName = "upstream",
                });

            result.Success.Should().BeTrue(result.Message);
            handler.Requests.Should().Contain(r => r.Path == "/repos/forked/upstream/pulls",
                "the explicit owner/repo must win over the job's own remote URL");
            handler.Requests.Should().NotContain(r => r.Path.Contains("ckchessmaster"),
                "otherwise the PR would be opened against the upstream repo instead of the fork");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    private static readonly GitCommandResult GitOk = new(0, "", "");

    private static Dictionary<string, string?> TokenConfig() => new() { ["GitHub:Token"] = Token };

    private static TestableGitHubPullRequestService BuildService(
        Dictionary<string, string?> settings,
        Func<GitCommandRequest, CancellationToken, Task<GitCommandResult>>? gitRunner = null,
        StubHttpHandler? handler = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new TestableGitHubPullRequestService(
            StubFactory(handler).Object, configuration,
            NullLogger<GitHubPullRequestService>.Instance, gitRunner);
    }

    private static Mock<IHttpClientFactory> StubFactory(StubHttpHandler? handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GitHubPullRequestService.HttpClientName))
            .Returns(() => new HttpClient(handler ?? new StubHttpHandler()));
        return factory;
    }

    private static Func<GitCommandRequest, CancellationToken, Task<GitCommandResult>> CaptureGit(
        List<GitCommandRequest> sink) =>
        (request, _) =>
        {
            sink.Add(request);
            return Task.FromResult(GitOk);
        };

    private static Func<GitCommandRequest, CancellationToken, Task<GitCommandResult>> CapturedGit(
        GitCommandResult result) =>
        (_, _) => Task.FromResult(result);

    /// <summary>
    /// A directory containing a <c>.git</c> entry, which is the whole precondition the service
    /// checks before it will run <c>git</c>.
    /// </summary>
    /// <remarks>
    /// Created as a plain directory rather than by invoking <c>git init</c>: the service never reads
    /// the repository, it only refuses to shell out somewhere that is not one. Keeping a real
    /// process out of the fixture also keeps these tests honest about what they prove — nothing here
    /// depends on git being installed or on a repository's HEAD state.
    /// </remarks>
    private static string CreateScratchRepository()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentmesh-pr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        return directory;
    }

    private static string? _cachedPrivateKey;

    /// <summary>
    /// A throwaway RSA key in PEM form, generated once per test class.
    /// </summary>
    /// <remarks>
    /// Generated rather than embedded so no committed file looks like a credential, and cached
    /// because key generation is the slowest thing these tests do.
    /// </remarks>
    private static string GeneratePrivateKeyPem() => _cachedPrivateKey ??=
        RSA.Create(2048)
            .ExportRSAPrivateKeyPem()
            .Replace("\r\n", "\n");

    private static System.Text.Json.JsonElement DecodeSegment(string segment)
    {
        var json = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(segment));
        return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// A test-only subclass that reaches the protected credential resolver.
/// </summary>
/// <remarks>
/// <see cref="GitHubPullRequestService.ResolveTokenAsync"/> is protected because it is the seam a
/// future credential source replaces. Making it public to satisfy a test would advertise it as
/// supported API; a subclass in the test assembly keeps the production surface honest.
/// </remarks>
internal sealed class TestableGitHubPullRequestService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<GitHubPullRequestService> logger,
    Func<GitCommandRequest, CancellationToken, Task<GitCommandResult>>? gitRunner = null)
    : GitHubPullRequestService(httpClientFactory, configuration, logger, gitRunner)
{
    public Task<string?> ResolveTokenAsyncPublic(GitHubPullRequestOptions options) =>
        base.ResolveTokenAsync(options, CancellationToken.None);
}

/// <summary>
/// Records requests and replays canned responses matched by method and path, so the GitHub calls can
/// be asserted without a network.
/// </summary>
/// <remarks>
/// Matched by path rather than by call order on purpose: an ordering-based stub passes for the wrong
/// reason when a call is added or removed, and the failure it reports is "response 2 came back for
/// request 1" instead of the actual mistake.
/// </remarks>
internal sealed class StubHttpHandler : HttpClientHandler
{
    private readonly List<(HttpMethod Method, string Path, int Status, string Body)> _responses = new();

    public sealed record RecordedRequest(HttpMethod Method, string Path, string Body, string? Authorization);

    public List<RecordedRequest> Requests { get; } = new();

    public StubHttpHandler Respond(HttpMethod method, string path, int status, string body)
    {
        _responses.Add((method, path, status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!.AbsolutePath,
            body,
            request.Headers.Authorization?.ToString()));

        var match = _responses.FirstOrDefault(r =>
            r.Method == request.Method && r.Path == request.RequestUri.AbsolutePath);

        if (match.Path is not null)
        {
            return new HttpResponseMessage((HttpStatusCode)match.Status)
            {
                Content = new StringContent(match.Body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}"""),
        };
    }
}

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMesh.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Services;

/// <summary>
/// Pushes a job branch and opens a pull request for it, from the control plane.
/// </summary>
/// <remarks>
/// The container a persona runs in deliberately holds no push credentials it needs to use
/// directly; this is the path that lets an agent hand work to a reviewer without ever assembling
/// a <c>git push</c> command line or handling a token.
/// </remarks>
public interface IGitHubPullRequestService
{
    Task<GitHubPullRequestResult> PushBranchAndCreatePrAsync(
        GitHubPullRequestRequest request,
        CancellationToken ct = default);
}

/// <summary>A single <c>git</c> invocation, already resolved to an argument vector.</summary>
/// <remarks>
/// A record rather than a command string: the arguments go to <c>ProcessStartInfo.ArgumentList</c>
/// verbatim, so nothing here is re-parsed by a shell and a branch name containing a space or a
/// shell metacharacter cannot become an injection point.
/// </remarks>
public readonly record struct GitCommandRequest(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment,
    int TimeoutSeconds);

public readonly record struct GitCommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// The GitHub settings the service reads.
/// </summary>
/// <param name="Token">
/// A classic or fine-grained PAT with <c>repo</c> scope. Wins over the App settings when both are
/// set, because a PAT is the explicit "use this identity" override an operator reaches for when an
/// App installation is misbehaving.
/// </param>
/// <param name="AppId">GitHub App id, for App-based auth.</param>
/// <param name="AppPrivateKey">
/// The App's RSA private key in PEM form. Accepts the two shapes configuration can actually carry:
/// a real multi-line PEM, or a single-line value with escaped <c>\n</c> (what
/// <c>GitHub__AppPrivateKey</c> delivers from a shell or a K8s secret).
/// </param>
/// <param name="AppInstallationId">
/// The installation to mint a token against. Required for App auth: the repository-access list is a
/// separate call, and guessing an installation id out of several would mean opening PRs against
/// whichever org happened to be first.
/// </param>
/// <param name="Repository">
/// Fallback <c>owner/repo</c> for when the caller cannot derive one from a job's remote URL.
/// </param>
/// <param name="ApiBaseUrl">Overrides the API host for GitHub Enterprise or a stubbed test server.</param>
public sealed record GitHubPullRequestOptions(
    string? Token,
    string? AppId,
    string? AppPrivateKey,
    string? AppInstallationId,
    string? Repository,
    string ApiBaseUrl);

/// <summary>
/// Server-side push + pull-request creation over the GitHub REST API.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why <c>git</c> for the push and REST for the PR.</strong> The job spec offers Octokit
/// "or standard HttpClient". Octokit is not referenced anywhere in the solution, and it has no
/// push transport at all — a PR client cannot move objects, so the push needs <c>git</c> (or a
/// from-scratch packfile writer) regardless. Adding a dependency that covers half the operation
/// to save hand-rolling one <c>POST /repos/{owner}/{repo}/pulls</c> call is not a trade worth
/// taking, so the push shells out to <c>git</c> and the PR is one JSON request.
/// </para>
/// <para>
/// <strong>Where the working tree comes from.</strong> The control plane owns no checkout. A
/// job's work lives in the runner's per-job directory (<c>~/.agentmesh/workspaces/jobs/&lt;jobId&gt;</c>),
/// which is a cache on the runner's filesystem. The caller therefore passes
/// <see cref="GitHubPullRequestRequest.WorkingDirectory"/> explicitly, and this service refuses to
/// guess: with no usable directory it reports <c>no_working_directory</c> rather than pushing
/// whatever repository happens to be at the API process's current working directory, which would
/// be a different repo entirely.
/// </para>
/// <para>
/// <strong>Why the token goes in the push URL and nowhere else.</strong>
/// <c>git push &lt;url&gt;</c> with embedded credentials is the only way to authenticate a one-shot
/// push without mutating the repository's <c>.git/config</c>, which is shared state on a runner
/// that may host several personas. The URL is redacted from every log line and from every string
/// returned to the caller.
/// </para>
/// <para>
/// <strong>Why unconfigured credentials are a simulated success and not a failure.</strong> The
/// job spec asks for a graceful local-Aspire fallback. Failing hard makes the tool useless in
/// development, where there is no token to configure; failing silently makes an agent believe a
/// review was requested when it was not. The compromise is a fabricated PR number with
/// <see cref="GitHubPullRequestResult.Simulated"/> set, and the word "simulated" in the message the
/// agent reads back. The number is deliberately out of GitHub's real range for any repo this
/// project has, so a pasted link cannot be mistaken for a live PR.
/// </para>
/// <para>
/// <strong>GitHub App auth is implemented, and needs three settings.</strong> The spec names
/// <c>GitHub:AppPrivateKey</c>. A private key alone cannot produce a usable credential: an App
/// authenticates as itself and must then exchange a signed JWT for a token scoped to one
/// <em>installation</em>. Picking that installation means listing installations and matching one to
/// the repository, which is a second API call whose failure mode is opening a PR against the wrong
/// org when an App is installed on several. <c>GitHub:AppId</c> and <c>GitHub:AppInstallationId</c>
/// are therefore required alongside the key; a partial configuration is logged and refused rather
/// than guessed at. A configured <c>GitHub:Token</c> always wins, so an operator can pin an
/// identity without touching the App settings.
/// </para>
/// </remarks>
public class GitHubPullRequestService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<GitHubPullRequestService> logger,
    Func<GitCommandRequest, CancellationToken, Task<GitCommandResult>>? gitRunner = null) : IGitHubPullRequestService
{
    /// <summary>The number handed back when there are no credentials to open a real PR with.</summary>
    public const int SimulatedPrNumber = 999;

    public const string HttpClientName = "agentmesh-github";

    private const int DefaultPushTimeoutSeconds = 120;

    /// <summary>
    /// Options for outbound GitHub request bodies.
    /// </summary>
    /// <remarks>
    /// Compact, not indented. GitHub accepts either, but a request body is not for humans to read,
    /// and the indented form puts a space after every colon — which makes an assertion about what
    /// this service actually sent (see the tests pinning <c>"head":"..."</c>) depend on formatting
    /// rather than on content.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<GitCommandRequest, CancellationToken, Task<GitCommandResult>> _gitRunner =
        gitRunner ?? RunGitAsync;

    /// <summary>
    /// The markdown body every AgentMesh PR carries, in the 3-R template.
    /// </summary>
    /// <remarks>
    /// Exposed and static because it is a contract, not an implementation detail: the reviewer
    /// reading the PR is a persona whose prompt tells it to expect Result / Reasoning / Risk here,
    /// and the tool's own output quotes the same three fields. Splitting the formatter out is what
    /// lets a test pin the exact shape without a network or a git repository.
    /// </remarks>
    public static string FormatPrBody(
        Summary3R summary,
        string? senderAgentId,
        string? teamLeadAgentId,
        string? notes = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary (3-R Standard)");
        sb.Append("**Result:** ").Append(Trim(summary.Result)).AppendLine();
        sb.Append("**Reasoning:** ").Append(Trim(summary.Reasoning)).AppendLine();
        sb.Append("**Risk:** ").Append(Trim(summary.Risk)).AppendLine();

        if (!string.IsNullOrWhiteSpace(notes))
        {
            sb.AppendLine();
            sb.AppendLine(notes.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("---");

        var sender = string.IsNullOrWhiteSpace(senderAgentId)
            ? "AgentMesh"
            : $"AgentMesh Persona @{senderAgentId.Trim().TrimStart('@')}";
        var lead = string.IsNullOrWhiteSpace(teamLeadAgentId)
            ? "the assigned reviewer"
            : $"@{teamLeadAgentId.Trim().TrimStart('@')}";
        sb.Append("*Automated pull request submitted by ").Append(sender)
          .Append(". Awaiting review from ").Append(lead).Append(".*");

        return sb.ToString();
    }

    private static string Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "_Not provided._" : value.Trim().ReplaceLineEndings(" ").Trim();

    public async Task<GitHubPullRequestResult> PushBranchAndCreatePrAsync(
        GitHubPullRequestRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Failure(request.HeadBranch, "invalid_title", "A pull request title is required.");
        }

        if (string.IsNullOrWhiteSpace(request.HeadBranch))
        {
            return Failure(null, "invalid_branch", "No branch to push: 'branch_name' was empty and no job branch could be resolved.");
        }

        var headBranch = request.HeadBranch.Trim();
        var baseBranch = string.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : request.BaseBranch.Trim();

        if (string.Equals(headBranch, baseBranch, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(headBranch, "branch_equals_base",
                $"'{headBranch}' is both the head and the base branch. A pull request needs two different branches.");
        }

        var options = ReadOptions();
        var token = await ResolveTokenAsync(options, ct);

        var (owner, repo) = ResolveRepository(request, options);

        // No credentials and no target repository: this is the local-Aspire path. Say so plainly
        // and do not touch git — a developer's working copy is not a job workspace, and pushing it
        // would be a destructive surprise in exchange for a PR nobody asked GitHub for.
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning(
                "open_pull_request running WITHOUT credentials: simulated PR #{PrNumber} for '{Branch}'. " +
                "Set GitHub:Token (or a persona GitHub token) to open a real pull request.",
                SimulatedPrNumber, headBranch);

            return new GitHubPullRequestResult(
                Success: true,
                PrNumber: SimulatedPrNumber,
                PrUrl: $"https://github.com/{owner ?? "your-org"}/{repo ?? "your-repo"}/pull/{SimulatedPrNumber}",
                HeadBranch: headBranch,
                BaseBranch: baseBranch,
                Message: $"Simulated PR #{SimulatedPrNumber} (no GitHub token configured, so nothing was pushed " +
                         "and no review was requested). Configure GitHub:Token to open a real pull request.",
                Simulated: true);
        }

        if (owner is null || repo is null)
        {
            return Failure(headBranch, "repository_unresolved",
                "Could not determine the target repository. Pass it through the job's git remote, or set " +
                "GitHub:Repository in configuration as 'owner/repo'.");
        }

        var pushResult = await PushBranchAsync(request, headBranch, token!, ct);
        if (!pushResult.Success)
        {
            return Failure(headBranch, pushResult.Reason ?? "push_failed", pushResult.Message ?? "git push failed.");
        }

        var body = FormatPrBody(request.Summary, request.SenderAgentId, request.TeamLeadAgentId);

        var pr = await CreatePullRequestAsync(owner!, repo!, request.Title.Trim(), headBranch, baseBranch, body, token!, ct);
        if (pr is null)
        {
            return new GitHubPullRequestResult(
                Success: false,
                PrNumber: null,
                PrUrl: null,
                HeadBranch: headBranch,
                BaseBranch: baseBranch,
                Message: $"'{headBranch}' was pushed to {owner}/{repo}, but the pull request could not be created. " +
                         "Check the API logs for the GitHub response body.",
                FailureReason: "pr_create_failed");
        }

        var (prNumber, prUrl) = pr.Value;

        var reviewNote = await RequestReviewersAsync(owner!, repo!, prNumber, request.ReviewerLogin, token!, ct);

        var message = $"PR #{prNumber} opened: {prUrl}";
        if (!string.IsNullOrWhiteSpace(request.ReviewerLogin))
        {
            message += reviewNote
                ? $". Assigned to @{request.ReviewerLogin} for review."
                : $" — but the review request for @{request.ReviewerLogin} failed; assign it manually.";
        }

        return new GitHubPullRequestResult(
            Success: true,
            PrNumber: prNumber,
            PrUrl: prUrl,
            HeadBranch: headBranch,
            BaseBranch: baseBranch,
            Message: message);
    }

    /// <summary>
    /// Read configuration on every call rather than once at construction.
    /// </summary>
    /// <remarks>
    /// The service is registered scoped and the API host reloads <c>appsettings*.json</c>, so a
    /// snapshot taken at startup would pin whatever was in the file before a developer added a
    /// token. Reading per call also keeps the test seams (a stub <see cref="IConfiguration"/> per
    /// instance) honest.
    /// <para>
    /// <strong>No manual environment-variable fallback.</strong> The host's default configuration
    /// already layers environment variables on top of the JSON files, so <c>GitHub__Token</c> works
    /// without any code here. Adding a hand-rolled <c>GITHUB_TOKEN</c> lookup would not only
    /// duplicate the framework, it would make this class's behaviour depend on whatever happens to
    /// be exported in the test runner's process — a unit test for the no-credentials path would
    /// silently take the authenticated path on any machine where the author had run
    /// <c>gh auth login</c>.
    /// </para>
    /// </remarks>
    public GitHubPullRequestOptions ReadOptions() => new(
        Token: FirstNonEmpty(configuration["GitHub:Token"]),
        AppId: FirstNonEmpty(configuration["GitHub:AppId"]),
        AppPrivateKey: NormalizePrivateKey(configuration["GitHub:AppPrivateKey"]),
        AppInstallationId: FirstNonEmpty(configuration["GitHub:AppInstallationId"]),
        Repository: FirstNonEmpty(configuration["GitHub:Repository"], configuration["GitHub:Repo"]),
        ApiBaseUrl: FirstNonEmpty(configuration["GitHub:ApiBaseUrl"]) ?? "https://api.github.com");

    /// <summary>
    /// Turn a configured private key into PEM the crypto API will accept.
    /// </summary>
    /// <remarks>
    /// <c>ImportFromPem</c> requires the literal <c>-----BEGIN RSA PRIVATE KEY-----</c> armor and real
    /// newlines inside the base64 body. A key arriving through
    /// <c>GitHub__AppPrivateKey=-----BEGIN...\\nMIIB...\\n-----END-----</c> from a shell or a Helm
    /// values file has neither: the armor survives but the newlines are two-character escapes.
    /// Unescaping them is what makes the same setting work from a file, an env var, and a K8s secret
    /// without asking the operator to reformat it. A value with no armor at all is treated as bare
    /// base64 and re-armored, which is the shape <c>openssl base64 -w 0</c> produces.
    /// </remarks>
    internal static string? NormalizePrivateKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim().Trim('"');

        // Only unescape when the text still looks single-line. A PEM read from a file already has
        // real newlines, and rewriting "\n" there would corrupt nothing but is unnecessary work.
        if (!text.Contains('\n') && text.Contains("\\n", StringComparison.Ordinal))
        {
            text = text.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n");
        }

        if (text.Contains("-----BEGIN", StringComparison.Ordinal)) return text;

        // Bare base64: re-armour it as a PKCS#1 key, which is what GitHub hands out on download.
        var body = text.Replace("\n", string.Empty).Replace("\r", string.Empty).Replace(" ", string.Empty);
        if (body.Length == 0) return null;

        var lines = body.Chunk(64).Select(chunk => new string(chunk));
        return "-----BEGIN RSA PRIVATE KEY-----\n" + string.Join("\n", lines) + "\n-----END RSA PRIVATE KEY-----";
    }

    /// <summary>
    /// The credential to push and open the PR with.
    /// </summary>
    /// <remarks>
    /// A PAT wins outright. Otherwise, if the three App settings are present, an installation token
    /// is minted. Anything less returns null and the caller takes the simulated path.
    /// <para>
    /// Partial App configuration is reported as a refusal rather than silently downgraded: an
    /// operator who pasted a private key and forgot the installation id would otherwise get a
    /// "no token configured" simulation and no clue that the key was the reason.
    /// </para>
    /// </remarks>
    protected virtual async Task<string?> ResolveTokenAsync(
        GitHubPullRequestOptions options,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Token)) return options.Token;

        var appSettings = new[] { options.AppId, options.AppPrivateKey, options.AppInstallationId };
        var configured = appSettings.Count(v => !string.IsNullOrWhiteSpace(v));

        if (configured == 0) return null;

        if (configured < appSettings.Length)
        {
            logger.LogWarning(
                "GitHub App credentials are incomplete: {Configured} of {Total} of " +
                "[GitHub:AppId, GitHub:AppPrivateKey, GitHub:AppInstallationId] are set. " +
                "Falling back to the simulated pull request.",
                configured, appSettings.Length);
            return null;
        }

        try
        {
            var jwt = CreateAppJwt(options.AppId!, options.AppPrivateKey!);
            return await MintInstallationTokenAsync(options, jwt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let a credential-shape problem escape as an unhandled exception: the caller is a
            // model mid-turn and needs a payload it can read, not a 500.
            logger.LogError(ex, "GitHub App installation token could not be minted for app {AppId}", options.AppId);
            return null;
        }
    }

    /// <summary>
    /// Sign the 60-second JWT that authenticates a GitHub App to its own API.
    /// </summary>
    /// <remarks>
    /// RS256, three base64url segments, <c>iat</c> wound back a minute against clock skew and
    /// <c>exp</c> ten minutes out — the shape GitHub documents. Implemented directly rather than
    /// through <c>Microsoft.IdentityModel.Tokens</c> because the API project pulls that in for its
    /// own bearer auth and this class library deliberately does not: a JWT library that can also
    /// validate and parse tokens is a much larger attack surface than "sign these three ASCII
    /// segments with this one key".
    /// </remarks>
    internal static string CreateAppJwt(string appId, string privateKeyPem, DateTimeOffset? now = null)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem.AsSpan());

        var issuedAt = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - 60;
        var expiresAt = issuedAt + 600;

        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = issuedAt, exp = expiresAt, iss = appId }));

        var signingInput = $"{header}.{claims}";
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private async Task<string?> MintInstallationTokenAsync(
        GitHubPullRequestOptions options,
        string jwt,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.ApiBaseUrl.TrimEnd('/')}/app/installations/{options.AppInstallationId}/access_tokens");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentMesh", "1.0"));
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("GitHub App token exchange failed: HTTP {Status} {Body}",
                (int)response.StatusCode, Truncate(Redact(Sanitize(body), jwt), 400));
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;

            if (string.IsNullOrWhiteSpace(token))
            {
                logger.LogError("GitHub App token exchange returned no token field");
                return null;
            }

            logger.LogInformation("Minted a GitHub App installation token for app {AppId}", options.AppId);
            return token;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "GitHub App token response was not JSON");
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Work out which repository the PR belongs to.
    /// </summary>
    /// <remarks>
    /// Precedence is caller-explicit, then the job's recorded remote, then configuration. The
    /// remote wins over configuration because a project's repository is per-project truth, while
    /// <c>GitHub:Repository</c> is a host-wide default that would otherwise point every project's
    /// PRs at whichever repo the control plane was configured with.
    /// </remarks>
    private static (string? Owner, string? Repo) ResolveRepository(
        GitHubPullRequestRequest request,
        GitHubPullRequestOptions options)
    {
        if (!string.IsNullOrWhiteSpace(request.RepositoryOwner) && !string.IsNullOrWhiteSpace(request.RepositoryName))
        {
            return (request.RepositoryOwner.Trim(), request.RepositoryName.Trim());
        }

        var fromRemote = ParseOwnerRepo(request.RemoteUrl);
        if (fromRemote is not null) return fromRemote.Value;

        var configured = ParseOwnerRepo(options.Repository);
        if (configured is not null) return configured.Value;

        // An ssh-style remote without a token still tells us the repository; only the URL form is
        // unusable for pushing with credentials.
        return (null, null);
    }

    /// <summary>
    /// Extract <c>owner</c> and <c>repo</c> from any git URL or <c>owner/repo</c> slug.
    /// </summary>
    /// <remarks>
    /// Handles the four shapes that actually occur here: <c>https://host/o/r.git</c>,
    /// <c>https://host/o/r</c>, scp-style <c>git@host:o/r.git</c>, and a bare <c>owner/repo</c>.
    /// Returns null for anything else rather than guessing, because a wrong owner on a PR
    /// creation is a 404 against someone else's repository.
    /// </remarks>
    public static (string Owner, string Repo)? ParseOwnerRepo(string? urlOrSlug)
    {
        if (string.IsNullOrWhiteSpace(urlOrSlug)) return null;

        var value = urlOrSlug.Trim();
        if (value.EndsWith("/", StringComparison.Ordinal)) value = value[..^1];
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];

        // Strip any credentials that were embedded in a previous push URL. Never propagate them.
        var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            var afterScheme = value[(schemeSeparator + 3)..];
            var slash = afterScheme.IndexOf('/');
            if (slash < 0) return null;
            value = afterScheme[(slash + 1)..];
        }
        else
        {
            var colon = value.IndexOf(':');
            if (colon >= 0 && !value.Contains('/')) return null;
            if (colon >= 0) value = value[(colon + 1)..];
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        var owner = segments[^2];
        var repo = segments[^1];
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)) return null;

        return (owner, repo);
    }

    private async Task<(bool Success, string? Reason, string? Message)> PushBranchAsync(
        GitHubPullRequestRequest request,
        string headBranch,
        string token,
        CancellationToken ct)
    {
        var workingDirectory = request.WorkingDirectory;

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return (false, "no_working_directory",
                $"No job workspace found at '{workingDirectory ?? "(not resolved)"}'. The control plane owns no " +
                "checkout; the runner must save its workspace before a PR can be opened from it.");
        }

        if (!Directory.Exists(Path.Combine(workingDirectory, ".git")))
        {
            return (false, "not_a_git_repository",
                $"'{workingDirectory}' is not a git repository, so there is nothing to push.");
        }

        var remoteUrl = string.IsNullOrWhiteSpace(request.RemoteUrl) ? "origin" : request.RemoteUrl.Trim();

        // Only rewrite an https remote. An ssh remote is authenticated by the runner's own key, and
        // pasting a PAT into it would produce a confusing "invalid URL" failure from ssh.
        var pushTarget = remoteUrl.Equals("origin", StringComparison.OrdinalIgnoreCase)
            ? remoteUrl
            : InjectCredentials(remoteUrl, token);

        var push = await _gitRunner(new GitCommandRequest(
            workingDirectory,
            ["push", "--set-upstream", pushTarget, $"{headBranch}:{headBranch}"],
            AuthlessGitEnvironment(),
            DefaultPushTimeoutSeconds), ct);

        if (push.ExitCode != 0)
        {
            var detail = Redact(Sanitize(push.StdErr.Length > 0 ? push.StdErr : push.StdOut), token);
            logger.LogError("git push failed for branch '{Branch}': {Detail}", headBranch, detail);
            return (false, "push_failed",
                $"git push of '{headBranch}' failed (exit {push.ExitCode}): {Truncate(detail, 800)}");
        }

        logger.LogInformation("Pushed branch '{Branch}' from {WorkingDirectory}", headBranch, workingDirectory);
        return (true, null, null);
    }

    /// <summary>
    /// Environment for the push that stops git from asking for input.
    /// </summary>
    /// <remarks>
    /// This is the difference between a tool that returns an error and one that hangs the agent's
    /// turn: with a credential prompt enabled, <c>git</c> waits on a terminal that will never
    /// answer. <c>GIT_TERMINAL_PROMPT=0</c> makes it fail immediately instead.
    /// <c>GCM_INTERACTIVE=never</c> covers Git Credential Manager, which ignores the former on
    /// some builds. The caller's own credential helper is deliberately left in place for ssh remotes.
    /// </remarks>
    private static Dictionary<string, string?> AuthlessGitEnvironment() => new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_ASKPASS"] = "echo",
        ["SSH_ASKPASS"] = "echo",
        ["GIT_FLUSH"] = "1",
    };

    private static string InjectCredentials(string remoteUrl, string token)
    {
        if (!remoteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return remoteUrl;

        // x-access-token is the conventional username for a PAT or installation token; the value is
        // ignored by GitHub but must be non-empty for the URL to parse.
        var withoutScheme = remoteUrl["https://".Length..];
        if (withoutScheme.Contains('@')) return remoteUrl; // already carries credentials

        return $"https://x-access-token:{Uri.EscapeDataString(token)}@{withoutScheme}";
    }

    private async Task<(int Number, string Url)?> CreatePullRequestAsync(
        string owner,
        string repo,
        string title,
        string head,
        string baseBranch,
        string body,
        string token,
        CancellationToken ct)
    {
        var payload = new
        {
            title,
            head,
            @base = baseBranch,
            body,
        };

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/repos/{owner}/{repo}/pulls",
            JsonPayload(payload),
            token,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            await LogFailure("create pull request", owner, repo, response, token, ct);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var number = doc.RootElement.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;

            if (number <= 0)
            {
                logger.LogError("GitHub returned no usable PR number for {Owner}/{Repo}: {Body}",
                    owner, repo, Truncate(Sanitize(json), 400));
                return null;
            }

            return (number, url ?? $"https://github.com/{owner}/{repo}/pull/{number}");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "GitHub PR response was not JSON for {Owner}/{Repo}", owner, repo);
            return null;
        }
    }

    private async Task<bool> RequestReviewersAsync(
        string owner,
        string repo,
        int prNumber,
        string? reviewerLogin,
        string token,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reviewerLogin)) return true; // nothing asked for, nothing failed

        var login = reviewerLogin.Trim().TrimStart('@');

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/repos/{owner}/{repo}/pulls/{prNumber}/requested_reviewers",
            JsonPayload(new { reviewers = new[] { login } }),
            token,
            ct);

        // 201 = newly requested, 204 = already requested (GitHub is idempotent here). Both are fine.
        if (!response.IsSuccessStatusCode)
        {
            await LogFailure("request reviewers", owner, repo, response, token, ct);
            return false;
        }

        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        StringContent content,
        string token,
        CancellationToken ct)
    {
        var options = ReadOptions();
        var client = httpClientFactory.CreateClient(HttpClientName);
        var request = new HttpRequestMessage(method, $"{options.ApiBaseUrl.TrimEnd('/')}{path}")
        {
            Content = content,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentMesh", "1.0"));

        return await client.SendAsync(request, ct);
    }

    private async Task LogFailure(
        string operation,
        string owner,
        string repo,
        HttpResponseMessage response,
        string token,
        CancellationToken ct)
    {
        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // The body is diagnostics; losing it must not replace the original failure.
        }

        logger.LogError("GitHub {Operation} failed for {Owner}/{Repo}: HTTP {Status} {Body}",
            operation, owner, repo, (int)response.StatusCode, Truncate(Redact(Sanitize(body), token), 600));
    }

    private static StringContent JsonPayload(object payload) =>
        new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private static GitHubPullRequestResult Failure(
        string? headBranch,
        string reason,
        string message) => new(
            Success: false,
            PrNumber: null,
            PrUrl: null,
            HeadBranch: headBranch,
            BaseBranch: null,
            Message: message,
            FailureReason: reason);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    /// <summary>
    /// Remove anything that looks like a token from text before it reaches a log or the caller.
    /// </summary>
    /// <remarks>
    /// GitHub echoes the request URL in some error bodies, and git echoes the remote it pushed to.
    /// Both would otherwise put a credential into the session ledger, which is readable by every
    /// persona on the mesh and by the Cockpit.
    /// </remarks>
    internal static string Redact(string? value, string token)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var result = value;

        if (!string.IsNullOrWhiteSpace(token))
        {
            result = result.Replace(token, "[redacted]");
            result = result.Replace(Uri.EscapeDataString(token), "[redacted]");
        }

        // Any user:pass@host pair, in case the failure came from a remote that already carried one.
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"(?<=://)[^\s/@:]+:[^\s@/]+@", "[redacted]@");

        return result;
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\0', ' ').Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// The real <c>git</c> runner. Kept private and static so the only way to exercise it in tests
    /// is through a subclass or the injected <c>gitRunner</c> seam.
    /// </summary>
    /// <remarks>
    /// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a reconstructed
    /// command line, and stdout/stderr are read as events with a hard timeout — the same two
    /// hazards that make a naive <c>Process.Start</c> deadlock on a full pipe buffer.
    /// </remarks>
    private static async Task<GitCommandResult> RunGitAsync(
        GitCommandRequest request,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in request.Environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            if (!process.Start())
            {
                return new GitCommandResult(-1, string.Empty, "Failed to start git.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds > 0 ? request.TimeoutSeconds : DefaultPushTimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new GitCommandResult(-1, stdout.ToString(),
                    $"git {request.Arguments.FirstOrDefault()} timed out after {request.TimeoutSeconds}s.");
            }

            return new GitCommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GitCommandResult(-1, string.Empty, ex.Message);
        }
    }
}

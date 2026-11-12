using System.Text;
using System.Text.RegularExpressions;

namespace AgentMesh.Core.Services;

/// <summary>What the read-only workspace policy decided about one command line.</summary>
public enum CommandDisposition
{
    /// <summary>Every program the command can reach is on the inspection allow-list with no mutating argument.</summary>
    Allow,

    /// <summary>The command can create, edit, delete, build, test, commit or push — do not run it.</summary>
    Refuse,
}

/// <summary>
/// The verdict for a single command line.
/// </summary>
/// <param name="Disposition">Whether the command may run in a read-only workspace.</param>
/// <param name="Reason">
/// For a refusal, the human-readable clause that fired — what the command <em>would do</em>, not which
/// array it hit — written to be handed straight back to a model as tool output so it can pick a
/// different approach. Null for an allow.
/// </param>
/// <param name="MatchedRule">
/// The stable rule id (<c>git-mutation:commit</c>, <c>mutation-denylist:rm</c>,
/// <c>shell-metachar:output-redirection</c>, <c>allow-list:cat</c>, <c>default-deny:deploy.sh</c>).
/// Carried separately from <paramref name="Reason"/> so logs, tests and a future policy screen can
/// group on it without parsing prose.
/// </param>
public sealed record ReadOnlyCommandDecision(
    CommandDisposition Disposition,
    string? Reason = null,
    string? MatchedRule = null)
{
    /// <summary>True when the command is safe to execute in a read-only workspace.</summary>
    public bool IsAllowed => Disposition == CommandDisposition.Allow;

    /// <summary>True when the command must not be executed.</summary>
    public bool IsRefused => Disposition == CommandDisposition.Refuse;
}

/// <summary>
/// The single read-only workspace command policy: which commands may inspect the tree, and which are
/// refused because they would change it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this lives in Core.</strong> Two hosts enforce one guarantee from two places: the CLI
/// runner, which actually launches shells and assembles the tool list, and the API, which refuses
/// platform tool calls for a read-only thread. If each carries its own list they drift, and the drift
/// is silent — the API promises read-only while the runner happily executes <c>rm</c>. This type owns
/// the vocabulary and the decision; the hosts own where to apply it.
/// </para>
/// <para>
/// <strong>The threat model is a cooperative respondent, not an attacker.</strong> A Q&amp;A thread runs
/// the same runner that can run any shell, so this fence stops plausible accidents — a model that
/// answers "here's how you'd fix it" by running the fix, a <c>git checkout</c> that discards someone
/// else's work — rather than a determined escape. Deliberate evasion (a program that writes files from
/// inside its own argument text) is out of scope, and that is exactly why the shells and interpreters
/// (<c>sh</c>, <c>python</c>, <c>awk</c>, …) sit in <see cref="MutationCommands"/> rather than being
/// half-parsed.
/// </para>
/// <para>
/// <strong>Consequence worth stating plainly:</strong> <c>dotnet build</c>, <c>dotnet test</c>,
/// <c>npm install</c> and <c>make</c> are refused, because they write <c>obj/</c>, <c>bin/</c> and
/// <c>node_modules/</c> into the checkout and so are file mutations by this policy's definition. A
/// read-only thread answers from what is already on disk; an operator who wants results from a build
/// opens a job. If that trade-off is wrong for a given deployment, the knob is one entry in
/// <see cref="MutationCommands"/> or <see cref="InspectionCommands"/> — both are public and named for
/// exactly that reason.
/// </para>
/// <para>
/// <strong>Chaining is handled by judging every clause, not by banning operators.</strong> The
/// historical Stage-1 guard matched <c>git commit|push|merge|rebase|cherry-pick</c> against the start of
/// the line only, so <c>git status &amp;&amp; git commit -m x</c> slipped through. Here the line is split
/// on shell control operators outside quotes and every clause is evaluated on its own, so each clause
/// of <c>echo hi &amp;&amp; rm x</c> is seen. Quoting is respected by the splitter, so
/// <c>grep "a;b" f.cs</c> stays one command and its pattern is not mistaken for a new clause.
/// </para>
/// <para>
/// <strong>Evaluation is deny-first, most-specific-first</strong> (see <see cref="Evaluate"/>):
/// git/gh repository mutations, then the mutation deny-list and interpreters, then the substitution and
/// redirection gates, then the inspection allow-list with per-argument vetoes, then default deny.
/// Order changes the message, not the safety: <c>git commit &amp;&amp; rm x</c> cites
/// <c>git commit</c> because that is the more actionable thing for a model to hear. Safety comes from
/// the last step — anything this policy cannot prove is inspection gets refused.
/// </para>
/// </remarks>
public static partial class ReadOnlyCommandPolicy
{
    /// <summary>
    /// Git subcommands that move repository or worktree state, refused wherever they appear in the line.
    /// </summary>
    /// <remarks>
    /// Wider than the old Stage-1 regex on purpose: <c>checkout</c>, <c>switch</c>, <c>restore</c>,
    /// <c>clean</c>, <c>reset</c>, <c>rm</c> and <c>mv</c> rewrite the working tree, so a read-only
    /// respondent could destroy work it was only reading. <c>add</c>/<c>stage</c> mutate the index;
    /// <c>fetch</c>/<c>clone</c>/<c>pull</c> write objects and refs; <c>gc</c> and <c>prune</c> rewrite
    /// the object store. Subcommands that can be either a read or a write (<c>branch</c>, <c>tag</c>,
    /// <c>remote</c>, <c>stash</c>, <c>reflog</c>, <c>bisect</c>, <c>worktree</c>, <c>config</c>) are
    /// <em>not</em> here — they are judged operation-by-operation in
    /// <see cref="EvaluateGitConditionalSegment"/>.
    /// </remarks>
    public static IReadOnlyList<string> MutatingGitSubcommands { get; } =
    [
        "add", "am", "apply", "checkout", "cherry-pick", "clean", "clone", "commit", "fetch", "gc",
        "index-pack", "merge", "mv", "prune", "pull", "push", "rebase", "reset", "restore", "rm",
        "stage", "switch",
    ];

    /// <summary>
    /// Git subcommands that only read repository state — the inspection half of <c>git</c>. Entries
    /// with an asterisk in their doc-comment are conditional: bare or listing forms are allowed,
    /// mutating operands are refused by <see cref="EvaluateGitConditionalSegment"/>.
    /// </summary>
    public static IReadOnlyList<string> InspectionGitSubcommands { get; } =
    [
        "blame", "cat-file", "count-objects", "describe", "diff", "for-each-ref", "grep", "log",
        "ls-files", "ls-tree", "ls-remote", "merge-base", "rev-list", "rev-parse", "shortlog",
        "show", "show-ref", "status", "whatchanged",
        // Conditional reads (see EvaluateGitConditionalSegment):
        "bisect", "branch", "config", "reflog", "remote", "stash", "tag", "worktree",
    ];

    /// <summary>
    /// <c>gh</c> noun-verb pairs that mutate GitHub state (or the local worktree, for
    /// <c>gh pr checkout</c>). <c>gh api</c> is included because it can issue arbitrary writes.
    /// </summary>
    public static IReadOnlyList<string> MutatingGhSubcommands { get; } =
    [
        "api", "auth login", "auth refresh", "codespace create", "codespace delete", "codespace edit",
        "codespace stop", "gpg-key add", "issue close", "issue comment", "issue create", "issue delete", "issue develop",
        "issue edit", "issue lock", "issue pin", "issue transfer", "issue unlock", "issue unpin",
        "pr checkout", "pr close", "pr comment", "pr create", "pr edit", "pr lock", "pr merge",
        "pr ready", "pr reopen", "pr review", "pr unlock", "release create", "release delete",
        "release edit", "release upload", "repo archive", "repo create", "repo delete", "repo edit",
        "repo rename", "repo set-default", "repo sync", "secret delete", "secret set", "variable delete",
        "variable set", "workflow disable", "workflow enable", "workflow run",
    ];

    /// <summary>The inspection half of <c>gh</c>: views, lists, statuses and diffs.</summary>
    public static IReadOnlyList<string> InspectionGhSubcommands { get; } =
    [
        "alias list", "auth status", "browse", "codespace list", "codespace view",
        "gist list", "gist view", "issue list", "issue status", "issue view", "label list",
        "pr checks", "pr diff", "pr list", "pr status", "pr view", "release list", "release view",
        "repo list", "repo view", "run list", "run view", "run watch", "search code",
        "search issues", "search prs", "search repos", "status", "variable list", "workflow list",
        "workflow view",
    ];

    /// <summary>
    /// Programs refused because their normal use changes disk, build output, processes, remote state
    /// or the host — plus the shells and interpreters, which can reach a write without naming one in a
    /// way any argument-level check could see.
    /// </summary>
    /// <remarks>
    /// Public, discrete and one-entry-per-line so a flip is visible in a diff. Removing an entry moves
    /// the command from "refused with a named reason" to "default deny", never to "allowed" — editing
    /// this array cannot accidentally turn it into an allow-list.
    /// </remarks>
    public static IReadOnlyList<string> MutationCommands { get; } =
    [
        // File and directory mutation.
        "chmod", "chown", "cp", "dd", "install", "link", "ln", "mkdir", "mktemp", "mv", "patch",
        "rm", "rmdir", "rsync", "shred", "tee", "touch", "truncate", "unlink", "wget",
        // Archives: both directions touch the tree.
        "ar", "cpio", "gzip", "bzip2", "xz", "zstd", "tar", "unzip", "zip", "7z", "7za", "7zr",
        // Build, test and restore drivers: these write obj/, bin/, target/, node_modules/ into the tree.
        "dotnet", "msbuild", "xbuild", "ms", "nuget", "make", "cmake", "ninja", "autoconf",
        "automake", "configure", "meson", "cargo", "rustc", "go", "gofmt", "golangci-lint",
        "javac", "jar", "mvn", "gradle", "sbt", "mix", "rebar", "lein", "webpack", "rollup",
        "npm", "npx", "yarn", "pnpm", "bun", "bunx", "deno", "node", "vite",
        "pip", "pip2", "pip3", "poetry", "uv", "conda", "gem", "bundle", "composer",
        // Package managers and orchestrators that reach the host or the network.
        "apt", "apt-get", "yum", "dnf", "pacman", "zypper", "brew", "snap", "choco",
        "docker", "podman", "kubectl", "helm", "terraform", "ansible", "ansible-playbook", "vagrant",
        // Version control systems other than git: a read-only thread's source of truth is git.
        "svn", "hg", "cvs", "fossil", "jj",
        // Shells and script interpreters: the rest of the line is *data* to them, so a nested write is
        // invisible to this policy. Refused outright rather than partially trusted.
        "sh", "bash", "zsh", "dash", "ksh", "fish", "csh", "tcsh", "pwsh", "powershell", "cmd",
        "cscript", "wscript", "busybox",
        "python", "python2", "python3", "ipython", "perl", "ruby", "php", "lua", "r", "Rscript",
        "awk", "gawk", "mawk", "xargs", "parallel", "expect", "tclsh",
        "sqlplus", "psql", "mysql", "sqlite3", "redis-cli", "mongosh",
        "ssh", "scp", "sftp", "ftp", "telnet", "nc", "ncat", "socat",
        // Editors and pagers with shell escapes (vim's `:w`/`:!`, less's `!`).
        "vi", "vim", "nvim", "nano", "pico", "jed", "ed", "ex", "emacs", "micro", "helix", "kak",
        "less", "more", "most", "pg",
        // Process, job and system control.
        "at", "batch", "crontab", "kill", "killall", "pkill", "systemctl", "service", "shutdown",
        "reboot", "mount", "umount", "chroot", "nsenter", "unshare", "setpriv",
    ];

    /// <summary>
    /// Programs allowed because reading and printing is all they can do to the tree.
    /// </summary>
    /// <remarks>
    /// Being listed here is necessary, not sufficient: the argument-level vetoes in
    /// <see cref="MutationFlags"/> still apply, because <c>find -delete</c>, <c>sed -i</c> and
    /// <c>sort -o out</c> write despite the binary looking inspection-shaped. Anything in neither list
    /// ends at default deny.
    /// </remarks>
    public static IReadOnlyList<string> InspectionCommands { get; } =
    [
        // Orientation: where am I, what is here, what is this machine.
        "pwd", "ls", "dir", "vdir", "exa", "eza", "tree", "fd", "fdfind", "find", "stat",
        "file", "du", "df", "realpath", "readlink", "basename", "dirname", "which", "whereis",
        "apropos", "type", "uname", "whoami", "id", "groups", "hostname", "date", "cal", "uptime",
        // `cd` moves the directory of a shell that dies with the command; it changes nothing on disk.
        "cd",
        // `env`/`printenv` deliberately absent: on mesh driver hosts the environment carries
        // injected credentials (AGENTMESH_TOKEN, Auth__StaticApiKey), and a Q&A respondent dumping
        // them into a thread is an exfiltration path no read-only flag asked for.
        "ps", "top", "htop", "free", "lscpu", "lsblk", "echo", "printf", "true",
        "false", "test", "[", "seq", "yes",
        // Read file contents.
        "cat", "tac", "head", "tail", "nl", "od", "hexdump", "xxd", "strings", "wc", "md5sum",
        "sha1sum", "sha256sum", "sha512sum", "cksum", "sum", "base64", "base32", "iconv", "column",
        "jq", "yq", "numfmt", "units", "fold", "fmt", "expand", "unexpand", "col",
        // Search, compare and reshape a stream without naming an output file.
        "grep", "egrep", "fgrep", "rg", "ag", "ack", "sift", "diff", "colordiff", "diff3", "sdiff",
        "cmp", "comm", "join", "paste", "cut", "tr", "uniq", "sort", "rev",
        "tsort", "ptx", "sed",
        // Syntax printers (the pager-shaped readers like `less` are refused instead).
        "bat", "batcat", "pygmentize", "chroma",
        // Source counters that only report.
        "tokei", "cloc", "scc",
        // Read-only remote queries. curl is allowed for plain GETs; see MutationFlags for the
        // write-shaped flags (-o, -T, -X, -d, -F) that turn it into a downloader or a poster.
        "curl",
        // Network introspection that cannot write to the tree. (`ip`/`arp`/`ifconfig` are deliberately
        // absent: they can also reconfigure the host, and the read-shaped uses are covered by ss/netstat.)
        "ping", "ping6", "traceroute", "tracepath", "dig", "nslookup", "host", "getent", "ss", "netstat",
        // Crypto/encoding utilities; the file-writing flags are vetoed below.
        "openssl",
    ];

    /// <summary>
    /// Arguments that turn an allow-listed binary into a writer, matched as whole flag names (an
    /// <c>=value</c> suffix is stripped before matching). Consulted only for binaries in
    /// <see cref="InspectionCommands"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> MutationFlags { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // find's action operators are how `find` mutates the tree. (`-ls` only prints, so it stays out.)
            ["find"] = ["-delete", "-exec", "-execdir", "-ok", "-okdir", "-fls", "-fprint", "-fprint0", "-fprintf"],
            ["fd"] = ["--exec"],
            ["fdfind"] = ["--exec"],
            // In-place editing is the whole reason `sed` looks dangerous.
            ["sed"] = ["-i", "--in-place"],
            ["sort"] = ["--output"],
            ["jq"] = ["--output-file"],
            // `rg --pre` runs an arbitrary program on every file it reads.
            ["rg"] = ["--pre", "--pre-glob", "--hostname-bin"],
            // `curl` is allowed for GETs; anything that names a local file or sends a body is a write.
            ["curl"] =
            [
                "--output", "--remote-name", "--remote-name-all", "--upload-file", "--request", "-X",
                "--data", "--data-ascii", "--data-binary", "--data-urlencode", "--form", "--form-string",
            ],
            ["openssl"] = ["-out", "-outfile", "-keyout"],
            // `git log --output=<file>` is the one write-shaped flag on an otherwise-read subcommand.
            ["git"] = ["--output"],
        };

    /// <summary>
    /// Short-option letters that turn an allow-listed binary into a writer. Unlike
    /// <see cref="MutationFlags"/> these are also tested <em>inside</em> single-dash clusters
    /// (<c>curl -sSo out</c>, <c>sed -ifoo</c>) — but only for the binaries listed here, so a letter
    /// that is harmless elsewhere (<c>sort -c</c> is "check only") cannot be refused as if it wrote.
    /// </summary>
    /// <remarks>
    /// Values are bare letters, matched case-sensitively: <c>curl -S</c> (show errors) is fine,
    /// <c>curl -sSo out</c> contains the write-flag <c>o</c>. Known limit, cooperative-threat model:
    /// <c>curl --data @file</c> posts a named file's contents without naming a write flag — the
    /// <c>--data</c>/<c>--request</c> refusals already cover most poster shapes; posting the contents
    /// of an existing file is the residue (it writes nothing locally).
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<char>> MutationClusterFlags { get; } =
        new Dictionary<string, IReadOnlyList<char>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sed"] = ['i'], // -i, -i.bak, -ifile: the suffix is the backup extension
            ["sort"] = ['o'],
            ["jq"] = ['o'],
            ["fd"] = ['X'],
            ["fdfind"] = ['X'],
            // Every curl cluster letter that ends in a write: -o out, -O remote-name, -T upload,
            // -d/-D body/dump-headers to file, -F form, -C resumed transfer, -X method override.
            ["curl"] = ['o', 'O', 'T', 'd', 'D', 'F', 'C', 'X'],
        };

    /// <summary>
    /// Judges one command line against the read-only workspace policy.
    /// </summary>
    /// <param name="rawCommand">The exact shell text a host was about to execute.</param>
    /// <returns>
    /// <see cref="CommandDisposition.Allow"/> only when every clause names an inspection program with
    /// no mutating argument and no clause smuggles a program or file this policy cannot see; otherwise
    /// <see cref="CommandDisposition.Refuse"/> with the reason and stable rule id of the most specific
    /// clause that fired.
    /// </returns>
    public static ReadOnlyCommandDecision Evaluate(string? rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return Refuse("no command was given", "empty-command");
        }

        var scan = Scan(rawCommand);

        // Pass 1 — repository-state mutations, in every clause of the line. First so a chained clause
        // cannot outrun this check the way it outran the old start-of-line-only regex, and so the cited
        // reason is the one a model can act on.
        foreach (var segment in scan.Segments)
        {
            if (EvaluateRepositorySegment(segment) is { } repoRefusal)
            {
                return repoRefusal;
            }
        }

        // Pass 2 — the mutation deny-list and interpreters, across every clause.
        foreach (var segment in scan.Segments)
        {
            if (ResolveLeader(segment) is { } leader && MutationCommandsLookup.Contains(leader))
            {
                return Refuse(
                    $"'{leader}' changes files, build output, processes or remote state, which a read-only workspace forbids",
                    $"mutation-denylist:{leader}");
            }
        }

        // Pass 3 — the gates on constructs that reach a program or file this policy cannot inspect.
        if (scan.HasCommandSubstitution)
        {
            return Refuse(
                "command substitution ($() or backticks) runs a program this policy cannot see, so nothing downstream is provably read-only",
                "shell-metachar:command-substitution");
        }

        if (scan.HasProcessSubstitution)
        {
            return Refuse(
                "process substitution (<( ) or >( )) wires a program to a pipe this policy cannot see",
                "shell-metachar:process-substitution");
        }

        if (scan.HasInputRedirection)
        {
            return Refuse(
                "input redirection ('<' or '<<<') names a file implicitly; pass the path as an argument so the command stays explicit",
                "shell-metachar:input-redirection");
        }

        if (scan.HasOutputRedirection)
        {
            return Refuse(
                "output redirection ('>' or '>>') creates or overwrites a file on disk; answer from what is already there instead of writing one",
                "shell-metachar:output-redirection");
        }

        // Pass 4 — the allow-list with per-argument vetoes; default deny for anything left over.
        foreach (var segment in scan.Segments)
        {
            var decision = EvaluateAllowedSegment(segment);
            if (decision.IsRefused)
            {
                return decision;
            }
        }

        return new ReadOnlyCommandDecision(CommandDisposition.Allow, MatchedRule: "inspection-allow-list");
    }

    /// <summary>
    /// Formats <paramref name="decision"/> as the refusal sentence a host hands back as tool output.
    /// </summary>
    /// <remarks>
    /// One shape for both hosts, and the rule id is quoted so an operator can find the array to flip.
    /// The "do not retry in another form" clause matters: without it a model treats a refusal as a
    /// transient tool error and tries four variations of the same write.
    /// </remarks>
    public static string RefusalMessage(string? rawCommand, ReadOnlyCommandDecision decision)
    {
        if (decision.IsAllowed)
        {
            throw new ArgumentException("An allowed decision has no refusal message.", nameof(decision));
        }

        var command = string.IsNullOrWhiteSpace(rawCommand) ? "(empty)" : rawCommand.Trim();
        return
            $"Error: refused by the read-only workspace policy [{decision.MatchedRule}] — {decision.Reason}. " +
            $"The command was: {command}\n" +
            $"This thread's workspace is read-only ('{WorkspaceAccessKinds.ReadOnly}' in its session metadata): " +
            "inspect with the allowed read tools and commands (read_file, cat, head, tail, grep, rg, find, ls, " +
            "tree, git status/log/diff/show/blame) and answer from what is already on disk. Do not retry this " +
            "command in another form or under another tool name. If a real answer needs a file to change, a " +
            "build to run, or a commit to be made, say so and ask the operator to open a job for it.";
    }

    private static ReadOnlyCommandDecision? EvaluateRepositorySegment(CommandSegment segment)
    {
        var index = LeaderIndex(segment);
        if (index < 0)
        {
            return null;
        }

        var leader = BaseName(segment.Tokens[index]);
        if (leader.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            var sub = ResolveGitSubcommand(segment, index, out var operands);
            if (sub is null)
            {
                return Refuse(
                    "'git' was invoked without a subcommand, so what it would change is unknown",
                    "git-mutation:unresolved");
            }

            if (MutatingGitLookup.Contains(sub))
            {
                return Refuse(
                    $"'git {sub}' changes the repository index, working tree, refs or remote",
                    $"git-mutation:{sub.ToLowerInvariant()}");
            }

            return EvaluateGitConditionalSegment(sub, operands);
        }

        if (leader.Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            var ghKey = ResolveGhKey(segment, index);
            if (ghKey is null)
            {
                return Refuse(
                    "'gh' was invoked without enough arguments to tell a read from a write",
                    "gh-mutation:unresolved");
            }

            return MutatingGhLookup.Contains(ghKey)
                ? Refuse(
                    $"'gh {ghKey}' creates or changes GitHub state",
                    $"gh-mutation:{ghKey.ToLowerInvariant().Replace(' ', '-')}")
                : null;
        }

        return null;
    }

    /// <summary>
    /// The conditional git subcommands: listing forms are inspection, mutating operands are not.
    /// </summary>
    /// <remarks>
    /// Each arm refuses the smallest honest clause: <c>git branch feature-x</c> cites creation, not
    /// "branch is unknown". Everything fails closed except where a flag is provably listing-only —
    /// an unrecognised flag on <c>branch</c>/<c>tag</c> is treated as formatting and allowed, while
    /// <c>git config</c> and the verb-taking subcommands (<c>stash</c>, <c>remote</c>,
    /// <c>reflog</c>, <c>bisect</c>, <c>worktree</c>) refuse the unknown shape instead.
    /// <paramref name="operands"/> is the token list after the subcommand, with flags kept and the
    /// values of known value-taking flags already removed.
    /// </remarks>
    private static ReadOnlyCommandDecision? EvaluateGitConditionalSegment(string sub, IReadOnlyList<string> operands)
    {
        switch (sub.ToLowerInvariant())
        {
            // `git branch`/`git tag` list when every operand is a listing flag; a name operand creates
            // a ref, and -d/-m/-c/-u rewrite refs. See EvaluateNamedRefListing for the flag table.
            case "branch":
                return EvaluateNamedRefListing(operands, "branch", "a branch");

            case "tag":
                return EvaluateNamedRefListing(operands, "tag", "a tag");

            // `git remote`, `git remote show X`, `git remote get-url X` read; add/set-url/remove
            // mutate .git/config.
            case "remote":
            {
                var verb = FirstOperand(operands);
                if (verb is null ||
                    verb.Equals("show", StringComparison.OrdinalIgnoreCase) ||
                    verb.Equals("get-url", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return Refuse(
                    $"'git remote {verb}' changes the repository's remote configuration",
                    $"git-mutation:remote-{verb.ToLowerInvariant()}");
            }

            // `git stash list` / `show` read the stash; every other form (including bare `git stash`)
            // pushes or pops the working tree.
            case "stash":
            {
                var verb = FirstOperand(operands);
                return verb is null
                    ? Refuse(
                        "'git stash' with no verb pushes the working tree onto the stash",
                        "git-mutation:stash-push")
                    : verb.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                      verb.Equals("show", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : Refuse(
                            $"'git stash {verb}' changes the working tree",
                            $"git-mutation:stash-{verb.ToLowerInvariant()}");
            }

            // `git reflog` / `git reflog show X` read; expire/delete prune history.
            case "reflog":
            {
                var verb = FirstOperand(operands);
                return verb is null || verb.Equals("show", StringComparison.OrdinalIgnoreCase) ||
                       verb.Equals("exists", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : Refuse(
                        $"'git reflog {verb}' rewrites reflog history",
                        $"git-mutation:reflog-{verb.ToLowerInvariant()}");
            }

            // `git bisect log` / `view` read the bisect state; start/reset/good/bad drive it.
            case "bisect":
            {
                var verb = FirstOperand(operands);
                return verb is null ||
                       verb.Equals("log", StringComparison.OrdinalIgnoreCase) ||
                       verb.Equals("view", StringComparison.OrdinalIgnoreCase) ||
                       verb.Equals("visualize", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : Refuse(
                        "'git bisect' (with anything other than 'log'/'view') drives or rewrites bisect state in .git",
                        "git-mutation:bisect");
            }

            // `git worktree list` reads; add/remove/prune/move change the worktree set.
            case "worktree":
            {
                var verb = FirstOperand(operands);
                return verb is not null && verb.Equals("list", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : Refuse(
                        "'git worktree' (without 'list') creates or removes working directories",
                        "git-mutation:worktree");
            }

            // `git config key` / `git config --get key` read; two non-flag operands (key + value) or a
            // write-shaped flag mutate .git/config.
            case "config":
            {
                var nonFlags = operands.Count(o => !o.StartsWith("-", StringComparison.Ordinal));
                if (nonFlags >= 2)
                {
                    return Refuse(
                        "'git config key value' writes the repository configuration file",
                        "git-mutation:config-set");
                }

                foreach (var token in operands)
                {
                    var flag = token.TrimStart('-').Split('=')[0];
                    if (flag is "unset" or "unset-all" or "add" or "replace-all" or "edit"
                        or "rename-section" or "remove-section" or "import")
                    {
                        return Refuse(
                            $"'git config --{flag}' writes the repository configuration file",
                            $"git-mutation:config-{flag.ToLowerInvariant()}");
                    }
                }

                return null;
            }

            default:
                return null;
        }
    }

    private static string? FirstOperand(IReadOnlyList<string> operands) =>
        operands.FirstOrDefault(o => !o.StartsWith("-", StringComparison.Ordinal));

    /// <summary>
    /// Shared rule for the named-ref listing subcommands (<c>git branch</c>, <c>git tag</c>): allowed
    /// when every operand is a provably listing-only flag, refused on any name operand or ref-rewriting
    /// flag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unknown flags are treated as formatting and allowed — the same way <c>git log --pretty=%H</c> is
    /// allowed — because the flag space of these two subcommands is mostly display options
    /// (<c>-v</c>, <c>-a</c>, <c>--sort</c>, <c>--color</c>, <c>--contains</c>, …) and refusing them
    /// would break ordinary inspection for no safety gain. The write-shaped flags are individually
    /// named in <see cref="RefRewritingFlags"/>, and <c>--format=…</c> additionally gets the shell
    /// execution vetoes (<c>%(!</c>) checked, because <c>git tag --format='%(contents)%(!cmd.sh)i'</c>
    /// is a real, documented file-writer.
    /// </para>
    /// <para>
    /// A <c>--list</c> flag makes trailing name operands patterns rather than a new ref, so
    /// <c>git tag --list "v1*"</c> passes while <c>git tag v1.0</c> creates.
    /// </para>
    /// </remarks>
    private static ReadOnlyCommandDecision? EvaluateNamedRefListing(IReadOnlyList<string> operands, string sub, string refNoun)
    {
        var rejections = sub == "tag" ? TagRewritingFlags : BranchRewritingFlags;
        var explicitList = false;
        var names = 0;

        foreach (var token in operands)
        {
            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                names++;
                continue;
            }

            var flag = token.TrimStart('-').Split('=')[0];
            if (flag is "l" or "list")
            {
                explicitList = true;
                continue;
            }

            // Single-dash tokens may be clusters (`-dD`, `-am`): test the whole name, then each letter.
            if (rejections.Contains(flag) ||
                (token.StartsWith("-", StringComparison.Ordinal) && !token.StartsWith("--", StringComparison.Ordinal) &&
                 token[1..].Any(ch => rejections.Contains(ch.ToString()))))
            {
                return Refuse(
                    $"'git {sub} {token}' creates, rewrites or deletes {refNoun} refs, which changes the repository",
                    $"git-mutation:{sub}-{flag.ToLowerInvariant()}");
            }

            // `--format` / `--sort` / `--contains` etc.: display and filter plumbing, allowed unless the
            // format string asks git to run something.
            var eq = token.IndexOf('=');
            if (eq > 0 && flag.Equals("format", StringComparison.OrdinalIgnoreCase))
            {
                var value = token[(eq + 1)..];
                if (value.Contains("%(!", StringComparison.Ordinal) ||
                    value.Contains("%(", StringComparison.Ordinal) && value.Contains('!', StringComparison.Ordinal))
                {
                    return Refuse(
                        $"\"{token}\" asks git to run an external command from inside a format string",
                        $"git-mutation:{sub}-format-interpreter");
                }
            }
        }

        if (names > 0 && !explicitList)
        {
            return Refuse(
                $"'git {sub}' with a name operand creates {refNoun}, which changes the repository",
                $"git-mutation:{sub}-create");
        }

        return null;
    }

    /// <summary>
    /// <c>git branch</c> flags that write refs: delete (-d/-D), move/rename (-m/-M), copy (-c/-C),
    /// force-create or reset a branch to a commit (-f), set or clear upstream (-u/--set-upstream-to),
    /// edit the branch description (-e). Listing flags (-a, -r, -v, --merged, --contains, …) are
    /// deliberately absent: they are how ordinary inspection is spelled.
    /// </summary>
    private static readonly HashSet<string> BranchRewritingFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "d", "D", "m", "M", "c", "C", "f", "F", "u", "U", "e",
        "delete", "move", "copy", "force", "edit", "set-upstream-to", "unset-upstream",
    };

    /// <summary>
    /// <c>git tag</c> flags that create or delete tags: -d deletes, -f overwrites, -a/-s/-u and the
    /// message flags (-m/-F/--file/--message) all create. Note the asymmetry the shared-set approach
    /// would have hidden: <c>-a</c> lists branches but <em>annotates (creates) a tag</em>.
    /// </summary>
    private static readonly HashSet<string> TagRewritingFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        // -m and -F carry the annotation message: `git tag -m msg v1` creates. -n only *lists* with
        // annotations, so it stays out.
        "d", "f", "F", "a", "m", "s", "u", "file",
        "delete", "force", "annotate", "message", "sign", "local-user", "cleanup", "author-date",
    };

    private static ReadOnlyCommandDecision EvaluateAllowedSegment(CommandSegment segment)
    {
        var index = LeaderIndex(segment);
        if (index < 0)
        {
            return Refuse(
                "no program name was found in this clause of the command, so this policy cannot prove it only reads",
                "default-deny");
        }

        var leader = BaseName(segment.Tokens[index]);

        if (leader.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            var sub = ResolveGitSubcommand(segment, index, out _);
            if (sub is null || !InspectionGitLookup.Contains(sub))
            {
                return Refuse(
                    $"'git {sub}' is not on the read-only inspection allow-list, so this policy cannot prove it only reads",
                    $"default-deny:git-{sub ?? "unresolved"}");
            }

            return EvaluateArgumentVeto(segment, index, leader)
                   ?? new ReadOnlyCommandDecision(CommandDisposition.Allow, MatchedRule: $"allow-list:git-{sub}");
        }

        if (leader.Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            var ghKey = ResolveGhKey(segment, index);
            return ghKey is not null && InspectionGhLookup.Contains(ghKey)
                ? new ReadOnlyCommandDecision(CommandDisposition.Allow, MatchedRule: $"allow-list:gh-{ghKey.ToLowerInvariant().Replace(' ', '-')}")
                : Refuse(
                    $"'gh {ghKey}' is not on the read-only inspection allow-list, so this policy cannot prove it only reads",
                    $"default-deny:gh-{ghKey?.ToLowerInvariant().Replace(' ', '-') ?? "unresolved"}");
        }

        if (!InspectionCommandsLookup.Contains(leader))
        {
            return Refuse(
                $"'{leader}' is not on the read-only inspection allow-list, so this policy cannot prove it only reads",
                $"default-deny:{leader}");
        }

        if (EvaluateArgumentVeto(segment, index, leader) is { } veto)
        {
            return veto;
        }

        return new ReadOnlyCommandDecision(CommandDisposition.Allow, MatchedRule: $"allow-list:{leader}");
    }

    /// <summary>
    /// The argument-level veto for an allow-listed binary, or null when its arguments stay read-only.
    /// </summary>
    /// <remarks>
    /// Catches a mutation visible in the argument text: exact flags, <c>--flag=value</c>, and short
    /// clusters (<c>sed -ifoo</c>, <c>curl -sSo out</c>) whose letters include a single-character
    /// mutation flag. A program that opens files from inside its own program text is out of scope —
    /// that is the interpreter-shaped-into-the-deny-list boundary described in the class remarks.
    /// </remarks>
    private static ReadOnlyCommandDecision? EvaluateArgumentVeto(CommandSegment segment, int leaderIndex, string leader)
    {
        var hasExact = MutationFlags.TryGetValue(leader, out var flags);
        var hasCluster = MutationClusterFlags.TryGetValue(leader, out var clusterLetters);
        if (!hasExact && !hasCluster)
        {
            return null;
        }

        foreach (var token in segment.Tokens.Skip(leaderIndex + 1))
        {
            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            var stripped = token.TrimStart('-');
            var name = stripped.Contains('=') ? stripped[..stripped.IndexOf('=')] : stripped;

            if (hasExact)
            {
                foreach (var flag in flags!)
                {
                    var bare = flag.TrimStart('-');
                    if (string.Equals(name, bare, StringComparison.OrdinalIgnoreCase))
                    {
                        return Refuse(
                            $"'{leader} {flag}' writes files, runs programs, or sends state, which a read-only workspace forbids even though '{leader}' itself is allowed",
                            $"mutation-flag:{leader}:{bare}");
                    }
                }
            }

            // Single-dash cluster (`-sSo`, `-ifoo`) — and a lone short flag (`-d`, `-o`) is a
            // one-letter cluster. Tested case-sensitively because `-S` and `-s` are different
            // operations on these binaries.
            if (hasCluster && !token.StartsWith("--", StringComparison.Ordinal) && token.Length >= 2)
            {
                foreach (var ch in token[1..])
                {
                    if (clusterLetters!.Contains(ch))
                    {
                        return Refuse(
                            $"'{leader} {token}' contains '{ch}', which writes files, which a read-only workspace forbids even though '{leader}' itself is allowed",
                            $"mutation-flag:{leader}:{ch}");
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the git subcommand and collects the operands that follow it.
    /// </summary>
    /// <remarks>
    /// <c>git -C &lt;path&gt; log</c> and <c>git -c k=v diff</c> are the shapes this exists for: this
    /// codebase drives <c>git</c> through <c>-C</c> constantly, and resolving the subcommand to
    /// <c>/path</c> or <c>k=v</c> would refuse a plain <c>git log</c> for a reason nobody could read.
    /// <paramref name="operands"/> receives every token after the subcommand except global-style flags
    /// and the value token of any <c>--opt &lt;value&gt;</c> pair, which is what the conditional
    /// subcommand rules need to tell <c>git tag v1</c> from <c>git tag --list v1</c>.
    /// </remarks>
    private static string? ResolveGitSubcommand(CommandSegment segment, int leaderIndex, out List<string> operands)
    {
        operands = [];
        string? sub = null;

        for (var i = leaderIndex + 1; i < segment.Tokens.Count; i++)
        {
            var token = segment.Tokens[i];

            if (sub is null)
            {
                if (GitGlobalValueOptions.Contains(token))
                {
                    i++; // the value belongs to the option, not to the subcommand
                    continue;
                }

                if (token.StartsWith("-", StringComparison.Ordinal) || token.Contains('='))
                {
                    continue;
                }

                sub = token;
                continue;
            }

            // Operands for the conditional rules: keep flags (they change list/create semantics, e.g.
            // `git tag -l`) and non-flag words, but drop the value of a value-taking flag so
            // `git branch --contains v1.0` has no operands. A flag written `--flag=value` never
            // consumes the next token.
            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                operands.Add(token);
                if (!token.Contains('=') && GitReadFlagValueOptions.Contains(token))
                {
                    i++;
                }

                continue;
            }

            operands.Add(token);
        }

        return sub;
    }

    /// <summary>Flags on conditional git reads whose following token is a value, not an operand.</summary>
    private static readonly string[] GitReadFlagValueOptions =
    [
        "--contains", "--no-contains", "--merged", "--no-merged", "--format", "--points-at",
        "--sort", "--color", "--ignore-case", "-n", "--verify", "--default",
    ];

    /// <summary>Resolves the <c>gh</c> key as "<c>noun verb</c>", or the bare noun for one-word commands.</summary>
    /// <remarks>
    /// <c>gh api</c> is its own single-word verb: <c>gh api /repos/x/labels -X POST</c> must resolve to
    /// "api", not "api /repos/x/labels", or the deny-list would miss every api call.
    /// </remarks>
    private static string? ResolveGhKey(CommandSegment segment, int leaderIndex)
    {
        var parts = new List<string>(2);
        for (var i = leaderIndex + 1; i < segment.Tokens.Count && parts.Count < 2; i++)
        {
            var token = segment.Tokens[i];
            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (token.Contains('='))
            {
                break;
            }

            parts.Add(token);

            // Single-word gh verbs stop the scan: whatever follows `api` is an endpoint, not a verb.
            if (parts.Count == 1 && GhSingleWordVerbs.Contains(token))
            {
                break;
            }
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static readonly HashSet<string> GhSingleWordVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "status", "browse",
    };

    /// <summary>
    /// The index of the program name in a clause, after <c>NAME=value</c> assignments and wrappers.
    /// </summary>
    /// <remarks>
    /// <c>env rm -rf x</c> and <c>sudo rm -rf x</c> must not present <c>env</c>/<c>sudo</c> as the
    /// program. <c>env</c> and <c>sudo</c> are unwrapped rather than denied because they are also
    /// useful for inspection when they wrap an allowed command (<c>env FOO=1 git status</c>). When a
    /// wrapper has no wrapped command (<c>env</c> alone, <c>nice -n 5</c>), the wrapper itself is
    /// resolved as the program — <c>env</c> then passes as a reader and the rest fail closed.
    /// A wrapper's value-taking flags (<c>nice -n 5</c>) leave the value to be read as the program name,
    /// which then fails the allow-list: the safe direction.
    /// </remarks>
    private static int LeaderIndex(CommandSegment segment)
    {
        if (segment.CachedLeaderIndex != UnresolvedLeaderSentinel)
        {
            return segment.CachedLeaderIndex;
        }

        var tokens = segment.Tokens;
        var i = 0;
        var wrapperIndex = -1;
        for (var hops = 0; hops < 8; hops++)
        {
            while (i < tokens.Count && IsEnvironmentAssignment(tokens[i]))
            {
                i++;
            }

            if (i >= tokens.Count)
            {
                break;
            }

            var name = BaseName(tokens[i]);
            if (!LeaderWrappers.Contains(name))
            {
                break;
            }

            wrapperIndex = i;
            i++;

            // Skip the wrapper's own options. `env NAME=value` assignments are also options to it, and
            // `env` is the only wrapper that accepts them (`sudo -u user`, `nice -n 5` are left to fail
            // closed: the value gets read as the program name and hits default deny).
            var isEnv = name.Equals("env", StringComparison.OrdinalIgnoreCase);
            while (i < tokens.Count &&
                   (tokens[i].StartsWith("-", StringComparison.Ordinal) ||
                    (isEnv && IsEnvironmentAssignment(tokens[i]))))
            {
                i++;
            }

            if (i >= tokens.Count)
            {
                // The wrapper *was* the command: bare `env` prints the environment (inspection), bare
                // `nohup`/`time` with nothing to run means no program ran at all.
                break;
            }
        }

        if (i >= tokens.Count && wrapperIndex >= 0)
        {
            i = wrapperIndex;
        }

        segment.CachedLeaderIndex = i < tokens.Count ? i : NoLeader;
        return segment.CachedLeaderIndex;
    }

    /// <summary>Sentinel for "this clause has no program name at all" (empty or operators-only).</summary>
    private const int NoLeader = -1;

    private const int UnresolvedLeaderSentinel = -2;

    private static string BaseName(string token)
    {
        var trimmed = token.Trim();
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>
    /// The program name a clause will execute, matched on basename so <c>/usr/bin/cat</c> is inspection
    /// and — deliberately — so <c>./rm</c> is refused by the deny-list rather than treated as unknown.
    /// A project-local script (<c>./deploy.sh</c>) resolves to a name on neither list and ends at
    /// default deny, which is the direction this fence wants to fail.
    /// </summary>
    private static string? ResolveLeader(CommandSegment segment)
    {
        var index = LeaderIndex(segment);
        return index < 0 ? null : BaseName(segment.Tokens[index]);
    }

    private static bool IsEnvironmentAssignment(string token) => EnvironmentAssignmentRegex().IsMatch(token);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentAssignmentRegex();

    private static ReadOnlyCommandDecision Refuse(string reason, string rule) =>
        new(CommandDisposition.Refuse, reason, rule);

    private static readonly HashSet<string> MutatingGitLookup = new(MutatingGitSubcommands, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InspectionGitLookup = new(InspectionGitSubcommands, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MutatingGhLookup = new(MutatingGhSubcommands, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InspectionGhLookup = new(InspectionGhSubcommands, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MutationCommandsLookup = new(MutationCommands, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InspectionCommandsLookup = new(InspectionCommands, StringComparer.OrdinalIgnoreCase);

    /// <summary>Global <c>git</c> options that consume the following token as their value.</summary>
    private static readonly HashSet<string> GitGlobalValueOptions = new(StringComparer.Ordinal)
    {
        "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--super-prefix",
    };

    /// <summary>Wrappers that hide the real program name; unwrapped before matching.</summary>
    private static readonly HashSet<string> LeaderWrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "env", "sudo", "doas", "command", "builtin", "nice", "nohup", "time", "stdbuf", "unbuffer", "ionice",
    };

    /// <summary>One clause of a command line, split on shell control operators outside quotes.</summary>
    private sealed class CommandSegment(List<string> tokens)
    {
        public List<string> Tokens { get; } = tokens;
        public int CachedLeaderIndex { get; set; } = UnresolvedLeaderSentinel;
    }

    private sealed class CommandScan(List<CommandSegment> segments)
    {
        public List<CommandSegment> Segments { get; } = segments;
        public bool HasOutputRedirection { get; set; }
        public bool HasInputRedirection { get; set; }
        public bool HasCommandSubstitution { get; set; }
        public bool HasProcessSubstitution { get; set; }
    }

    /// <summary>
    /// Splits the line into clauses and records the operators that cannot be judged clause-by-clause.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quote-aware. Single quotes make everything literal, so <c>grep 'a &amp;&amp; b' f.cs</c> stays one
    /// clause and its pattern is not mistaken for a new command, and <c>echo 'x &gt; y'</c> is just
    /// text. Double quotes keep command substitution live, because <c>grep "$(rm x)" f</c> really does
    /// run <c>rm</c>.
    /// </para>
    /// <para>
    /// Redirection operators and their targets are removed from the token stream rather than left as
    /// arguments: a file name must not be read as a program argument, and <c>cat x &gt; y</c> has to be
    /// refused for the redirect rather than approved because <c>cat</c> is allow-listed. Descriptor
    /// duplication (<c>2&gt;&amp;1</c>, <c>&gt;&amp;2</c>) and writes aimed at <c>/dev/null</c> are
    /// plumbing, not tree changes, and pass; <c>git log 2&gt;&amp;1 | head</c> is exactly the inspection
    /// this policy exists to permit.
    /// </para>
    /// <para>
    /// A <c>#</c> that starts a token outside quotes begins a comment: the rest of the physical line is
    /// dropped, matching the shell, so <c>git log # check history</c> is <c>git log</c>.
    /// </para>
    /// </remarks>
    private static CommandScan Scan(string raw)
    {
        var segments = new List<CommandSegment>();
        var current = new List<string>();
        var token = new StringBuilder();
        var scan = new CommandScan(segments);
        var inSingle = false;
        var inDouble = false;

        void FlushToken()
        {
            if (token.Length > 0)
            {
                current.Add(token.ToString());
                token.Clear();
            }
        }

        void FlushSegment()
        {
            FlushToken();
            if (current.Count > 0)
            {
                segments.Add(new CommandSegment(current));
                current = [];
            }
        }

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inSingle)
            {
                if (c == '\'')
                {
                    inSingle = false;
                }
                else
                {
                    token.Append(c);
                }

                continue;
            }

            if (c == '\\' && i + 1 < raw.Length)
            {
                // An escaped newline is a line continuation; anything else becomes a literal.
                if (raw[i + 1] != '\n')
                {
                    token.Append(raw[i + 1]);
                }

                i++;
                continue;
            }

            if (c == '\'')
            {
                inSingle = true;
                continue;
            }

            if (c == '"')
            {
                inDouble = !inDouble;
                continue;
            }

            if (!inDouble && char.IsWhiteSpace(c))
            {
                FlushToken();
                continue;
            }

            if (!inDouble && c == '#' && token.Length == 0)
            {
                // Comment: drop the rest of the line.
                while (i < raw.Length && raw[i] != '\n')
                {
                    i++;
                }

                i--; // the loop's ++ lands on the newline, which then flushes the segment
                continue;
            }

            if (c == '`')
            {
                scan.HasCommandSubstitution = true;
                continue;
            }

            if (c == '$' && i + 1 < raw.Length && raw[i + 1] == '(')
            {
                // `$(` is live inside double quotes too (`grep "$(rm x)" f` really runs rm), so unlike
                // most operators this one is flagged regardless of quoting state.
                scan.HasCommandSubstitution = true;
                i++;
                continue;
            }

            if (!inDouble && c == '<' && i + 1 < raw.Length && raw[i + 1] == '(')
            {
                scan.HasProcessSubstitution = true;
                StripTrailingFileDescriptor(token);
                i++;
                FlushToken();
                continue;
            }

            if (!inDouble && c is '<' or '>')
            {
                var isOutput = c == '>';
                StripTrailingFileDescriptor(token);

                var k = i + 1;
                if (k < raw.Length && raw[k] == c)
                {
                    k++; // `>>` append, `<<` here-string/heredoc
                }
                else if (isOutput && k < raw.Length && raw[k] == '|')
                {
                    k++; // `>|` clobber
                }

                while (k < raw.Length && raw[k] == ' ')
                {
                    k++;
                }

                if (isOutput && k < raw.Length && raw[k] == '(')
                {
                    scan.HasProcessSubstitution = true;
                    FlushToken();
                    i = k;
                    continue;
                }

                if (k < raw.Length && raw[k] == '&')
                {
                    // `>&2`, `2>&1`, `>&-`: descriptor plumbing; nothing reaches disk. Consume the
                    // whole form so the descriptor number is not tokenised as a stray argument.
                    k++;
                    if (k < raw.Length && (raw[k] == '-' || raw[k] == '='))
                    {
                        k++;
                    }

                    while (k < raw.Length && (char.IsDigit(raw[k]) || raw[k] == 'p'))
                    {
                        k++; // `2>&1p` (POSIX) and plain digits
                    }

                    i = k - 1;
                    continue;
                }

                // Read the target word so `/dev/null` can be exempted and the name dropped.
                var targetStart = k;
                while (k < raw.Length && !char.IsWhiteSpace(raw[k]) && raw[k] is not (';' or '&' or '|' or '<' or '>'))
                {
                    k++;
                }

                var target = raw[targetStart..k];
                var isBitBucket = isOutput && target.Equals("/dev/null", StringComparison.Ordinal);

                if (!isBitBucket)
                {
                    if (isOutput)
                    {
                        scan.HasOutputRedirection = true;
                    }
                    else
                    {
                        scan.HasInputRedirection = true;
                    }
                }

                FlushToken();
                i = k - 1;
                continue;
            }

            // Every remaining operator is literal inside double quotes; only `$(` and backticks keep
            // their substitution power there (`grep "$(rm x)" f` really does run rm), and those are
            // handled above.
            if (inDouble)
            {
                token.Append(c);
                continue;
            }

            if (c == ';' || c == '\n' || c == '\r')
            {
                FlushSegment();
                continue;
            }

            if (c == '&')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '&')
                {
                    i++;
                }

                FlushSegment();
                continue;
            }

            if (c == '|')
            {
                if (i + 1 < raw.Length && (raw[i + 1] == '|' || raw[i + 1] == '&'))
                {
                    i++;
                }

                FlushSegment();
                continue;
            }

            token.Append(c);
        }

        FlushSegment();

        if (segments.Count == 0)
        {
            // Whitespace-, quote- or operator-only input: nothing to run and nothing provable about it,
            // so hand the allow-list pass an empty clause, which default-denies.
            segments.Add(new CommandSegment([]));
        }

        return scan;
    }

    /// <summary>
    /// Removes a trailing bare file-descriptor number that belongs to a redirection operator, so
    /// <c>grep x 2&gt;/dev/null</c> is read as <c>grep x</c> plus descriptor plumbing and
    /// <c>echo hi 2&gt;err.log</c> as a write.
    /// </summary>
    /// <remarks>
    /// Only a purely numeric token is stripped, matching how a shell binds <c>N&gt;</c>: it never
    /// touches <c>echo 12 &gt; x</c> (digits separated by a space from the operator are an argument —
    /// and here the operator has already been detected, so the digits <em>are</em> the descriptor) and
    /// never eats <c>log2&gt;x</c>, where the digits belong to the preceding file name, which is being
    /// refused as a write target anyway.
    /// </remarks>
    private static void StripTrailingFileDescriptor(StringBuilder token)
    {
        if (token.Length == 0)
        {
            return;
        }

        var allDigits = true;
        for (var i = 0; i < token.Length; i++)
        {
            if (!char.IsDigit(token[i]))
            {
                allDigits = false;
                break;
            }
        }

        if (allDigits)
        {
            token.Clear();
        }
    }
}

using System.Diagnostics;
using Xunit;

namespace AgentMesh.Tests;

/// <summary>
/// A test that needs the real <c>git</c> binary, skipped when it is not on PATH.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists alongside <see cref="PostgresFactAttribute"/>.</strong> The same argument
/// applies to a different external dependency. Most of the git-change reader is tested on captured
/// plumbing payloads, because those tests can use filenames a repository cannot contain (a newline, a
/// tab) and never depend on what is installed. A few claims can only be made by executing git: that
/// the <c>--format</c> specification the parser decodes is the one git actually produces, and that a
/// rename's diff really is a whole-file addition unless both pathspecs are passed. Those are exactly
/// the assertions a stub cannot make, because a stub restates whatever its author believed.
/// </para>
/// <para>
/// Skipping rather than failing when git is absent keeps the suite runnable anywhere. The alternative
/// blocks every CI job on a machine without git, and tests that block get deleted rather than
/// provisioned. The skip names the reason, so the gap is visible in the run output instead of being
/// silently absent.
/// </para>
/// <para>
/// The probe runs in the constructor because xUnit reads <see cref="FactAttribute.Skip"/> during
/// discovery, which happens in-process after the environment is populated; setting it later has no
/// effect.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GitFactAttribute : FactAttribute
{
    /// <summary>Populated once: probing per test would spawn one process per test for the same answer.</summary>
    private static readonly Lazy<bool> GitPresent = new(DetectGit);

    public GitFactAttribute()
    {
        if (!GitPresent.Value)
        {
            Skip = "requires the real git binary on PATH; this run cannot verify git's own output shapes";
        }
    }

    private static bool DetectGit()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return false;

            // Read before waiting: a child blocked on an unread pipe never exits, and an
            // "is git installed" probe that hangs the test host is worse than one that reports false.
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return process.HasExited && process.ExitCode == 0 &&
                   output.StartsWith("git version", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // No git, no permission to execute it, or a headless image that trimmed it out. All of
            // those mean the same thing to a test that wants to shell out.
            return false;
        }
    }
}

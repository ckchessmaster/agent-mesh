using System.Diagnostics;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Core.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

/// <summary>
/// Pins the argument contract <c>AgyAdapter</c> sends to the binary, because the capability surface
/// advertises against it. Anything the picker offers that this method drops or rewrites is a lie
/// the user cannot see.
/// </summary>
public class AgyAdapterArgumentTests
{
    private static ProcessStartInfo Args(string? model = null, string? effort = null, string? mode = null)
    {
        var psi = new ProcessStartInfo { FileName = "agy" };
        AgyAdapter.AddAgySelectionArguments(psi, new PromptReceivedEvent
        {
            Model = model,
            Effort = effort,
            Mode = mode
        });
        return psi;
    }

    private static List<string> Flatten(ProcessStartInfo psi) => psi.ArgumentList.ToList();

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void Effort_EveryDocumentedLevelIsForwardedVerbatim(string effort)
    {
        // `agy --help`: --effort  Reasoning effort (low|medium|high|xhigh|max).
        // xhigh and max used to be rewritten down to "high" here while the picker advertised all
        // five, so a user who chose `max` silently got `high` and the transcript reported `max`.
        var args = Flatten(Args(model: "gemini-4-argon", effort: effort));

        args.Should().Contain("--effort");
        args[args.IndexOf("--effort") + 1].Should().Be(effort);
    }

    [Theory]
    [InlineData("MAX", "max")]
    [InlineData(" XHigh ", "xhigh")]
    public void Effort_IsNormalizedButNotSubstituted(string input, string expected)
    {
        var args = Flatten(Args(effort: input));

        args[args.IndexOf("--effort") + 1].Should().Be(expected);
    }

    [Theory]
    [InlineData("ultra")]
    [InlineData("none")]
    [InlineData("turbo")]
    public void Effort_UnknownValueOmitsTheFlagRatherThanPickingANeighbour(string effort)
    {
        // Dropping the flag lets agy apply its own default. Mapping "ultra" to "high" would be a
        // plausible-looking substitution for a value we have no evidence the driver supports.
        var args = Flatten(Args(effort: effort));

        args.Should().NotContain("--effort");
    }

    [Fact]
    public void Effort_EveryAdvertisedValueIsActuallyForwarded()
    {
        // The invariant that was violated: whatever the contract advertises must survive this
        // method unchanged. If someone re-adds a clamp, this fails.
        foreach (var effort in DriverEffortContracts.Agy)
        {
            var args = Flatten(Args(effort: effort));
            args.Should().Contain("--effort", $"the picker advertises '{effort}' for agy");
            args[args.IndexOf("--effort") + 1].Should().Be(effort);
        }
    }

    [Fact]
    public void ContextSize_IsNeverForwardedBecauseAgyHasNoFlagForIt()
    {
        // agy --help exposes --model, --effort, --mode, --agent and no context-size flag. The old
        // picker offered 128k/200k/500k/1m/2m on this driver and none of it reached the binary.
        var psi = new ProcessStartInfo { FileName = "agy" };
        AgyAdapter.AddAgySelectionArguments(psi, new PromptReceivedEvent
        {
            Model = "gemini-4-argon",
            ContextSize = "500k"
        });

        Flatten(psi).Should().NotContain("--context");
        Flatten(psi).Should().NotContain("--context-size");
        Flatten(psi).Should().NotContain("500k");
    }

    [Fact]
    public void Model_IsForwardedVerbatim()
    {
        var args = Flatten(Args(model: "gemini-4-argon"));

        args[args.IndexOf("--model") + 1].Should().Be("gemini-4-argon");
    }

    [Theory]
    [InlineData("claude-3-7-sonnet", "claude-sonnet-5-5")]
    [InlineData("claude-3-5-sonnet", "claude-sonnet-5-5")]
    public void RetiredModelIds_AreMappedOntoTheCurrentEquivalent(string saved, string expected)
    {
        // A compatibility mapping on ids the binary now rejects, not a substitution for missing
        // telemetry: the driver would fail the run otherwise.
        var args = Flatten(Args(model: saved));

        args[args.IndexOf("--model") + 1].Should().Be(expected);
    }

    [Fact]
    public void Mode_IsForwardedVerbatim()
    {
        var args = Flatten(Args(mode: "plan"));

        args[args.IndexOf("--mode") + 1].Should().Be("plan");
    }

    [Fact]
    public void NoSelections_ProduceNoFlags()
    {
        // An empty selection must not invent defaults: the driver's own defaults apply.
        Flatten(Args()).Should().BeEmpty();
    }

    [Fact]
    public void AddAgySelectionArguments_ReturnsTheModelItPassed()
    {
        // The transcript reports this value as the effective model, so it must be what was actually
        // sent rather than what the user asked for.
        var psi = new ProcessStartInfo { FileName = "agy" };
        var resolved = AgyAdapter.AddAgySelectionArguments(psi, new PromptReceivedEvent
        {
            Model = "claude-3-7-sonnet"
        });

        resolved.Should().Be("claude-sonnet-5-5");
    }
}

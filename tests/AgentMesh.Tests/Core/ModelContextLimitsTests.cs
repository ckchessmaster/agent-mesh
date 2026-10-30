using AgentMesh.Core.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Core;

/// <summary>
/// The context-window table is now shared between the API that renders the picker and the adapter
/// that honours the choice. These tests pin the values both sides used to disagree about.
/// </summary>
public class ModelContextLimitsTests : IDisposable
{
    public ModelContextLimitsTests() => ModelContextLimits.ClearLearnedLimits();
    public void Dispose() => ModelContextLimits.ClearLearnedLimits();
    [Theory]
    [InlineData("qwen3.8-flash-next", 524_288)]
    [InlineData("flash-next-abliterated", 524_288)]
    [InlineData("qwen-3-mini", 524_288)]
    [InlineData("qwen2.5-coder", 131_072)]
    [InlineData("llama3.3", 131_072)]
    [InlineData("llama-3.1-70b", 131_072)]
    [InlineData("gpt-4o", 131_072)]
    [InlineData("deepseek-r1", 131_072)]
    public void Resolve_KnownModels_ReturnTheirVerifiedWindows(string model, int expected)
    {
        var resolution = ModelContextLimits.Resolve(model, null);

        resolution.Tokens.Should().Be(expected);
        resolution.IsFallback.Should().BeFalse(
            "a model in the table was matched, so the number is not a guess");
    }

    [Fact]
    public void Resolve_UnknownModel_FallsBackAndSaysSo()
    {
        var resolution = ModelContextLimits.Resolve("some-brand-new-model", null);

        resolution.Tokens.Should().Be(ModelContextLimits.DefaultTokenLimit);
        resolution.IsFallback.Should().BeTrue(
            "an unknown model's window is an assumption, and the caller must be able to tell");
    }

    [Theory]
    [InlineData("256k", 262_144)]
    [InlineData("1M", 1_048_576)]
    [InlineData("1m", 1_048_576)]
    [InlineData("65536", 65_536)]
    public void Resolve_ExplicitOption_WinsOverTheModelTable(string option, int expected)
    {
        // The operator picked a size; that is a decision, not telemetry, so it wins.
        ModelContextLimits.ResolveTokens("qwen3.8-flash-next", option).Should().Be(expected);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("auto")]
    [InlineData("long_context")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Resolve_NonSizeOptions_DeferToTheModelTable(string? option)
    {
        // Returning a number for "auto" would be inventing one. Deferring lets the model table
        // answer, and the fallback flag says when even that had to guess.
        var resolution = ModelContextLimits.Resolve("qwen3.8-flash-next", option);

        resolution.Tokens.Should().Be(524_288);
        resolution.Source.Should().StartWith("modelTable:");
    }

    [Fact]
    public void Resolve_ExplicitOption_ReportsItsSource()
    {
        var resolution = ModelContextLimits.Resolve("unknown-model", "200k");

        resolution.Tokens.Should().Be(204_800);
        resolution.IsFallback.Should().BeFalse();
        resolution.Source.Should().Be("option:200k");
    }

    [Theory]
    [InlineData("128k", 131_072)]
    [InlineData("1m", 1_048_576)]
    [InlineData("1024", 1_024)]
    [InlineData("auto", null)]
    [InlineData("garbage", null)]
    [InlineData("0k", null)]
    [InlineData("-8k", null)]
    public void ParseOption_HandlesSizesAndRejectsEverythingElse(string option, int? expected)
    {
        ModelContextLimits.ParseOption(option).Should().Be(expected);
    }

    [Fact]
    public void MaxSelectableTokenLimit_IsTheWidestWindowAnyDriverHonours()
    {
        // The picker must not offer a window the runtime silently clamps. 1M is the ceiling the
        // verified driver contracts support (agy reports 1M-class models; claude --autocompact
        // caps at 1M), and it is the value OpenAiAdapter's own resolution accepts.
        ModelContextLimits.MaxSelectableTokenLimit.Should().Be(1_048_576);
        ModelContextLimits.ParseOption("2m").Should().Be(2_097_152,
            "parsing is not the same as offering: 2m still resolves so old saved values do not crash");
    }

    [Theory]
    [InlineData("agy", false)]
    [InlineData("antigravity", false)]
    [InlineData("generic", false)]
    [InlineData("claude", true)]
    [InlineData("copilot", true)]
    [InlineData("openai", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void SupportsContextSelection_TracksWhetherTheDriverCanBeTold(string? adapter, bool expected)
    {
        // agy has no context-size flag and AgyAdapter forwards none, so a picker on that driver is
        // a control that changes nothing. Unknown adapters default to false: a driver earns the
        // control rather than inheriting one.
        ModelContextLimits.SupportsContextSelection(adapter).Should().Be(expected);
    }
}

public class DriverAdaptersTests
{
    [Theory]
    [InlineData("agy", "agy")]
    [InlineData("AGY", "agy")]
    [InlineData(" antigravity ", "agy")]
    [InlineData("claude-code", "claude")]
    [InlineData("github-copilot", "copilot")]
    [InlineData("ollama", "openai")]
    [InlineData("vllm", "openai")]
    [InlineData("lm-studio", "openai")]
    [InlineData("custom", "generic")]
    [InlineData("command", "generic")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void Canonicalize_MapsAliasesOntoOneName(string? input, string? expected)
    {
        // The same driver was called "agy" in one table and "antigravity" in another, which is how
        // two capability surfaces could disagree about a single install.
        DriverAdapters.Canonicalize(input).Should().Be(expected);
    }
}

public class DriverEffortContractsTests
{
    [Theory]
    [InlineData("agy", "xhigh", true)]
    [InlineData("agy", "max", true)]
    [InlineData("agy", "MAX", true)]
    [InlineData("agy", " low ", true)]
    [InlineData("agy", "ultra", false)]
    [InlineData("agy", "", false)]
    [InlineData("claude", "max", true)]
    [InlineData("copilot", "high", false)]
    [InlineData("openai", "high", false)]
    [InlineData("generic", "high", false)]
    public void Supports_ReflectsWhatTheAdapterForwardsVerbatim(string adapter, string effort, bool expected)
    {
        // The rule: advertise an effort only if the adapter passes it through unchanged. agy's
        // binary documents xhigh/max (agy --help), copilot has no effort flag at all.
        DriverEffortContracts.Supports(adapter, effort).Should().Be(expected);
    }

    [Fact]
    public void AgyContract_MatchesWhatAgyAdapterForwards()
    {
        DriverEffortContracts.Agy.Should().Equal("low", "medium", "high", "xhigh", "max");
    }

    [Fact]
    public void Copilot_HasNoEffortLadder()
    {
        // CopilotAdapter builds --model/--mode/--context only. Advertising efforts there would put
        // a control on a driver that ignores it.
        DriverEffortContracts.For("copilot").Should().BeEmpty();
    }
}

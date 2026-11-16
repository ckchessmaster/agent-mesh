using AgentMesh.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

public class ModelBehaviorProfilesTests : IDisposable
{
    public ModelBehaviorProfilesTests() => ModelBehaviorProfiles.ClearLearnedState();
    public void Dispose() => ModelBehaviorProfiles.ClearLearnedState();

    [Fact]
    public void Resolve_UnknownModel_IsEmptyDefault_WithEchoThinking()
    {
        var profile = ModelBehaviorProfiles.Resolve("mystery-9000", endpointIsLoopback: true);
        profile.Sampling.Should().BeNull();
        profile.ThinkingRoundTripMode.Should().Be(ThinkingRoundTrip.Echo);
        profile.IsDefault.Should().BeTrue();
        profile.Dialect.Should().Be(SamplingDialect.Extended); // loopback auto-resolves Extended
    }

    [Fact]
    public void Resolve_RemoteEndpoint_DefaultsToStrictDialect()
    {
        ModelBehaviorProfiles.Resolve("anything", endpointIsLoopback: false)
            .Dialect.Should().Be(SamplingDialect.Strict);
    }

    [Fact]
    public void Resolve_QwenFamily_LoopbackCarriesSampling_RemoteDoesNot()
    {
        var loopback = ModelBehaviorProfiles.Resolve("qwen3.8-flash-next", endpointIsLoopback: true);
        loopback.IsDefault.Should().BeFalse();
        loopback.Source.Should().StartWith("familyTable");
        loopback.Sampling!["temperature"].Should().Be(0.6);
        loopback.Sampling["top_k"].Should().Be(20);

        // Same model behind a strict endpoint: the extended numbers must not travel.
        var remote = ModelBehaviorProfiles.Resolve("qwen3.8-flash-next", endpointIsLoopback: false);
        remote.Sampling.Should().BeNull();
    }

    [Fact]
    public void Resolve_PresetOverrides_WinAndAreReported()
    {
        var profile = ModelBehaviorProfiles.Resolve(
            "qwen3.8-flash-next", endpointIsLoopback: true,
            presetDialectOverride: null,
            presetSamplingOverride: new Dictionary<string, double> { ["temperature"] = 0.3 },
            presetThinkingOverride: ThinkingRoundTrip.Off);
        profile.Source.Should().Be("preset");
        profile.Sampling!.Should().ContainKey("temperature");
        profile.Sampling.ContainsKey("top_k").Should().BeFalse();
        profile.ThinkingRoundTripMode.Should().Be(ThinkingRoundTrip.Off);
    }

    [Fact]
    public void MaySendSamplingKey_ExtendedBlockedOnStrict_StandardAlwaysAllowed()
    {
        var strict = ModelBehaviorProfiles.Resolve(
            "anything", endpointIsLoopback: false,
            presetSamplingOverride: new Dictionary<string, double> { ["temperature"] = 0.5, ["top_k"] = 20 });
        strict.Dialect.Should().Be(SamplingDialect.Strict);
        strict.MaySendSamplingKey("anything", "temperature").Should().BeTrue();
        strict.MaySendSamplingKey("anything", "top_k").Should().BeFalse();
    }

    [Fact]
    public void LearnedRejectedKey_RemovesFromMaySend_AndRecordReportsFirstTime()
    {
        var profile = ModelBehaviorProfiles.Resolve("qwen3.8-flash-next", endpointIsLoopback: true);
        profile.MaySendSamplingKey("qwen3.8-flash-next", "top_k").Should().BeTrue();
        ModelBehaviorProfiles.RecordRejectedSamplingKey("qwen3.8-flash-next", "top_k")
            .Should().BeTrue("first recording is new");
        ModelBehaviorProfiles.RecordRejectedSamplingKey("qwen3.8-flash-next", "top_k")
            .Should().BeFalse("already learned");
        profile.MaySendSamplingKey("qwen3.8-flash-next", "top_k").Should().BeFalse();
        profile.MaySendSamplingKey("other-model", "top_k").Should().BeTrue("learning is per model");
    }

    [Fact]
    public void ReasoningFieldRejection_FlagRoundTrip()
    {
        ModelBehaviorProfiles.IsReasoningFieldRejected("m1").Should().BeFalse();
        ModelBehaviorProfiles.RecordReasoningFieldRejected("m1");
        ModelBehaviorProfiles.IsReasoningFieldRejected("m1").Should().BeTrue();
        ModelBehaviorProfiles.IsReasoningFieldRejected("m2").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<think>unclosed")]
    [InlineData("</think> closing without opener")]
    public void SanitizeInlineThinking_RejectsUnusable(string? input)
    {
        ModelBehaviorProfiles.SanitizeInlineThinking(input).Should().BeNull();
    }

    [Fact]
    public void SanitizeInlineThinking_UnwrapsBalancedBlock()
    {
        ModelBehaviorProfiles.SanitizeInlineThinking("<think>plenty of thought</think>")
            .Should().Be("plenty of thought");
        ModelBehaviorProfiles.SanitizeInlineThinking("no tags at all")
            .Should().Be("no tags at all");
    }
}

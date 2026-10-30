using AgentMesh.Core.Models;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Models;

/// <summary>
/// The rules that decide what a memory row is allowed to look like, checked where they are written
/// rather than where they are enforced.
/// </summary>
/// <remarks>
/// The database will reject an over-long or blank column too, but only after a round trip and with a
/// provider-specific message. <see cref="AgentMemory.TryCreate"/> is the last point where the tool can
/// answer a model with "your key is 200 characters, the limit is 128", so these tests pin that surface.
/// </remarks>
public class AgentMemoryModelTests
{
    [Fact]
    public void TryCreate_WithValidFields_NormalizesAndDefaultsConfidence()
    {
        var memory = AgentMemory.TryCreate(
            agentId: "Rook",
            projectId: " Agent-Mesh ",
            category: "Build_And_Test",
            key: "  dotnet_test_flags ",
            value: "  dotnet test -v quiet --nologo  ");

        memory.Should().NotBeNull();
        memory!.AgentId.Should().Be("Rook");
        memory.ProjectId.Should().Be("Agent-Mesh", "project casing is normalized by the caller, not the model");
        memory.Category.Should().Be(AgentMemoryCategories.BuildAndTest, "the stored form is always the canonical literal");
        memory.Key.Should().Be("dotnet_test_flags");
        memory.Value.Should().Be("dotnet test -v quiet --nologo");
        memory.ConfidenceScore.Should().Be(1.0);
        memory.CreatedAt.Should().Be(memory.UpdatedAt);
        memory.IsGlobal.Should().BeFalse();
        memory.Id.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_WithMissingProject_FallsBackToGlobalSentinel(string? projectId)
    {
        // An absent project is "we are not talking about one repository", which is what '*' means. It is
        // not a validation failure, because a global lesson is the common case for toolchain quirks.
        var memory = AgentMemory.TryCreate("rook", projectId, "environment", "sdk_path", "/usr/share/dotnet");

        memory.Should().NotBeNull();
        memory!.ProjectId.Should().Be(AgentMemory.GlobalProjectId);
        memory.IsGlobal.Should().BeTrue();
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("build_and_test")]
    [InlineData("codebase_gotcha")]
    [InlineData("BUILD_AND_TEST")]
    [InlineData(" Codebase_Gotcha ")]
    public void TryCreate_AcceptsKnownCategories(string category)
    {
        var memory = AgentMemory.TryCreate("rook", "agent-mesh", category, "k", "v");

        memory.Should().NotBeNull();
        memory!.Category.Should().Be(category.Trim().ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("misc")]
    [InlineData("environment ")] // trailing space is trimmed and still valid
    public void CategoryNormalization_OnlyAcceptsTheClosedSet(string? category)
    {
        var expected = category?.Trim().ToLowerInvariant();
        var allowed = new[] { "environment", "build_and_test", "codebase_gotcha" };

        AgentMemoryCategories.IsAllowed(category).Should().Be(allowed.Contains(expected),
            $"'{category}' must be accepted only when it names a declared bucket");
    }

    [Fact]
    public void TryCreate_RejectsUnknownCategory_RatherThanDefaulting()
    {
        // Defaulting a bad bucket would file the lesson where nobody looks for it.
        AgentMemory.TryCreate("rook", "agent-mesh", "hot_takes", "k", "v").Should().BeNull();
    }

    [Theory]
    [InlineData("", "environment", "key", "value")]
    [InlineData("rook", "environment", "", "value")]
    [InlineData("rook", "environment", "   ", "value")]
    [InlineData("rook", "environment", "key", "")]
    [InlineData("rook", "environment", "key", "   ")]
    [InlineData("rook", "not_a_category", "key", "value")]
    public void TryCreate_RejectsMissingIdentityOrContent(
        string agentId, string category, string key, string value)
    {
        AgentMemory.TryCreate(agentId, "agent-mesh", category, key, value).Should().BeNull();
    }

    [Fact]
    public void TryCreate_RejectsOverlongIdentityFields()
    {
        AgentMemory.TryCreate(new string('a', AgentMemory.MaxAgentIdLength + 1), "p", "environment", "k", "v")
            .Should().BeNull();
        AgentMemory.TryCreate("rook", new string('p', AgentMemory.MaxProjectIdLength + 1), "environment", "k", "v")
            .Should().BeNull();
        AgentMemory.TryCreate("rook", "p", "environment", new string('k', AgentMemory.MaxKeyLength + 1), "v")
            .Should().BeNull();

        // Exactly at the limit is still storable, so the boundary is inclusive.
        AgentMemory.TryCreate(new string('a', AgentMemory.MaxAgentIdLength), "p", "environment",
            new string('k', AgentMemory.MaxKeyLength), "v").Should().NotBeNull();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.42)]
    [InlineData(1.0)]
    public void ConfidenceScore_WithinRange_IsAccepted(double confidence)
    {
        var memory = AgentMemory.TryCreate("rook", "p", "environment", "k", "v", confidence);

        memory.Should().NotBeNull();
        memory!.ConfidenceScore.Should().Be(confidence);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(1.5)]
    public void ConfidenceScore_OutsideRange_IsClamped(double confidence)
    {
        // Clamped, not rejected: the score is advisory and a caller that sends 1.5 clearly means "fully
        // confident". Refusing the write would lose the lesson over a number nobody reads.
        var memory = AgentMemory.TryCreate("rook", "p", "environment", "k", "v", confidence);

        memory.Should().NotBeNull();
        memory!.ConfidenceScore.Should().Be(confidence < 0 ? 0.0 : 1.0);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ConfidenceScore_NonFinite_IsRejected(double confidence)
    {
        // NaN cannot be compared in SQL and would poison an ORDER BY confidence_score read.
        AgentMemory.TryCreate("rook", "p", "environment", "k", "v", confidence).Should().BeNull();
    }

    [Fact]
    public void TryApplyUpdate_RefreshesContentButPreservesIdentityAndCreation()
    {
        var original = AgentMemory.TryCreate("rook", "agent-mesh", "environment", "sdk_path", "/usr/share/dotnet")!;
        var originalId = original.Id;
        var createdAt = original.CreatedAt;
        var stale = original.CreatedAt.AddMinutes(-30);

        var applied = AgentMemory.TryApplyUpdate(
            original, "codebase_gotcha", "/usr/lib/dotnet", 0.7, out var updated);

        applied.Should().BeTrue();
        updated.Id.Should().Be(originalId, "an update replaces content, it does not mint a new row");
        updated.CreatedAt.Should().Be(createdAt);
        updated.UpdatedAt.Should().BeAfter(stale);
        updated.AgentId.Should().Be("rook");
        updated.ProjectId.Should().Be("agent-mesh");
        updated.Key.Should().Be("sdk_path");
        updated.Value.Should().Be("/usr/lib/dotnet");
        updated.Category.Should().Be(AgentMemoryCategories.CodebaseGotcha);
        updated.ConfidenceScore.Should().Be(0.7);
    }

    [Fact]
    public void TryApplyUpdate_KeepsConfidenceWhenNotSupplied()
    {
        var original = AgentMemory.TryCreate("rook", "p", "environment", "k", "v", 0.3)!;

        AgentMemory.TryApplyUpdate(original, "environment", "v2", null, out var updated)
            .Should().BeTrue();

        updated.ConfidenceScore.Should().Be(0.3, "a re-save that says nothing about confidence keeps the prior trust");
    }

    [Theory]
    [InlineData("bogus_category", "value")]
    [InlineData("environment", "")]
    [InlineData("environment", "   ")]
    public void TryApplyUpdate_RejectsInvalidCategoryOrValue(string category, string value)
    {
        var original = AgentMemory.TryCreate("rook", "p", "environment", "k", "v")!;
        var before = original.Value;

        AgentMemory.TryApplyUpdate(original, category, value, null, out _).Should().BeFalse();

        original.Value.Should().Be(before, "a rejected update must leave the stored row untouched");
    }

    [Fact]
    public void ValidationErrors_ListsEveryProblemAndAcceptsARowBuiltByTryCreate()
    {
        AgentMemory.TryCreate("rook", "p", "environment", "k", "v")!
            .ValidationErrors().Should().BeEmpty();

        var broken = new AgentMemory
        {
            AgentId = new string('a', AgentMemory.MaxAgentIdLength + 1),
            ProjectId = " ",
            Category = "nope",
            Key = "",
            Value = "",
            ConfidenceScore = double.NaN,
        };

        var errors = broken.ValidationErrors();

        errors.Should().HaveCount(6);
        errors.Should().ContainSingle(e => e.Contains(nameof(AgentMemory.AgentId)));
        errors.Should().ContainSingle(e => e.Contains(nameof(AgentMemory.ProjectId)));
        errors.Should().ContainSingle(e => e.Contains(nameof(AgentMemory.Category)));
        errors.Should().ContainSingle(e => e.Contains(nameof(AgentMemory.Key)));
        errors.Should().ContainSingle(e => e.Contains(nameof(AgentMemory.Value)));
        errors.Should().ContainSingle(e => e.Contains(nameof(AgentMemory.ConfidenceScore)));
    }

    [Fact]
    public void StoreableNow_RoundTripsThePrecisionPostgreSQLKeeps()
    {
        // timestamptz stores microseconds; DateTimeOffset.UtcNow carries 100ns ticks. A timestamp minted
        // at full precision comes back from the database different from what was sent, which breaks any
        // comparison between the row a write returns and the row a later read sees.
        var now = AgentMemory.StoreableNow();

        (now.Ticks % 10).Should().Be(0, "microsecond-aligned ticks survive a timestamptz round trip");
        now.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

        var memory = AgentMemory.TryCreate("rook", "p", "environment", "k", "v")!;
        (memory.CreatedAt.Ticks % 10).Should().Be(0);
        (memory.UpdatedAt.Ticks % 10).Should().Be(0);
    }

    [Fact]
    public void Identity_IsTheUniquenessTriple()
    {
        var memory = AgentMemory.TryCreate("rook", "agent-mesh", "environment", "sdk_path", "/x")!;

        memory.Identity.Should().Be(new MemoryIdentity("rook", "agent-mesh", "sdk_path"));
        memory.Identity.Should().Be(memory.Identity, "the repository and the tests must agree on one definition");
    }

    [Fact]
    public void AllowedValuesHint_ListsEveryDeclaredBucket()
    {
        AgentMemoryCategories.AllowedValuesHint()
            .Should().Be("'environment', 'build_and_test', 'codebase_gotcha'");

        AgentMemoryCategories.All.Should().OnlyContain(c => AgentMemoryCategories.IsAllowed(c));
    }
}

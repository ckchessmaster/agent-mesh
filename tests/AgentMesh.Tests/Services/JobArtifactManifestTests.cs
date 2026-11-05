using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The merge and parse rules behind a job's artifact manifest.
/// </summary>
/// <remarks>
/// These are the rules both writers share (sync-time mirror and read-time backfill), so they are tested
/// directly rather than only through the endpoints: the interesting cases are collisions and re-syncs, and
/// driving those through a controller would assert the same rules twice while making the collision setup
/// much noisier.
/// </remarks>
public class JobArtifactManifestTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static JobArtifactDto Artifact(
        string name,
        string? id = null,
        DateTimeOffset? updated = null,
        string? content = null,
        long size = 10,
        string? s3Key = null) =>
        new(
            Id: id ?? name,
            JobId: JobId,
            SourceSessionId: SessionId,
            Name: name,
            Summary: null,
            ContentType: "text/markdown",
            SizeBytes: size,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: updated ?? DateTimeOffset.UtcNow,
            Content: content,
            S3Key: s3Key,
            DownloadUrl: JobArtifactManifest.BuildContentUrl(JobId, id ?? name));

    [Fact]
    public void Read_WhenColumnIsEmptyObject_ReturnsEmpty()
    {
        // The '{}' default that predates this column, and the shape a hand-written row may hold.
        JobArtifactManifest.Read("{}").Should().BeEmpty();
        JobArtifactManifest.Read(null).Should().BeEmpty();
        JobArtifactManifest.Read("").Should().BeEmpty();
        JobArtifactManifest.Read("   ").Should().BeEmpty();
    }

    [Fact]
    public void Read_WhenPayloadIsCorrupt_ReturnsEmptyInsteadOfThrowing()
    {
        // A jsonb column can hold anything a previous deploy wrote. A corrupt manifest must degrade to
        // "no artifacts", not 500 the panel — same posture as SessionsController.ExtractArtifacts.
        JobArtifactManifest.Read("{ not json").Should().BeEmpty();
        JobArtifactManifest.Read("[ { broken").Should().BeEmpty();
    }

    [Fact]
    public void Read_WhenEnvelopeForm_PicksArtifactsArray()
    {
        var json = """
            {"artifacts":[{"id":"a1","jobId":"11111111-1111-1111-1111-111111111111","name":"plan.md",
              "contentType":"text/markdown","sizeBytes":42,"createdAt":"2026-11-01T00:00:00Z",
              "updatedAt":"2026-11-02T00:00:00Z","downloadUrl":"/x"}]}
            """;

        var read = JobArtifactManifest.Read(json);

        read.Should().ContainSingle();
        read[0].Name.Should().Be("plan.md");
        read[0].SizeBytes.Should().Be(42);
    }

    [Fact]
    public void Read_WhenPayloadIsPascalCase_StillDeserialises()
    {
        // Payloads written without a camelCase naming policy are PascalCase, and they are already in
        // databases. PropertyNameCaseInsensitive is what keeps the mirror readable across that change.
        var json = """
            [{"Id":"a1","JobId":"11111111-1111-1111-1111-111111111111","Name":"SPEC.md",
              "ContentType":"text/markdown","SizeBytes":7,"CreatedAt":"2026-11-01T00:00:00Z",
              "UpdatedAt":"2026-11-02T00:00:00Z"}]
            """;

        JobArtifactManifest.Read(json).Single().Name.Should().Be("SPEC.md");
    }

    [Fact]
    public void Write_ThenRead_RoundTripsAndOmitsNulls()
    {
        var json = JobArtifactManifest.Write(new[] { Artifact("plan.md", content: null, s3Key: null) });

        json.Should().NotContain("\"content\"");
        json.Should().NotContain("\"s3Key\"");

        var round = JobArtifactManifest.Read(json);
        round.Single().Name.Should().Be("plan.md");
        round.Single().Content.Should().BeNull();
    }

    [Fact]
    public void Merge_WhenNameDiffers_KeepsBothEntries()
    {
        var merged = JobArtifactManifest.Merge(new[] { Artifact("plan.md") }, new[] { Artifact("diagram.mmd") });

        merged.Should().HaveCount(2);
        merged.Select(a => a.Name).Should().Contain(new[] { "plan.md", "diagram.mmd" });
    }

    [Fact]
    public void Merge_WhenNameMatchesCaseInsensitively_UpdatesInPlaceAndKeepsIdentity()
    {
        // The rule that stops the panel filling with near-duplicates when an agent re-syncs an edited
        // file: keying is by name, and the id survives so the open selection does not jump.
        var original = Artifact("plan.md", id: "artifact-id-1", size: 10);
        var resync = Artifact("PLAN.md", id: "artifact-id-2", size: 99, content: "edited body");

        var merged = JobArtifactManifest.Merge(new[] { original }, new[] { resync });

        merged.Should().ContainSingle();
        merged[0].Id.Should().Be("artifact-id-1");
        merged[0].SizeBytes.Should().Be(99);
    }

    [Fact]
    public void Merge_WhenIncomingCarriesNoBody_KeepsTheStoredBody()
    {
        // The list fold runs on every panel open and carries no Content. Blanking the body a detail read
        // had stored would make the panel lose what it already had.
        var stored = Artifact("plan.md", content: "# kept");
        var incoming = Artifact("plan.md", content: null);

        var merged = JobArtifactManifest.Merge(new[] { stored }, new[] { incoming });

        merged.Single().Content.Should().Be("# kept");
    }

    [Fact]
    public void Merge_WhenIncomingCarriesNoSummary_KeepsTheStoredSummary()
    {
        var stored = Artifact("plan.md") with { Summary = "the architecture plan" };
        var incoming = Artifact("plan.md") with { Summary = null };

        JobArtifactManifest.Merge(new[] { stored }, new[] { incoming })
            .Single().Summary.Should().Be("the architecture plan");
    }

    [Fact]
    public void Merge_WhenIncomingIsNewer_UpdatedAtAdvances()
    {
        var older = DateTimeOffset.UtcNow.AddDays(-2);
        var newer = DateTimeOffset.UtcNow;

        var merged = JobArtifactManifest.Merge(
            new[] { Artifact("plan.md", updated: older) },
            new[] { Artifact("plan.md", updated: newer) });

        merged.Single().UpdatedAt.Should().BeCloseTo(newer, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Merge_DropsEntriesWithoutAName()
    {
        var merged = JobArtifactManifest.Merge(
            JobArtifactManifest.Empty,
            new[] { Artifact(""), Artifact(" ") with { Name = "" }, Artifact("real.md") });

        merged.Should().ContainSingle(a => a.Name == "real.md");
    }

    [Fact]
    public void SortForDisplay_IsNewestFirstThenNameStable()
    {
        // Determinism matters because the panel refetches constantly: an order that flips between two
        // identical payloads makes the list jump under the cursor.
        var now = DateTimeOffset.UtcNow;
        var list = new List<JobArtifactDto>
        {
            Artifact("b.md", updated: now.AddMinutes(-1)),
            Artifact("a.md", updated: now),
            Artifact("c.md", updated: now)
        };

        var sorted = JobArtifactManifest.SortForDisplay(list);

        sorted.Select(a => a.Name).Should().Equal("a.md", "c.md", "b.md");
    }

    [Fact]
    public void MergeIntoManifest_ReturnsBothMergedListAndJsonToStore()
    {
        var merged = JobArtifactManifest.MergeIntoManifest(
            JobArtifactManifest.Write(new[] { Artifact("plan.md") }),
            new[] { Artifact("diagram.mmd") },
            out var json);

        merged.Should().HaveCount(2);
        JobArtifactManifest.Read(json).Should().HaveCount(2);
    }

    [Fact]
    public void BuildContentUrl_EscapesTheArtifactId()
    {
        // Artifact ids reach this URL from a manifest an agent wrote. An id containing a slash or a query
        // must not be able to address a different route.
        var url = JobArtifactManifest.BuildContentUrl(JobId, "weird/../id?x=1");

        url.Should().StartWith($"/api/project-jobs/{JobId}/artifacts/");
        url.Should().NotContain("../");
        url.Should().NotContain("?x=1");
        url.Should().EndWith("/content");
    }

    [Fact]
    public void FromSessionArtifact_RepointsDownloadUrlAtTheJobEndpoint()
    {
        var entry = JobArtifactManifest.FromSessionArtifact(
            jobId: JobId,
            sourceSessionId: SessionId,
            id: "abc",
            name: "plan.md",
            summary: "s",
            contentType: "text/markdown",
            sizeBytes: 5,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            s3Key: "sessions/2/artifacts/abc/plan.md");

        entry.DownloadUrl.Should().Be($"/api/project-jobs/{JobId}/artifacts/abc/content");
        entry.JobId.Should().Be(JobId);
        entry.SourceSessionId.Should().Be(SessionId);
        // A mirror never carries the body: the list must stay cheap.
        entry.Content.Should().BeNull();
    }

    [Fact]
    public void ReadSessionArtifacts_MapsSessionManifestAndDropsBody()
    {
        var metadata = """
            {"jobId":"11111111-1111-1111-1111-111111111111",
             "artifacts":[
               {"id":"a1","sessionId":"22222222-2222-2222-2222-222222222222","name":"plan.md",
                "summary":"plan","contentType":"text/markdown","sizeBytes":12,
                "createdAt":"2026-11-01T00:00:00Z","updatedAt":"2026-11-02T00:00:00Z",
                "content":"# huge body that must not ride the list","s3Key":"k1"},
               {"name":"no-id.md","contentType":"text/plain","sizeBytes":3,
                "createdAt":"2026-11-01T00:00:00Z","updatedAt":"2026-11-02T00:00:00Z"},
               {"contentType":"text/plain","sizeBytes":1},
               "not-an-object"
             ]}
            """;

        var read = JobArtifactManifest.ReadSessionArtifacts(metadata, JobId);

        // The unnamed entry and the raw string are skipped; the two named ones map.
        read.Should().HaveCount(2);

        var plan = read.Single(a => a.Name == "plan.md");
        plan.Id.Should().Be("a1");
        plan.JobId.Should().Be(JobId);
        plan.SourceSessionId.Should().Be(SessionId);
        plan.S3Key.Should().Be("k1");
        plan.Content.Should().BeNull();
        plan.DownloadUrl.Should().Be($"/api/project-jobs/{JobId}/artifacts/a1/content");

        // An entry with no id is addressed by name, which is what the disk-discovered entries do too.
        read.Single(a => a.Name == "no-id.md").Id.Should().Be("no-id.md");
    }

    [Fact]
    public void ReadSessionArtifacts_WhenMetadataHasNoArtifactsKey_ReturnsEmpty()
    {
        JobArtifactManifest.ReadSessionArtifacts("""{"jobId":"x"}""", JobId).Should().BeEmpty();
        JobArtifactManifest.ReadSessionArtifacts(null, JobId).Should().BeEmpty();
        JobArtifactManifest.ReadSessionArtifacts("not json", JobId).Should().BeEmpty();
        JobArtifactManifest.ReadSessionArtifacts("""{"artifacts":"nope"}""", JobId).Should().BeEmpty();
    }

    [Fact]
    public void ReadSessionArtifacts_WhenOwningSessionIsGiven_ItWinsOverTheEntryPayload()
    {
        // The content endpoint resolves bytes from the source session's directory, so the row whose
        // manifest is being read is the authoritative location — an entry claiming a different session
        // would otherwise point at a directory that does not hold the file.
        var other = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var metadata = $$"""
            {"artifacts":[{"id":"a1","sessionId":"{{other}}","name":"plan.md","contentType":"text/markdown",
                           "sizeBytes":2,"createdAt":"2026-11-01T00:00:00Z","updatedAt":"2026-11-02T00:00:00Z"}]}
            """;

        JobArtifactManifest.ReadSessionArtifacts(metadata, JobId, SessionId)
            .Single().SourceSessionId.Should().Be(SessionId);

        // Without a row to attribute it to, the payload value is still used rather than dropped.
        JobArtifactManifest.ReadSessionArtifacts(metadata, JobId)
            .Single().SourceSessionId.Should().Be(other);
    }

    [Fact]
    public void ReadSessionArtifacts_WhenEntryOmitsSessionId_FallsBackToTheOwningRow()
    {
        // Entries written before sessionId was part of the shape must still resolve to their bytes.
        var metadata = """
            {"artifacts":[{"id":"a1","name":"plan.md","contentType":"text/markdown","sizeBytes":2,
                           "createdAt":"2026-11-01T00:00:00Z","updatedAt":"2026-11-02T00:00:00Z"}]}
            """;

        JobArtifactManifest.ReadSessionArtifacts(metadata, JobId, SessionId)
            .Single().SourceSessionId.Should().Be(SessionId);
        JobArtifactManifest.ReadSessionArtifacts(metadata, JobId)
            .Single().SourceSessionId.Should().BeNull();
    }

    [Fact]
    public void ReadSessionArtifacts_WhenPayloadIsPascalCase_StillMaps()
    {
        var metadata = """
            {"Artifacts":[{"Id":"a1","Name":"SPEC.md","ContentType":"text/markdown","SizeBytes":2,
                           "CreatedAt":"2026-11-01T00:00:00Z","UpdatedAt":"2026-11-02T00:00:00Z"}]}
            """;

        JobArtifactManifest.ReadSessionArtifacts(metadata, JobId)
            .Single().Name.Should().Be("SPEC.md");
    }
}

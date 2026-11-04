using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The shared document-index formatter: what an agent is told about a project's library, and the
/// invariants that keep that text cheap to cache and honest about what it omits.
/// </summary>
/// <remarks>
/// <para>
/// This class is the single source of the index text for both prompt paths — the worker's Layer 3 and the
/// four legacy <c>[Project Guidelines]</c> envelope sites in the Api. The tests here are therefore not
/// about a helper's formatting taste: <see cref="FormatDocumentIndex_AndFormatProjectEnvelope_AreTheSameBytesForTheSameLibrary"/>
/// is the assertion that makes "the two prompt paths cannot drift" a checked property rather than an
/// intention stated in a comment.
/// </para>
/// <para>
/// Everything is exercised through the pure formatter with hand-built entries, so no database and no
/// provider is involved. The reads that feed it are covered by
/// <see cref="Controllers.ProjectsControllerDocumentTests"/> and the layer integration by
/// <see cref="PromptStackDocumentLayerTests"/>.
/// </para>
/// </remarks>
public class ProjectDocumentIndexFormatterTests
{
    private const string ProjectId = "agent-mesh";

    private static ProjectDocumentEntry Text(string id, string title, string content) => new(
        Id: id, Title: title, KindLabel: "text", Content: content);

    private static ProjectDocumentEntry File(
        string id, string title, string fileName, string contentType, long size) => new(
        Id: id, Title: title, KindLabel: "file", FileName: fileName, ContentType: contentType, SizeBytes: size);

    // ── presence and absence ────────────────────────────────────────────────────

    [Fact]
    public void NullLibrary_EmitsNoSection()
    {
        // A header announcing an empty library is a line the agent has to read to discover there is
        // nothing to read, and it is a section boundary that changes the cached prefix for every project
        // that has never had a document.
        ProjectDocumentIndexFormatter.FormatDocumentIndex(null, ProjectId).Should().BeNull();
    }

    [Fact]
    public void EmptyLibrary_EmitsNoSection()
    {
        ProjectDocumentIndexFormatter.FormatDocumentIndex([], ProjectId).Should().BeNull();
    }

    [Fact]
    public void EmptyLibrary_EnvelopeKeepsOnlyGuidelines()
    {
        // The regression guard for the four legacy call sites: for a project with no documents the
        // envelope must be byte-identical to what those sites emitted before this feature existed, or
        // every existing prompt assertion in the suite would need rewriting and every deployed agent
        // would see a changed prefix.
        ProjectDocumentIndexFormatter
            .FormatProjectEnvelope("Build with dotnet build.", [], ProjectId)
            .Should().Be("[Project Guidelines: Build with dotnet build.]\n");
    }

    [Fact]
    public void EmptyLibraryAndNoGuidelines_EnvelopeIsEmpty()
    {
        ProjectDocumentIndexFormatter.FormatProjectEnvelope(null, [], ProjectId).Should().BeEmpty();
        ProjectDocumentIndexFormatter.FormatProjectEnvelope("   ", null, ProjectId).Should().BeEmpty();
    }

    // ── content of an entry ─────────────────────────────────────────────────────

    [Fact]
    public void TextDocument_NamesItselfItsIdAndItsUrlAndQuotesItsBody()
    {
        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex(
            [Text("abc123", "API contract", "POST /things creates a thing.")], ProjectId);

        block.Should().NotBeNull();
        block.Should().Contain(ProjectDocumentIndexFormatter.SectionHeader);
        block.Should().Contain("API contract");
        block.Should().Contain("text");
        block.Should().Contain("doc id: abc123");
        block.Should().Contain($"/api/projects/{ProjectId}/documents/abc123/content");
        block.Should().Contain("POST /things creates a thing.");
    }

    [Fact]
    public void FileDocument_NamesFileAndTypeAndSizeButQuotesNothing()
    {
        // A file's bytes are not in the row, so there is nothing to excerpt. Claiming an excerpt for a
        // file would be the formatter inventing content it has never read.
        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex(
            [File("deadbeef", "Architecture diagram", "arch.png", "image/png", 1482391)], ProjectId);

        block.Should().Contain("arch.png");
        block.Should().Contain("image/png");
        block.Should().Contain("1.4 MB", "an agent deciding whether to fetch needs an order of magnitude, not a byte count");
        block.Should().Contain("doc id: deadbeef");
        block.Should().NotContain("excerpt:", "a file document has no body in the row to quote");
    }

    [Fact]
    public void Excerpt_CollapsesNewlinesOntoOneLine()
    {
        // The index is a list of one-line entries. A pasted document with hard returns would otherwise
        // break its entry across lines and read as several documents.
        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex(
            [Text("id1", "Notes", "line one\nline two\n\nline   three")], ProjectId);

        block.Should().Contain("excerpt: line one line two line three");
    }

    [Fact]
    public void Excerpt_CutsOnAWordBoundaryAndMarksTheCut()
    {
        var body = string.Join(' ', Enumerable.Repeat("word", 400));

        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex([Text("id1", "Long", body)], ProjectId);

        block.Should().Contain(ProjectDocument.TruncationMarker,
            "an agent must be able to tell a quoted whole from a quoted prefix");
        var excerptLine = block!.Split('\n').First(l => l.Contains("excerpt:"));
        excerptLine.Length.Should().BeLessThan(ProjectDocumentIndexFormatter.MaxExcerptChars + 80,
            "the excerpt is bounded, which is what keeps 25 entries from dominating the layer");
        excerptLine.TrimEnd().Should().EndWith(ProjectDocument.TruncationMarker,
            "the cut lands at a word boundary, not mid-identifier");
        excerptLine.Should().NotContain("wor" + ProjectDocument.TruncationMarker);
    }

    [Fact]
    public void WhitespaceOnlyTextDocument_StillListedButWithoutAnExcerpt()
    {
        // A row whose body is blank is a row the API accepted before the whitespace rule existed, or one
        // backfilled by hand. It still has to be listed — it exists and is fetchable — but it must not
        // render "excerpt: " with nothing after it.
        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex([Text("id1", "Blank", "   \n  ")], ProjectId);

        block.Should().Contain("Blank");
        block.Should().NotContain("excerpt:");
    }

    [Fact]
    public void MissingTitle_RendersALabelRatherThanAnEmptyEntry()
    {
        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex([Text("id1", "", "body")], ProjectId);

        block.Should().Contain("Untitled document");
    }

    // ── the cap ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MoreDocumentsThanTheCap_ListsTheCapAndSaysHowManyWereDropped()
    {
        // The count of what was omitted is reported rather than the omission being silent: an agent that
        // believes it saw the whole library will not go looking for the spec that is not listed, and a
        // spec it never learned about reads to that agent as a spec that does not exist.
        var many = Enumerable.Range(0, ProjectDocumentIndexFormatter.MaxRenderedDocuments + 7)
            .Select(i => Text($"id{i:D3}", $"Doc {i:D3}", "body"))
            .Cast<ProjectDocumentEntry>()
            .ToList();

        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex(many, ProjectId);

        block.Should().NotBeNull();
        CountEntryLines(block!).Should().Be(ProjectDocumentIndexFormatter.MaxRenderedDocuments);
        block.Should().Contain("7 more document(s) not listed");
        block.Should().Contain($"/api/projects/{ProjectId}/documents for the full library");
    }

    [Fact]
    public void CallerSuppliedTotal_IsUsedForTheOmissionLineWhenThePageWasBounded()
    {
        // The prompt path reads at most MaxRenderedDocuments rows, so the list it hands over can never
        // itself reveal that the library is bigger. Without this parameter the index would claim to be
        // complete about a library that is not.
        var page = Enumerable.Range(0, ProjectDocumentIndexFormatter.MaxRenderedDocuments)
            .Select(i => Text($"id{i:D3}", $"Doc {i:D3}", "body"))
            .Cast<ProjectDocumentEntry>()
            .ToList();

        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex(page, ProjectId, totalCount: 50);

        block.Should().Contain("25 more document(s) not listed",
            "the library holds 50 and the page holds 25; the difference is what the agent was not told");
    }

    [Fact]
    public void TotalSmallerThanThePage_CannotClaimRowsWereDropped()
    {
        // A caller that passes a nonsense total must not produce an index that claims to have hidden
        // rows it actually printed.
        var page = Enumerable.Range(0, 3)
            .Select(i => Text($"id{i}", $"Doc {i}", "body"))
            .Cast<ProjectDocumentEntry>()
            .ToList();

        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex(page, ProjectId, totalCount: 1);

        block.Should().NotContain("more document(s) not listed");
    }

    [Fact]
    public void LibraryCapIsLargerThanTheRenderCapSoTheOmissionLineIsReachable()
    {
        // If the two numbers ever became equal, the omission line would be dead code and a full library
        // would silently stop being listed one document at a time. The relationship is the contract.
        ProjectDocument.MaxDocumentsPerProject.Should().BeGreaterThan(
            ProjectDocumentIndexFormatter.MaxRenderedDocuments,
            "the API cap must exceed the prompt cap or the index silently truncates with no way to say so");
    }

    // ── cache stability ─────────────────────────────────────────────────────────

    [Fact]
    public void BlockCarriesNoTimestampsOrCounters()
    {
        // Layer 3 sits in the cached prefix. A CreatedAt or an "as of" timestamp would change the bytes
        // on every read and invalidate every layer below it for every turn — the exact cost the layering
        // exists to remove.
        var documents = new[]
        {
            Text("id1", "Alpha", "a"),
            File("id2", "Beta", "b.pdf", "application/pdf", 12345)
        };

        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex(documents, ProjectId)!;

        block.Should().NotMatchRegex(@"\d{4}-\d{2}-\d{2}", "no ISO dates in the cached prefix");
        block.Should().NotMatchRegex(@"\d{2}:\d{2}", "no clock times in the cached prefix");
        block.Should().NotContain(DateTime.UtcNow.Year.ToString());
    }

    [Fact]
    public void SameLibraryDifferentInsertionOrder_ProducesIdenticalBytes()
    {
        // The database's ORDER BY is the primary defence, but the renderer re-orders for the same reason
        // the roster does: the cache property belongs to the renderer. If a future caller builds entries
        // from a second source, or the provider changes its plan, the block must not reshuffle.
        var documents = new[]
        {
            Text("id3", "Gamma", "g"),
            Text("id1", "Alpha", "a"),
            Text("id2", "Beta", "b")
        };

        var forward = ProjectDocumentIndexFormatter.FormatDocumentIndex(documents, ProjectId);
        var reversed = ProjectDocumentIndexFormatter.FormatDocumentIndex(documents.Reverse().ToList(), ProjectId);
        var shuffled = ProjectDocumentIndexFormatter.FormatDocumentIndex(
            [documents[1], documents[2], documents[0]], ProjectId);

        reversed.Should().Be(forward);
        shuffled.Should().Be(forward);
    }

    [Fact]
    public void OrderingIsByTitleThenIdSoAnEditDoesNotReshuffleTheBlock()
    {
        // Title-then-id is the order that survives an edit. Ordering by UpdatedAt would move a document
        // to the top the moment it was touched, changing every byte after it in the cached prefix.
        var block = ProjectDocumentIndexFormatter.FormatDocumentIndex(
            [Text("idz", "Zeta", "z"), Text("ida", "Alpha", "a"), Text("idm", "Mid", "m")], ProjectId)!;

        var titles = EntryLines(block).Select(l => l.Split(' ')[1]).ToList();
        titles.Should().ContainInOrder("Alpha", "Mid", "Zeta");
    }

    // ── url construction ────────────────────────────────────────────────────────

    [Fact]
    public void BuildContentUrl_EscapesTheProjectId()
    {
        // The project id reaches this method from a route value. Concatenating it would let a crafted id
        // become a second path segment or a traversal.
        ProjectDocumentIndexFormatter.BuildContentUrl("weird/../id", Guid.Empty)
            .Should().Be("/api/projects/weird%2F..%2Fid/documents/00000000000000000000000000000000/content");
    }

    [Fact]
    public void BuildContentUrl_NormalisesAGuidToTheNFormat()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        ProjectDocumentIndexFormatter.BuildContentUrl("p", id)
            .Should().Be("/api/projects/p/documents/11111111222233334444555555555555/content");

        // The string overload must agree with the Guid overload, because the DTO, the index and the
        // staging path build this URL from different shapes of the same id.
        ProjectDocumentIndexFormatter.BuildContentUrl("p", id.ToString())
            .Should().Be(ProjectDocumentIndexFormatter.BuildContentUrl("p", id));
        ProjectDocumentIndexFormatter.BuildContentUrl("p", id.ToString("D").ToUpperInvariant())
            .Should().Be(ProjectDocumentIndexFormatter.BuildContentUrl("p", id));
    }

    [Fact]
    public void BuildContentUrl_LeavesANonGuidIdAloneButEscapesIt()
    {
        // A non-guid id is not a reason to throw — the formatter renders what the row says — but it is
        // exactly the case where escaping matters.
        ProjectDocumentIndexFormatter.BuildContentUrl("p", "a b?c")
            .Should().Be("/api/projects/p/documents/a%20b%3Fc/content");
    }

    // ── the drift invariant ─────────────────────────────────────────────────────

    [Fact]
    public void FormatDocumentIndex_AndFormatProjectEnvelope_AreTheSameBytesForTheSameLibrary()
    {
        // THE assertion for this feature's central risk. The worker composes Layer 3 through
        // FormatDocumentIndex; the four legacy envelope sites compose their preamble through
        // FormatProjectEnvelope. If those two ever produced different text for one library, an agent on
        // the layered path and an agent on the flat path would be told different things about the same
        // project's documents — and the divergence would be invisible until an agent ignored a spec.
        var documents = new[]
        {
            Text("id1", "API contract", "POST /things creates a thing."),
            File("id2", "arch.png", "arch.png", "image/png", 20480)
        };

        var layered = ProjectDocumentIndexFormatter.FormatDocumentIndex(documents, ProjectId);
        var envelope = ProjectDocumentIndexFormatter.FormatProjectEnvelope(null, documents, ProjectId);

        envelope.Should().Be(layered,
            "the envelope is the guidelines line plus the index; with no guidelines the two must be identical");
    }

    [Fact]
    public void EnvelopeIsGuidelinesThenIndexInThatOrder()
    {
        var documents = new[] { Text("id1", "API contract", "body") };

        var envelope = ProjectDocumentIndexFormatter.FormatProjectEnvelope(
            "Always run scoped tests.", documents, ProjectId);

        envelope.Should().StartWith("[Project Guidelines: Always run scoped tests.]\n");
        envelope.IndexOf(ProjectDocumentIndexFormatter.SectionHeader, StringComparison.Ordinal)
            .Should().BeGreaterThan(0, "the guidelines are the project's rules and the index is its library");
    }

    [Fact]
    public void EnvelopeCarriesTheTotalThroughToTheOmissionLine()
    {
        var page = Enumerable.Range(0, ProjectDocumentIndexFormatter.MaxRenderedDocuments)
            .Select(i => Text($"id{i:D3}", $"Doc {i:D3}", "body"))
            .Cast<ProjectDocumentEntry>()
            .ToList();

        ProjectDocumentIndexFormatter.FormatProjectEnvelope(null, page, ProjectId, totalCount: 40)
            .Should().Contain("15 more document(s) not listed");
    }

    // ── row -> entry projection ─────────────────────────────────────────────────

    [Fact]
    public void ToEntry_TextDocument_ExposesItsBodyAndCountsItAsItsLength()
    {
        var row = new ProjectDocument
        {
            ProjectId = ProjectId,
            Title = "Spec",
            Kind = ProjectDocumentKind.Text,
            Content = "hello"
        };

        var entry = ProjectDocumentIndexFormatter.ToEntry(row);

        entry.KindLabel.Should().Be("text");
        entry.Content.Should().Be("hello");
        entry.SizeBytes.Should().Be(5);
        entry.FileName.Should().BeNull();
    }

    [Fact]
    public void ToEntry_FileDocument_WithholdsItsBodyAndReportsStoredSize()
    {
        // The projection is what decides whether the index quotes a body. Quoting a file document's
        // Content column would print the empty string every upload stores, and reporting its SizeBytes as
        // the body length would be wrong in the other direction.
        var row = new ProjectDocument
        {
            ProjectId = ProjectId,
            Title = "diagram.png",
            Kind = ProjectDocumentKind.File,
            FileName = "diagram.png",
            ContentType = "image/png",
            SizeBytes = 99,
            Content = string.Empty
        };

        var entry = ProjectDocumentIndexFormatter.ToEntry(row);

        entry.KindLabel.Should().Be("file");
        entry.Content.Should().BeNull("a file's body is not in the row, so there is nothing to excerpt");
        entry.SizeBytes.Should().Be(99);
        entry.Excerpt(ProjectDocumentIndexFormatter.MaxExcerptChars).Should().BeNull();
    }

    [Fact]
    public void ToEntries_SkipsNullsAndSurvivesANullList()
    {
        // Prompt assembly degrades one layer rather than throwing, and a renderer that NREs on a null
        // element would move the failure from "shorter prompt" to "no turn".
        ProjectDocumentIndexFormatter.ToEntries(null).Should().BeEmpty();
        ProjectDocumentIndexFormatter
            .ToEntries([new ProjectDocument { Title = "ok" }, null!])
            .Should().ContainSingle();
    }

    private static int CountEntryLines(string block) => EntryLines(block).Count;

    private static List<string> EntryLines(string block) =>
        block.Split('\n').Where(l => l.StartsWith("* ", StringComparison.Ordinal)).ToList();
}

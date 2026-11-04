using System.Text.Json;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Worker.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The project document library as it reaches an agent through the layered prompt stack: present in the
/// cached Layer 3, absent from the envelope, and byte-stable until the library actually changes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProjectDocumentIndexFormatterTests"/> pins the text of the index in isolation. This file
/// pins the two things only the assembled stack can show: that the index is wired into the layer the
/// cache rules put it in (a formatter nobody calls is a passing unit test and an agent that never hears
/// about its own specs), and that adding a document does not disturb the layers above it.
/// </para>
/// <para>
/// The harness is the one <see cref="PromptStackLayeringTests"/> uses — in-memory context, a stubbed
/// reconciler probe, no database — so these tests run anywhere the suite runs.
/// </para>
/// </remarks>
public class PromptStackDocumentLayerTests
{
    private const string ProjectId = "agent-mesh";
    private static readonly Guid JobId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid SessionId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private readonly string _dbName = "PromptDocs_" + Guid.NewGuid();

    private AgentMeshDbContext Db() => TestDbContextFactory.Create(_dbName);

    private static PromptStackContext Context() => new(
        AgentId: "driver-rook",
        ProjectId: ProjectId,
        JobId: JobId.ToString(),
        TaskId: TaskId.ToString(),
        WorkspacePath: "/workspace/repos/agent-mesh",
        TargetBranch: "feat/layers",
        UserPrompt: "Wire the document library into the prompt.",
        PersonaId: "rook",
        TurnSessionId: SessionId.ToString());

    private static Task<AssembledPromptStack> BuildAsync(AgentMeshDbContext db) =>
        new PromptStackBuilder(
            db,
            new WorkspaceStateRepository(db),
            new ShellWorkspaceReconciler(shellRunner: (_, _, _) =>
                Task.FromResult(new CommandExecutionResult(true, 0, """
                    @@HEADTYPE@@
                    branch
                    @@BRANCH@@
                    feat/layers
                    @@UPSTREAM@@
                    origin/feat/layers
                    @@COUNT@@
                    0
                    @@FILES@@
                    @@STASH@@
                    @@HEAD@@
                    c0ffee1 wire: prompt stack
                    @@DONE@@
                    """))),
            null,
            new AgentMemoryRepository(db))
        .BuildPromptStackAsync(Context());

    private static async Task SeedAsync(AgentMeshDbContext db, bool withDocuments)
    {
        db.Projects.Add(new Project
        {
            Id = ProjectId,
            Name = "Agent Mesh",
            RepositoryName = "agent-mesh",
            GitBaseBranch = "main",
            ProjectInstructions = "Reproduce first, verify always.",
        });

        db.Personas.Add(new Persona
        {
            Id = "rook",
            Name = "Rook",
            Role = "Software Engineer",
            DriverId = "driver-rook",
            PersonaPrompt = "Reproduce first, verify always.",
            TrustTier = TrustTier.Intern
        });

        db.Agents.Add(new Agent
        {
            Id = "driver-rook",
            Name = "Rook Driver",
            RuntimeType = RuntimeType.CliAgy,
            TrustTier = TrustTier.Intern
        });

        db.ProjectJobs.Add(new ProjectJob
        {
            Id = JobId,
            ProjectId = ProjectId,
            Title = "Document library",
            Description = "Surface project documents to agents",
            GitBranchName = "feat/layers",
            Status = ProjectJobStatus.Active,
            TasksJson = JsonSerializer.Serialize(new[]
            {
                new { id = TaskId.ToString(), title = "Wire the layer", assignedPersonaId = "rook" }
            })
        });

        db.AgentSessions.Add(new AgentSession
        {
            Id = SessionId,
            Title = "Prompt stack turn",
            Status = "thinking",
            PersonaId = "rook",
            DriverId = "driver-rook",
            CreatorAgentId = "driver-rook",
            MetadataJson = JsonSerializer.Serialize(new { jobId = JobId.ToString(), projectId = ProjectId }),
        });

        if (withDocuments)
        {
            db.ProjectDocuments.AddRange(
                new ProjectDocument
                {
                    ProjectId = ProjectId,
                    Title = "API contract",
                    Kind = ProjectDocumentKind.Text,
                    Content = "POST /things creates a thing."
                },
                new ProjectDocument
                {
                    ProjectId = ProjectId,
                    Title = "Architecture diagram",
                    Kind = ProjectDocumentKind.File,
                    FileName = "arch.png",
                    ContentType = "image/png",
                    SizeBytes = 20480,
                    S3Key = $"projects/{ProjectId}/documents/x/arch.png"
                });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Layer3_ListsTheProjectLibrary()
    {
        using var db = Db();
        await SeedAsync(db, withDocuments: true);

        var cached = (await BuildAsync(db)).CachedSystemPrompt;

        var layer3 = Slice(cached, "[LAYER 3: PROJECT MANIFEST]", "[LAYER 4:");
        layer3.Should().Contain(ProjectDocumentIndexFormatter.SectionHeader);
        layer3.Should().Contain("API contract");
        layer3.Should().Contain("Architecture diagram");
        layer3.Should().Contain("arch.png");
        layer3.Should().Contain("/documents/", "the agent needs a URL it can actually fetch");
    }

    [Fact]
    public async Task Layer3_SitsAfterInstructionsAndBeforeTheDivider()
    {
        // The index belongs at the end of the project manifest: the conventions come first because they
        // are the rules the documents are written against, and the whole thing must stay inside Layer 3
        // rather than spilling into Layer 4, which would shift the team topology.
        using var db = Db();
        await SeedAsync(db, withDocuments: true);

        var cached = (await BuildAsync(db)).CachedSystemPrompt;

        var instructions = cached.IndexOf("[PROJECT CONVENTIONS & BUILD CONTRACTS]", StringComparison.Ordinal);
        var index = cached.IndexOf(ProjectDocumentIndexFormatter.SectionHeader, StringComparison.Ordinal);
        var layer4 = cached.IndexOf("[LAYER 4:", StringComparison.Ordinal);

        instructions.Should().BeGreaterThan(-1);
        index.Should().BeGreaterThan(instructions);
        layer4.Should().BeGreaterThan(index, "the index is part of the project layer, not a layer of its own");
    }

    [Fact]
    public async Task EmptyLibrary_Layer3IsByteIdenticalToAPreFeatureProject()
    {
        // The property that keeps this deployable: a project that has never had a document must produce
        // the same cached prompt it produced before the feature existed. Otherwise shipping the feature
        // invalidates every deployed agent's prefix cache for no benefit, and every existing layering
        // assertion would need its expectations rewritten.
        using var emptyDb = Db();
        await SeedAsync(emptyDb, withDocuments: false);
        var withoutDocuments = (await BuildAsync(emptyDb)).CachedSystemPrompt;

        using var fullDb = TestDbContextFactory.Create(_dbName + "_full");
        await SeedAsync(fullDb, withDocuments: true);
        var withDocuments = (await BuildAsync(fullDb)).CachedSystemPrompt;

        withoutDocuments.Should().NotContain(ProjectDocumentIndexFormatter.SectionHeader,
            "a header announcing an empty library is a line the agent reads to learn there is nothing to read");

        // And the two differ only by the index block, which is the whole point of putting it in Layer 3:
        // the layers above are untouched by a library edit.
        Slice(withoutDocuments, "[LAYER 1:", "[LAYER 2:").Should().Be(Slice(withDocuments, "[LAYER 1:", "[LAYER 2:"));
        Slice(withoutDocuments, "[LAYER 4:", "[LAYER 5:").Should().Be(Slice(withDocuments, "[LAYER 4:", "[LAYER 5:"));
    }

    [Fact]
    public async Task TheIndexIsNotDuplicatedInTheEnvelope()
    {
        // Layer 6 is the part that survives compaction and is re-sent every turn. Repeating the library
        // there would double the tokens the index costs and, worse, give the agent two copies to disagree
        // with once someone adds a document mid-session.
        using var db = Db();
        await SeedAsync(db, withDocuments: true);

        var assembled = await BuildAsync(db);

        assembled.UserTaskEnvelope.Should().NotContain(ProjectDocumentIndexFormatter.SectionHeader);
        assembled.CachedSystemPrompt.Should().Contain(ProjectDocumentIndexFormatter.SectionHeader);
    }

    [Fact]
    public async Task Layer3_CarriesNoTimestampsFromTheLibrary()
    {
        // Every document row has a CreatedAt and an UpdatedAt. If either reached the cached prefix, the
        // block would change on every read and the layer's cacheability would be gone — with no test
        // failing, because the prompt would still be correct, just expensive.
        using var db = Db();
        await SeedAsync(db, withDocuments: true);

        var cached = (await BuildAsync(db)).CachedSystemPrompt;
        var layer3 = Slice(cached, "[LAYER 3: PROJECT MANIFEST]", "[LAYER 4:");

        layer3.Should().NotMatchRegex(@"\d{4}-\d{2}-\d{2}T", "no ISO timestamps from document rows");
        foreach (var stamp in new[] { DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1) })
        {
            layer3.Should().NotContain(stamp.ToString("O"));
        }
    }

    [Fact]
    public async Task Layer3_BoundsWhatItRendersAndSaysWhatItDropped()
    {
        // A library at the API cap (50) must not put 50 lines in the cached prefix. The index caps, and
        // the omission line is what stops the agent concluding that an unlisted spec does not exist.
        using var db = Db();
        await SeedAsync(db, withDocuments: false);

        for (var i = 0; i < ProjectDocument.MaxDocumentsPerProject; i++)
        {
            db.ProjectDocuments.Add(new ProjectDocument
            {
                ProjectId = ProjectId,
                Title = $"Doc {i:D3}",
                Kind = ProjectDocumentKind.Text,
                Content = $"body {i}"
            });
        }
        await db.SaveChangesAsync();

        var cached = (await BuildAsync(db)).CachedSystemPrompt;
        var layer3 = Slice(cached, "[LAYER 3: PROJECT MANIFEST]", "[LAYER 4:");

        var listed = layer3.Split('\n').Count(l => l.StartsWith("* ", StringComparison.Ordinal));
        listed.Should().Be(ProjectDocumentIndexFormatter.MaxRenderedDocuments,
            "the prompt carries a table of contents, not the library");
        layer3.Should().Contain(
            $"{ProjectDocument.MaxDocumentsPerProject - ProjectDocumentIndexFormatter.MaxRenderedDocuments} more document(s) not listed",
            "the read is bounded, so only the total count can reveal that documents exist beyond the page");
    }

    [Fact]
    public async Task Layer3_SurvivesAnUnreadableLibrary()
    {
        // The standing rule for prompt assembly: a subsystem that is unavailable degrades one layer, it
        // does not fail the turn. Here the library is simply absent (the migration has not run on this
        // pod yet), which is the exact rolling-deploy order that makes this reachable in production.
        using var db = Db();
        await SeedAsync(db, withDocuments: false);

        var assembled = await BuildAsync(db);

        assembled.CachedSystemPrompt.Should().Contain("[LAYER 3: PROJECT MANIFEST]");
        assembled.CachedSystemPrompt.Should().Contain("Reproduce first, verify always.",
            "the rest of the project layer still loads when the library does not");
    }

    [Fact]
    public async Task Layer3AndTheLegacyEnvelopeAgreeOnTheSameLibrary()
    {
        // The drift invariant, asserted across the two REAL code paths rather than between two calls into
        // one helper. Layer 3 is composed by the worker's PromptStackBuilder; the legacy envelope is
        // composed by ProjectDocumentStaging.PrepareAsync, which is exactly what the four Api call sites
        // (ProjectJobsController x2, JobTurnDispatcher, SessionsController) now call. If these two ever
        // differ, an agent on one path is told something different about its project's documents than an
        // agent on the other — and the divergence is invisible until an agent ignores a spec.
        using var db = Db();
        await SeedAsync(db, withDocuments: true);

        var cached = (await BuildAsync(db)).CachedSystemPrompt;

        // Cut at the layer divider rather than at "[LAYER 4:" because the divider belongs to the join, not
        // to Layer 3: slicing to the next marker would compare "\n\n---" against the envelope's absence of
        // one and report drift where the two index blocks are byte-identical.
        var layer3Index = From(cached, ProjectDocumentIndexFormatter.SectionHeader)
            .Split(PromptStackBuilder.LayerDivider)[0].TrimEnd();

        // The instructions string is the one SeedAsync writes on the project row, which is what the Api
        // sites pass through from project.ProjectInstructions.
        var legacy = await AgentMesh.Api.Services.ProjectDocumentStaging.PrepareAsync(
            db, ProjectId, Guid.NewGuid(), projectInstructions: "Reproduce first, verify always.");

        var envelopeIndex = From(legacy.ProjectPreamble, ProjectDocumentIndexFormatter.SectionHeader).TrimEnd();

        envelopeIndex.Should().Be(layer3Index,
            "both prompt paths must describe one library in one set of bytes");
    }

    /// <summary>The text from <paramref name="marker"/> to the end of the string.</summary>
    private static string From(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"expected a section starting with '{marker}'");
        return text[start..];
    }

    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"expected a section starting with '{from}'");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"expected a section ending with '{to}'");
        return text[start..end];
    }
}

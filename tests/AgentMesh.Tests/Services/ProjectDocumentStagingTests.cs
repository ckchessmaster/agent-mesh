using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// Stage-on-demand: a project's uploaded documents are copied into the turn session's attachment
/// directory and named in the turn's attachment payload, while the prompt preamble is rendered from the
/// same read that decided what to copy.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this pins that the controller tests do not.</strong>
/// <see cref="Controllers.ProjectsControllerDocumentTests"/> proves a document's bytes land in the
/// project's document directory. This proves the second half of the feature: that on the next turn those
/// bytes are where the runner looks for them, and that the agent is told the path. Staging bytes nobody
/// mentions is a file an agent finds only by running <c>ls</c> — the behaviour the platform's own prompt
/// layer tells agents not to do.
/// </para>
/// <para>
/// <strong>Why the turn session's directory is asserted directly.</strong> The contract with the CLI is a
/// path: <c>AgentAdapters.FormatAttachedFilesBlock</c> resolves a staged file as
/// <c>.agentmesh/attachments/{id}_{fileName}</c> relative to the session's working tree, and
/// <c>SessionsController.GetAttachmentDirectory</c> is the absolute form of that same directory. Asserting
/// on the file's existence there is asserting on the real integration point; asserting on a mock's
/// received arguments would only prove the call was made.
/// </para>
/// <para>
/// Each test uses a unique project slug and cleans up both the project's document directory and the turn
/// session's attachment directory, because unlike the in-memory database these are process-global paths.
/// </para>
/// </remarks>
public class ProjectDocumentStagingTests : IDisposable
{
    private readonly List<string> _projectDirs = new();
    private readonly List<Guid> _sessionDirs = new();

    private string NewProjectId([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        // The suffix leads so a 64-char truncation (Project.id is character varying(64)) can only eat the
        // readable name, never the part that makes the slug unique. Two tests sharing a slug would have
        // one delete the other's document directory at teardown.
        var slug = $"stage-{Guid.NewGuid():N}"[..20].ToLowerInvariant() + $"-{caller.ToLowerInvariant()}";
        slug = slug.Length > 64 ? slug[..64] : slug;

        _projectDirs.Add(slug);
        return slug;
    }

    private Guid NewTurnSessionId()
    {
        var id = Guid.NewGuid();
        _sessionDirs.Add(id);
        return id;
    }

    /// <summary>
    /// The turn session's attachment directory, computed without creating it.
    /// </summary>
    /// <remarks>
    /// <c>SessionsController.GetAttachmentDirectory</c> creates the directory it names, so calling it to
    /// ask "was this created?" always answers yes. The path is spelled out here instead, mirroring the
    /// production formula.
    /// <para>
    /// That duplication is only safe because the positive test
    /// (<see cref="Prepare_CreatesTheAttachmentDirectoryWhereTheCliLooksForIt"/>) asserts against this same
    /// helper: if the formula drifted from production's, that test would fail. The pair pins the location;
    /// either one alone would not.
    /// </para>
    /// </remarks>
    private static string AttachmentDirWithoutCreating(Guid turnSessionId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentmesh", "artifacts", turnSessionId.ToString(), "attachments");

    public void Dispose()
    {
        foreach (var dir in _projectDirs.Select(ProjectsController.GetDocumentDirectory)
                     .Concat(_sessionDirs.Select(AttachmentDirWithoutCreating)))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                Directory.Delete(dir, recursive: true);

                var parent = Path.GetDirectoryName(dir);
                if (parent is not null && Directory.Exists(parent) &&
                    Directory.GetFileSystemEntries(parent).Length == 0)
                {
                    Directory.Delete(parent);
                }
            }
            catch
            {
                // Teardown noise must not fail a passing test; the next run uses fresh paths.
            }
        }
    }

    private static async Task<(AgentMeshDbContext db, Project project)> SeedAsync(
        string projectId,
        params ProjectDocument[] documents)
    {
        var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Staging Project",
            WorkspaceType = WorkspaceType.Git
        });

        foreach (var document in documents)
        {
            document.ProjectId = projectId;
            db.ProjectDocuments.Add(document);
        }

        await db.SaveChangesAsync();
        return (db, db.Projects.Local.First(p => p.Id == projectId));
    }

    /// <summary>Writes a file into the project's document directory the way the upload endpoint does.</summary>
    private static string WriteSourceFile(string projectId, Guid documentId, string fileName, byte[] bytes)
    {
        var path = ProjectsController.LocalDocumentPath(
            ProjectsController.GetDocumentDirectory(projectId), documentId, fileName);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    private static ProjectDocument FileDoc(string title, string fileName, string contentType = "application/pdf")
    {
        var doc = new ProjectDocument
        {
            Title = title,
            Kind = ProjectDocumentKind.File,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = 4
        };
        doc.S3Key = $"projects/{doc.ProjectId}/documents/{doc.Id:N}/{fileName}";
        return doc;
    }

    private static ProjectDocument TextDoc(string title, string content) => new()
    {
        Title = title,
        Kind = ProjectDocumentKind.Text,
        Content = content
    };

    // ── the happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Prepare_CopiesUploadedDocumentsIntoTheTurnSessionDirectory()
    {
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("API spec", "spec.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x21 };
            WriteSourceFile(projectId, doc.Id, "spec.pdf", bytes);

            await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            var stagedPath = Path.Combine(
                SessionsController.GetAttachmentDirectory(turnSessionId), $"{doc.Id:N}_spec.pdf");

            System.IO.File.Exists(stagedPath).Should().BeTrue(
                "the CLI resolves a staged attachment at .agentmesh/attachments/{id}_{fileName} under the turn session");
            (await System.IO.File.ReadAllBytesAsync(stagedPath)).Should().Equal(bytes);
        }
    }

    [Fact]
    public async Task Prepare_AddsStagedDocumentsToTheAttachmentPayload()
    {
        // The payload is what the CLI renders as [Attached Files in Workspace], naming each file's path.
        // Copying bytes without listing them here would leave the file discoverable only to an agent that
        // thinks to run ls.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("API spec", "spec.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            WriteSourceFile(projectId, doc.Id, "spec.pdf", new byte[] { 1, 2, 3, 4, 5 });

            var prepared = await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            var staged = prepared.Attachments.Should().ContainSingle().Subject;
            staged.Id.Should().Be(doc.Id.ToString("N"));
            staged.FileName.Should().Be("spec.pdf");
            staged.ContentType.Should().Be("application/pdf");
            staged.SizeBytes.Should().Be(4);
            staged.DownloadUrl.Should().Contain($"/documents/{doc.Id:N}/content");
        }
    }

    [Fact]
    public async Task Prepare_KeepsExistingAttachmentsAndAppendsDocuments()
    {
        // A turn already carries the message's own attachments plus the active task's. Documents are the
        // third contributor, and dropping either of the first two would regress job chat.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("API spec", "spec.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            WriteSourceFile(projectId, doc.Id, "spec.pdf", new byte[] { 1 });

            var existing = new List<JobAttachmentDto>
            {
                new("message-att-1", Guid.NewGuid(), "screenshot.png", "image/png", 10, "jobs/x/screenshot.png", "/dl", DateTimeOffset.UtcNow)
            };

            var prepared = await ProjectDocumentStaging.PrepareAsync(
                db, projectId, turnSessionId, existing);

            prepared.Attachments.Should().HaveCount(2);
            prepared.Attachments[0].Id.Should().Be("message-att-1", "the caller's list is preserved in order");
            prepared.Attachments[1].Id.Should().Be(doc.Id.ToString("N"));
        }
    }

    [Fact]
    public async Task Prepare_DoesNotListADocumentTwice()
    {
        // A user who attaches the project's spec.pdf to one message gets it from both routes. Listing it
        // twice renders two identical chips and two identical CLI lines for one file.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("API spec", "spec.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            WriteSourceFile(projectId, doc.Id, "spec.pdf", new byte[] { 1 });

            var existing = new List<JobAttachmentDto>
            {
                new(doc.Id.ToString("N"), Guid.NewGuid(), "spec.pdf", "application/pdf", 4, "k", "/dl", DateTimeOffset.UtcNow)
            };

            var prepared = await ProjectDocumentStaging.PrepareAsync(
                db, projectId, turnSessionId, existing);

            prepared.Attachments.Should().ContainSingle()
                .Which.Id.Should().Be(doc.Id.ToString("N"));
        }
    }

    // ── what is and is not staged ───────────────────────────────────────────────

    [Fact]
    public async Task Prepare_NeverStagesTextDocuments()
    {
        // A text document has no bytes on disk; its body is a column. Materialising one into a file per
        // turn would write a derived artifact the delete path knows nothing about, to duplicate content
        // the prompt already carries as an excerpt.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var (db, _) = await SeedAsync(projectId, TextDoc("Pasted notes", "the whole body"));
        await using (db)
        {
            var prepared = await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            prepared.Attachments.Should().BeEmpty();
            prepared.ProjectPreamble.Should().Contain("Pasted notes", "a text document is referenced, not staged");
            prepared.ProjectPreamble.Should().Contain("excerpt: the whole body");
        }
    }

    [Fact]
    public async Task Prepare_SkipsAFileWhoseBytesAreNotOnThisPod()
    {
        // The row says a file exists; the local copy is on the replica that took the upload. The document
        // stays in the index with its URL, which is exactly where it was before this feature — staging is
        // an optimisation on top of the reference, not a replacement for it.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("Remote spec", "remote.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            var prepared = await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            prepared.Attachments.Should().BeEmpty("nothing was copied");
            prepared.ProjectPreamble.Should().Contain("remote.pdf", "but the agent is still told it exists");
            prepared.ProjectPreamble.Should().Contain("/content", "and given the URL that will fetch it");
        }
    }

    [Fact]
    public async Task Prepare_StagesUpToTheAttachmentCapAcrossTheWholeLibrary()
    {
        // The cap is MaxAttachmentsPerMessage, and it applies to the first N FILES by title across the
        // whole library — not to the first N documents of any kind. A project whose earliest-titled
        // documents are pasted notes must still get spec.pdf staged, or the document an agent most needs
        // is the one never handed to it.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();

        var notes = Enumerable.Range(0, 30)
            .Select(i => TextDoc($"Note {i:D2}", "body"))
            .ToList();

        // "Zz" sorts after all thirty notes, so a naive take-then-filter would drop it.
        var lateSpec = FileDoc("Zz API spec", "late-spec.pdf");

        var (db, _) = await SeedAsync(projectId, notes.Append(lateSpec).ToArray());
        await using (db)
        {
            WriteSourceFile(projectId, lateSpec.Id, "late-spec.pdf", new byte[] { 7, 7 });

            var prepared = await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            prepared.Attachments.Should().ContainSingle()
                .Which.FileName.Should().Be("late-spec.pdf",
                    "the file filter runs in the query, before the cap, so a file deep in title order is still staged");
        }
    }

    [Fact]
    public async Task Prepare_CapsStagedFilesAtThePerTurnAttachmentLimit()
    {
        // A project may hold MaxDocumentsPerProject documents; a turn's payload is capped at
        // MaxAttachmentsPerMessage. Without the cap, every turn of every job in a 50-document project
        // would copy 50 files and render 50 CLI lines.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();

        var files = Enumerable.Range(0, SessionsController.MaxAttachmentsPerMessage + 5)
            .Select(i => FileDoc($"Doc {i:D2}", $"doc{i:D2}.pdf"))
            .ToList();

        var (db, _) = await SeedAsync(projectId, files.ToArray());
        await using (db)
        {
            foreach (var file in files)
            {
                WriteSourceFile(projectId, file.Id, file.FileName!, new byte[] { 1 });
            }

            var prepared = await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            prepared.Attachments.Should().HaveCount(SessionsController.MaxAttachmentsPerMessage);
        }
    }

    // ── the preamble ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prepare_RendersGuidelinesAndIndexTogether()
    {
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("API spec", "spec.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            var prepared = await ProjectDocumentStaging.PrepareAsync(
                db, projectId, turnSessionId, projectInstructions: "Always run scoped tests.");

            prepared.ProjectPreamble.Should().StartWith("[Project Guidelines: Always run scoped tests.]\n");
            prepared.ProjectPreamble.Should().Contain(AgentMesh.Core.Services.ProjectDocumentIndexFormatter.SectionHeader);
            prepared.ProjectPreamble.Should().Contain("spec.pdf");
        }
    }

    [Fact]
    public async Task Prepare_WithNoDocumentsAndNoInstructions_RendersNothing()
    {
        // The property that keeps every pre-existing prompt byte-identical for a project that has never
        // had a document: the legacy envelope sites used to emit "" in exactly this case.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var (db, _) = await SeedAsync(projectId);
        await using (db)
        {
            var prepared = await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            prepared.ProjectPreamble.Should().BeEmpty();
            prepared.Attachments.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Prepare_WithComposePreambleFalse_StillStagesButRendersNoText()
    {
        // The layered-stack path: Layer 3 already carries the index, so the envelope text must not be
        // built and thrown away — but the files are useful on both paths and must still be staged.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("API spec", "spec.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            WriteSourceFile(projectId, doc.Id, "spec.pdf", new byte[] { 3 });

            var prepared = await ProjectDocumentStaging.PrepareAsync(
                db, projectId, turnSessionId, composePreamble: false);

            prepared.ProjectPreamble.Should().BeEmpty();
            prepared.Attachments.Should().ContainSingle().Which.FileName.Should().Be("spec.pdf");
        }
    }

    [Fact]
    public async Task Prepare_PreambleAndStagedFilesComeFromTheSameRead()
    {
        // The invariant the two bounded reads are arranged around: whatever the index lists as staged-
        // eligible, the copy loop sees the same rows. A second independent read could disagree with the
        // first if a document were added between them, and the agent would be told about a file it was
        // never handed.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var docs = new[] { FileDoc("A spec", "a.pdf"), FileDoc("B spec", "b.pdf") };
        var (db, _) = await SeedAsync(projectId, docs);
        await using (db)
        {
            foreach (var doc in docs)
            {
                WriteSourceFile(projectId, doc.Id, doc.FileName!, new byte[] { 1 });
            }

            var prepared = await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            var listedInPreamble = docs.Where(d => prepared.ProjectPreamble.Contains(d.Id.ToString("N")));
            var handedToRunner = prepared.Attachments.Select(a => a.Id);

            foreach (var doc in listedInPreamble)
            {
                handedToRunner.Should().Contain(doc.Id.ToString("N"),
                    $"the preamble advertises {doc.FileName} by id, so the turn must also be handed its bytes");
            }
        }
    }

    // ── degradation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prepare_DoesNotMintAnEmptyAttachmentDirectoryWhenNothingStages()
    {
        // GetAttachmentDirectory creates the directory it names. If staging asked for it before knowing
        // there was a file to copy, every turn of every project whose rows point at another pod's files
        // would leave an empty directory behind — unbounded litter on the turn path, and a session
        // directory tree that implies attachments that never existed.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("Remote spec", "remote.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            // No WriteSourceFile: the row exists, the bytes are on another replica.
            await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            Directory.Exists(AttachmentDirWithoutCreating(turnSessionId)).Should().BeFalse(
                "nothing was copied, so no attachment directory should have been created for this turn");
        }
    }

    [Fact]
    public async Task Prepare_WithAnEmptyLibrary_TouchesNoAttachmentDirectory()
    {
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var (db, _) = await SeedAsync(projectId);
        await using (db)
        {
            await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            Directory.Exists(AttachmentDirWithoutCreating(turnSessionId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Prepare_CreatesTheAttachmentDirectoryWhereTheCliLooksForIt()
    {
        // The mirror of the two tests above: the optimisation must not break the case that needs it.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("API spec", "spec.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            WriteSourceFile(projectId, doc.Id, "spec.pdf", new byte[] { 1 });

            await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId);

            Directory.Exists(AttachmentDirWithoutCreating(turnSessionId)).Should().BeTrue(
                "the non-creating path formula matches what GetAttachmentDirectory actually made");
        }
    }

    [Fact]
    public async Task Prepare_WithAnUnreachableDatabase_DegradesInsteadOfThrowing()
    {
        // The rolling-deploy order that makes this reachable: the API ships a build before the migration
        // runs, so project_documents does not exist yet. Every job turn in every project would otherwise
        // 500 on a table the feature only wanted to be a convenience.
        using var db = TestDbContextFactory.CreateUnreachable();

        var prepared = await ProjectDocumentStaging.PrepareAsync(
            db, "any-project", NewTurnSessionId(), projectInstructions: "Still runs.");

        prepared.Attachments.Should().BeEmpty();
        prepared.ProjectPreamble.Should().Be("[Project Guidelines: Still runs.]\n",
            "the guidelines come from the project row the caller already loaded, so they survive a library outage");
    }

    [Fact]
    public async Task Prepare_WithANullExistingList_StartsFromEmpty()
    {
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var (db, _) = await SeedAsync(projectId);
        await using (db)
        {
            var prepared = await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId, null);

            prepared.Attachments.Should().NotBeNull();
            prepared.Attachments.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Prepare_DoesNotMutateTheCallersAttachmentList()
    {
        // The caller keeps its list for its own metadata and SignalR payloads. Staging into the same
        // List<JobAttachmentDto> instance would mean a turn that is dispatched twice lists documents
        // twice, and the caller's own view of its message would change underneath it.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("API spec", "spec.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            WriteSourceFile(projectId, doc.Id, "spec.pdf", new byte[] { 1 });

            var existing = new List<JobAttachmentDto>
            {
                new("att-1", Guid.NewGuid(), "a.png", "image/png", 1, "k", "/dl", DateTimeOffset.UtcNow)
            };

            var prepared = await ProjectDocumentStaging.PrepareAsync(db, projectId, turnSessionId, existing);

            existing.Should().ContainSingle("the caller's list is input, not an out-parameter");
            prepared.Attachments.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task StageFileDocumentsAsync_IsThePreambleFreeFormOfPrepare()
    {
        // The two entry points must not diverge in what they copy, only in whether they render text.
        var projectId = NewProjectId();
        var turnSessionId = NewTurnSessionId();
        var doc = FileDoc("API spec", "spec.pdf");
        var (db, _) = await SeedAsync(projectId, doc);
        await using (db)
        {
            WriteSourceFile(projectId, doc.Id, "spec.pdf", new byte[] { 9 });

            var attachments = await ProjectDocumentStaging.StageFileDocumentsAsync(
                db, projectId, turnSessionId);

            attachments.Should().ContainSingle().Which.FileName.Should().Be("spec.pdf");
            System.IO.File.Exists(Path.Combine(
                SessionsController.GetAttachmentDirectory(turnSessionId), $"{doc.Id:N}_spec.pdf"))
                .Should().BeTrue();
        }
    }
}
